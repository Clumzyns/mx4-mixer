using Mixer.Config;
using Mixer.Services;

namespace Mixer.Audio;

/// <summary>
/// Paints the Nova Pro OLED. The mix shows while a dial or fader is moving.
/// After a few idle seconds the screen shows the song Spotify is playing.
/// HID writes stay on the ChatMix read thread.
/// </summary>
public sealed class BaseStationDisplay : IDisposable
{
    /// <summary>How long the mix stays up after the last change before the song returns.</summary>
    private const int IdleMs = 5000;
    private const int SongPollMs = 1000;
    /// <summary>The base station draws its own screen over ours after a dial turn, so the frame is sent again on this interval.</summary>
    private const int RefreshMs = 1000;

    private readonly MixerConfig _config;
    private readonly ChatMixMonitor _chat;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private bool _disposed;
    private bool _enabled;
    private bool _showSong;
    private bool _released = true;
    private OledScreen.Picture _mix;
    private bool _hasMix;
    private long _lastChange;
    private object? _shown;
    private byte[][]? _shownPackets;
    private readonly SongScreen _songScreen = new();
    private ScreenSong? _onScreen;
    private SpotifySong.Track? _song;
    private long _songRead = -SongPollMs;
    private long _sentAt;
    private bool _songFailed;

    public BaseStationDisplay(MixerConfig config, ChatMixMonitor chat)
    {
        _config = config;
        _chat = chat;
        _enabled = config.CustomDisplay;
        _showSong = config.ShowSongOnDisplay;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Mixer.Oled"
        };
        // The song screen is drawn with WPF text, which needs an STA thread.
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set
        {
            Volatile.Write(ref _enabled, value);
            _wake.Set();
        }
    }

    public bool ShowSong
    {
        get => Volatile.Read(ref _showSong);
        set
        {
            Volatile.Write(ref _showSong, value);
            _wake.Set();
        }
    }

    /// <summary>The song the base station screen is showing right now, or null while it shows the mix.</summary>
    public ScreenSong? SongOnScreen => Volatile.Read(ref _onScreen);

    public void Start() => _thread.Start();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volatile.Write(ref _enabled, false);
        _wake.Set();
        try { _thread.Join(800); } catch { /* background */ }
        _wake.Dispose();
    }

    private void Loop()
    {
        _lastChange = Environment.TickCount64;
        while (!_disposed)
        {
            // A short wait keeps scrolling smooth while the song is up.
            _wake.Wait(_shown is SongScreen.Frame ? 25 : 50);
            _wake.Reset();
            if (_disposed) break;
            try
            {
                if (!Volatile.Read(ref _enabled))
                {
                    Release();
                    continue;
                }

                var now = Environment.TickCount64;
                var mix = Read();
                if (!_hasMix || !mix.Equals(_mix))
                {
                    if (_hasMix) _lastChange = now;
                    _mix = mix;
                    _hasMix = true;
                }

                SpotifySong.Track? song = null;
                if (Volatile.Read(ref _showSong) && now - _lastChange >= IdleMs)
                    song = Song(now);

                // The frame changes as the text scrolls and the clock ticks.
                SongScreen.Frame? frame = song is { } track ? SongFrame(track, now) : null;
                object wanted = frame is { } next ? next : mix;
                if (wanted.Equals(_shown) && now - _sentAt < RefreshMs) continue;

                // A refresh of the same screen reuses the packed frame.
                var packets = wanted.Equals(_shown) ? _shownPackets : null;
                if (packets == null && frame is { } drawn) packets = PackSong(drawn);
                if (packets == null)
                {
                    wanted = mix;
                    if (wanted.Equals(_shown) && now - _sentAt < RefreshMs) continue;
                    packets = OledScreen.Pack(mix);
                }

                var generation = _chat.SubmitOled(packets);
                _chat.WaitOled(generation, 200);
                _shown = wanted;
                _shownPackets = packets;
                Volatile.Write(ref _onScreen, wanted is SongScreen.Frame && song is { } showing
                    ? new ScreenSong(showing.Title, showing.Artist, showing.Position, showing.Duration, showing.UpdatedAt)
                    : null);
                _sentAt = now;
                _released = false;
            }
            catch (Exception ex)
            {
                Log.Error("OLED frame failed", ex);
            }
        }

        try { Release(); } catch (Exception ex) { Log.Error("OLED release failed", ex); }
    }

    private SpotifySong.Track? Song(long now)
    {
        if (now - _songRead < SongPollMs) return _song;
        _songRead = now;
        try
        {
            _song = SpotifySong.Playing();
        }
        catch (Exception ex)
        {
            _song = null;
            if (!_songFailed) Log.Error("Spotify song lookup failed", ex);
            _songFailed = true;
        }
        return _song;
    }

    private SongScreen.Frame? SongFrame(SpotifySong.Track track, long now)
    {
        try
        {
            // Coming back from the mix starts the scroll at the beginning of the title.
            if (_shown is not SongScreen.Frame) _songScreen.Restart(now);
            return _songScreen.Next(track, now, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            SongFailed(ex);
            return null;
        }
    }

    private byte[][]? PackSong(SongScreen.Frame frame)
    {
        try
        {
            return OledScreen.Pack(_songScreen.Pixels(frame));
        }
        catch (Exception ex)
        {
            SongFailed(ex);
            return null;
        }
    }

    private void SongFailed(Exception ex)
    {
        if (!_songFailed) Log.Error("Song screen failed. Showing the mix instead.", ex);
        _songFailed = true;
        Volatile.Write(ref _showSong, false);
    }

    private void Release()
    {
        if (_released) return;
        var generation = _chat.SubmitOled([OledScreen.ReturnToUi()]);
        _chat.WaitOled(generation, 400);
        _released = true;
        _shown = null;
        _shownPackets = null;
        Volatile.Write(ref _onScreen, null);
    }

    private OledScreen.Picture Read()
    {
        var (gameMix, chatMix) = ChatMixMath.ToLevels(_config.ChatMixBalance);
        return new OledScreen.Picture(
            Percent(_config.MasterVolume),
            gameMix,
            chatMix,
            Percent(_config.Channel(ChannelId.Game).Volume),
            Percent(_config.Channel(ChannelId.Chat).Volume),
            Percent(_config.Channel(ChannelId.Media).Volume),
            Percent(_config.Channel(ChannelId.Aux).Volume),
            _config.Channel(ChannelId.Game).Muted,
            _config.Channel(ChannelId.Chat).Muted,
            _config.Channel(ChannelId.Media).Muted,
            _config.Channel(ChannelId.Aux).Muted);
    }

    private static int Percent(float volume) =>
        (int)Math.Round(Math.Clamp(volume, 0f, 1f) * 100f, MidpointRounding.AwayFromZero);
}

/// <summary>Position is what Spotify reported at UpdatedAt. Duration is zero when the length is unknown.</summary>
public sealed record ScreenSong(string Title, string Artist, TimeSpan Position, TimeSpan Duration, DateTimeOffset UpdatedAt)
{
    public bool HasTimeline => Duration > TimeSpan.Zero;

    public TimeSpan PositionAt(DateTimeOffset now)
    {
        var position = Position + (now - UpdatedAt);
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        return position > Duration ? Duration : position;
    }
}
