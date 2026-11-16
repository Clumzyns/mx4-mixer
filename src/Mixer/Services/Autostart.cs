using System.Diagnostics;
using Microsoft.Win32;

namespace Mixer.Services;

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("The Windows startup registry key is not available.");

        if (!enabled)
        {
            key.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
            return;
        }

        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            path = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Mixer could not find its own executable path.");

        key.SetValue(AppInfo.Name, $"\"{path}\" --tray");
    }
}
