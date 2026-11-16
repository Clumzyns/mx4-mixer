using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MediaControl = global::Windows.Media.Control;

namespace Mixer.Services;

/// <summary>
/// Reads the track Spotify is playing. The Windows media session gives the title, artist,
/// position and length. If Spotify has no media session, the window title gives title and artist only:
/// Spotify titles its main window "Artist - Title" while a track plays, even when hidden in the tray.
/// </summary>
internal static class SpotifySong
{
    /// <summary>Position is what Spotify reported at UpdatedAt. Duration is zero when the length is unknown.</summary>
    public readonly record struct Track(string Title, string Artist, TimeSpan Position, TimeSpan Duration, DateTimeOffset UpdatedAt)
    {
        public bool HasTimeline => Duration > TimeSpan.Zero;

        /// <summary>Spotify only reports the position on play, pause, seek and track change, so the clock fills the gap.</summary>
        public TimeSpan PositionAt(DateTimeOffset now)
        {
            var position = Position + (now - UpdatedAt);
            if (position < TimeSpan.Zero) return TimeSpan.Zero;
            return position > Duration ? Duration : position;
        }
    }

    private static MediaControl.GlobalSystemMediaTransportControlsSessionManager? _manager;
    private static bool _sessionFailed;

    /// <summary>The track Spotify is playing, or null when it is paused, closed, or playing an ad.</summary>
    public static Track? Playing()
    {
        if (!_sessionFailed)
        {
            try
            {
                // WinRT calls run on a pool thread so they never wait on the caller's STA.
                var task = Task.Run(FromMediaSession);
                if (task.Wait(1500))
                {
                    var (found, track) = task.Result;
                    if (found) return track;
                }
            }
            catch (Exception ex)
            {
                _sessionFailed = true;
                Log.Error("Windows media session unavailable. Reading the Spotify window title instead.", ex);
            }
        }
        return FromWindowTitle();
    }

    private static (bool Found, Track? Track) FromMediaSession()
    {
        _manager ??= MediaControl.GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
        foreach (var session in _manager.GetSessions())
        {
            var app = session.SourceAppUserModelId ?? "";
            if (!app.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) continue;

            var status = session.GetPlaybackInfo()?.PlaybackStatus;
            if (status != MediaControl.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                return (true, null);

            var media = session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            var title = media?.Title?.Trim() ?? "";
            if (title.Length == 0) return (true, null);
            var artist = media?.Artist?.Trim() ?? "";

            var timeline = session.GetTimelineProperties();
            var duration = timeline.EndTime - timeline.StartTime;
            var updated = timeline.LastUpdatedTime;
            if (duration <= TimeSpan.Zero || updated.Year < 2000)
                return (true, new Track(title, artist, TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.UtcNow));
            return (true, new Track(title, artist, timeline.Position - timeline.StartTime, duration, updated));
        }
        return (false, null);
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);

    private static Track? FromWindowTitle()
    {
        var pids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("Spotify"))
        {
            try { pids.Add((uint)process.Id); }
            catch { /* exited */ }
            finally { process.Dispose(); }
        }
        if (pids.Count == 0) return null;

        Track? found = null;
        var name = new StringBuilder(64);
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (!pids.Contains(pid)) return true;
            var length = GetWindowTextLength(window);
            if (length <= 0) return true;
            name.Clear();
            GetClassName(window, name, name.Capacity);
            if (!name.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return true;
            var text = new StringBuilder(length + 1);
            GetWindowText(window, text, text.Capacity);
            found = Parse(text.ToString());
            return found == null;
        }, IntPtr.Zero);
        return found;
    }

    internal static Track? Parse(string title)
    {
        title = title.Trim();
        if (title.Length == 0 || title.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase)) return null;
        var split = title.IndexOf(" - ", StringComparison.Ordinal);
        if (split <= 0 || split + 3 >= title.Length) return null;
        return new Track(title[(split + 3)..].Trim(), title[..split].Trim(), TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.UtcNow);
    }
}
