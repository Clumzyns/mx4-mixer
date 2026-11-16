using Mixer.Config;

namespace Mixer.Audio;

/// <summary>
/// Paints the mix onto the Nova Pro OLED. HID writes stay on the ChatMix read thread.
/// </summary>
public sealed class BaseStationDisplay : IDisposable
{
    private readonly MixerConfig _config;
    private readonly ChatMixMonitor _chat;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private bool _disposed;
    private bool _enabled;
    private bool _released = true;
    private OledScreen.Picture _last;
    private bool _hasLast;

    public BaseStationDisplay(MixerConfig config, ChatMixMonitor chat)
    {
        _config = config;
        _chat = chat;
        _enabled = config.CustomDisplay;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Mixer.Oled"
        };
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
        while (!_disposed)
        {
            _wake.Wait(50);
            if (_disposed) break;
            try
            {
                if (!Volatile.Read(ref _enabled))
                {
                    Release();
                    continue;
                }

                var picture = Read();
                if (_hasLast && picture.Equals(_last)) continue;
                var generation = _chat.SubmitOled(OledScreen.Pack(picture));
                _chat.WaitOled(generation, 200);
                _last = picture;
                _hasLast = true;
                _released = false;
            }
            catch (Exception ex)
            {
                Services.Log.Error("OLED frame failed", ex);
            }
        }

        try { Release(); } catch (Exception ex) { Services.Log.Error("OLED release failed", ex); }
    }

    private void Release()
    {
        if (_released) return;
        var generation = _chat.SubmitOled([OledScreen.ReturnToUi()]);
        _chat.WaitOled(generation, 400);
        _released = true;
        _hasLast = false;
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
