namespace Mixer;

/// <summary>
/// The product name lives here so the window, tray, config folder, and registry key stay in sync.
/// </summary>
public static class AppInfo
{
    public const string Name = "Mixer";
    public const string Version = "1.0.0";
    public const string MutexName = @"Local\Mixer.AudioMixer.SingleInstance";
    public const string ShowEventName = @"Local\Mixer.AudioMixer.Show";
}
