using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mixer.Audio;

namespace Mixer.Services;

public readonly record struct ProcessInfo(string ExeKey, string DisplayName, string? Path);

public static class ProcessPath
{
    private const uint QueryLimited = 0x1000;

    public static ProcessInfo Describe(uint pid, bool systemSounds)
    {
        if (systemSounds || pid == 0)
            return new ProcessInfo("system.sounds", "System Sounds", null);

        var path = Query(pid);
        if (string.IsNullOrWhiteSpace(path))
            return new ProcessInfo($"pid:{pid}", $"Process {pid}", null);

        var file = System.IO.Path.GetFileName(path).ToLowerInvariant();
        return new ProcessInfo(file, NiceName(file, path), path);
    }

    public static string? Query(uint pid)
    {
        var handle = OpenProcess(QueryLimited, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size))
                return null;
            var path = buffer.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string NiceName(string exeKey, string path)
    {
        if (DefaultApps.Names.TryGetValue(exeKey, out var known))
            return known;
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description) && description.Length <= 42 && description.IndexOfAny(new[] { '\\', '/', '%', '@' }) < 0)
                return description.Trim();
        }
        catch
        {
            // Fall through to the file name.
        }

        var stem = System.IO.Path.GetFileNameWithoutExtension(exeKey);
        if (string.IsNullOrWhiteSpace(stem)) return exeKey;
        return char.ToUpperInvariant(stem[0]) + stem[1..];
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
