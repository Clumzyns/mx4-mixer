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

    public readonly record struct Node(uint Pid, uint Parent, string ExeKey);

    public static IReadOnlyList<Node> Snapshot()
    {
        var list = new List<Node>();
        var snap = CreateToolhelp32Snapshot(2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry)) return list;
            do
            {
                var exe = string.IsNullOrWhiteSpace(entry.szExeFile)
                    ? ""
                    : entry.szExeFile.Trim().ToLowerInvariant();
                list.Add(new Node(entry.th32ProcessID, entry.th32ParentProcessID, exe));
                entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            }
            while (Process32Next(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
        return list;
    }

    public static IEnumerable<uint> Descendants(uint root, IReadOnlyList<Node> nodes)
    {
        var children = new Dictionary<uint, List<uint>>();
        foreach (var node in nodes)
        {
            if (node.Pid == 0 || node.Parent == node.Pid) continue;
            if (!children.TryGetValue(node.Parent, out var list))
            {
                list = [];
                children[node.Parent] = list;
            }
            list.Add(node.Pid);
        }

        var seen = new HashSet<uint>();
        var queue = new Queue<uint>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (!children.TryGetValue(pid, out var kids)) continue;
            foreach (var child in kids)
            {
                if (!seen.Add(child)) continue;
                yield return child;
                queue.Enqueue(child);
            }
        }
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
