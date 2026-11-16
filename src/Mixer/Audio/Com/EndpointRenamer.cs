using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Mixer.Services;

namespace Mixer.Audio.Com;

internal static class EndpointRenamer
{
    private static readonly Guid EnumeratorId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EnumeratorInterface = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));
    private const uint RegOptionBackupRestore = 0x00000004;
    private const uint KeyWow6464 = 0x0100;
    private const uint KeyAllAccess = 0xF003F;
    private const uint RegSz = 1;
    private const uint RegBinary = 3;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint PrivilegeEnabled = 0x00000002;

    private static readonly PropertyKey DeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly string[] RegistryValues =
    [
        "{a45c254e-df1c-4efd-8020-67d146a850e0},2",
        "{b3f8fa53-0004-438e-9003-51a46e139bfc},6"
    ];

    public static void SetFriendlyName(string deviceId, string name)
    {
        Log.Info($"Rename endpoint {deviceId} -> {name}");
        string registryError;
        try
        {
            if (TryWriteRegistry(deviceId, name))
            {
                RefreshDevices();
                return;
            }
            registryError = "registry write reported failure";
        }
        catch (Exception ex)
        {
            registryError = ex.ToString();
            Log.Error($"Rename registry step failed for {deviceId}", ex);
        }

        Log.Info($"Rename falling back to IPropertyStore for {deviceId}");
        try
        {
            WritePropertyStore(deviceId, name);
            RefreshDevices();
        }
        catch (Exception ex)
        {
            Log.Error($"Rename IPropertyStore step failed for {deviceId}", ex);
            throw new InvalidOperationException(
                "Couldn't rename " + deviceId + ". Registry: " + registryError + " Property store: " + ex.Message, ex);
        }
    }

    private static bool TryWriteRegistry(string deviceId, string name)
    {
        EnablePrivilege("SeBackupPrivilege");
        EnablePrivilege("SeRestorePrivilege");
        if (!TryRegistryPath(deviceId, out var subkey))
            throw new InvalidOperationException("Couldn't map " + deviceId + " to an MMDevices registry key.");

        Log.Info("Rename opening " + subkey);
        var status = RegCreateKeyEx(HkeyLocalMachine, subkey, 0, null, RegOptionBackupRestore,
            KeyAllAccess | KeyWow6464, IntPtr.Zero, out var key, out _);
        if (status != 0 || key == IntPtr.Zero)
            throw new Win32Exception(status, "RegCreateKeyEx failed for " + subkey);

        try
        {
            var wrote = false;
            foreach (var valueName in RegistryValues)
            {
                try
                {
                    WritePropertyValue(key, valueName, name);
                    wrote = true;
                    Log.Info($"Rename wrote {valueName} on {subkey}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Rename failed writing {valueName} on {subkey}", ex);
                }
            }

            var flush = RegFlushKey(key);
            if (flush != 0)
                Log.Error("Rename RegFlushKey returned " + flush);
            return wrote;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static void WritePropertyValue(IntPtr key, string valueName, string name)
    {
        var size = 0;
        var query = RegQueryValueEx(key, valueName, IntPtr.Zero, out var type, null, ref size);
        if (query == 0 && (type == RegSz || type == 2))
        {
            var text = Encoding.Unicode.GetBytes(name + "\0");
            var status = RegSetValueEx(key, valueName, 0, RegSz, text, text.Length);
            if (status != 0) throw new Win32Exception(status, "RegSetValueEx REG_SZ " + valueName);
            return;
        }

        byte[] blob;
        if (query == 0 && type == RegBinary && size > 0)
        {
            var existing = new byte[size];
            var read = size;
            var readStatus = RegQueryValueEx(key, valueName, IntPtr.Zero, out _, existing, ref read);
            if (readStatus != 0) throw new Win32Exception(readStatus, "RegQueryValueEx " + valueName);
            blob = ReplaceBinaryString(existing, name);
        }
        else
        {
            blob = PackString(name);
        }

        var write = RegSetValueEx(key, valueName, 0, RegBinary, blob, blob.Length);
        if (write != 0) throw new Win32Exception(write, "RegSetValueEx REG_BINARY " + valueName);
    }

    private static byte[] ReplaceBinaryString(byte[] existing, string name)
    {
        var payload = Encoding.Unicode.GetBytes(name + "\0");
        var header = 8;
        if (existing.Length >= 8 && existing[0] == 31)
        {
            var declared = BitConverter.ToInt32(existing, 4);
            header = declared > 0 && declared + 8 == existing.Length ? 8 : 4;
        }
        else if (existing.Length >= 4)
        {
            header = 4;
        }

        var blob = new byte[header + payload.Length];
        var keep = Math.Min(header, existing.Length);
        if (keep > 0) Buffer.BlockCopy(existing, 0, blob, 0, keep);
        if (header >= 8)
        {
            blob[0] = 31;
            blob[1] = 0;
            blob[4] = (byte)payload.Length;
            blob[5] = (byte)(payload.Length >> 8);
            blob[6] = (byte)(payload.Length >> 16);
            blob[7] = (byte)(payload.Length >> 24);
        }
        Buffer.BlockCopy(payload, 0, blob, header, payload.Length);
        return blob;
    }

    private static byte[] PackString(string name)
    {
        var payload = Encoding.Unicode.GetBytes(name + "\0");
        var blob = new byte[8 + payload.Length];
        blob[0] = 31;
        blob[4] = (byte)payload.Length;
        blob[5] = (byte)(payload.Length >> 8);
        blob[6] = (byte)(payload.Length >> 16);
        blob[7] = (byte)(payload.Length >> 24);
        Buffer.BlockCopy(payload, 0, blob, 8, payload.Length);
        return blob;
    }

    private static bool TryRegistryPath(string deviceId, out string subkey)
    {
        subkey = "";
        var key = EndpointIds.Key(deviceId);
        string flow;
        if (key.Contains("{0.0.0.", StringComparison.OrdinalIgnoreCase)) flow = "Render";
        else if (key.Contains("{0.0.1.", StringComparison.OrdinalIgnoreCase)) flow = "Capture";
        else return false;
        var split = key.IndexOf("}.{", StringComparison.Ordinal);
        if (split < 0 || split + 2 >= key.Length) return false;
        var guid = key[(split + 2)..];
        if (!guid.StartsWith('{') || !guid.EndsWith('}')) return false;
        subkey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{flow}\{guid}\Properties";
        return true;
    }

    private static void EnablePrivilege(string privilege)
    {
        if (!OpenProcessToken(ProcessHandle(), TokenAdjustPrivileges | TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed for " + privilege);
        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupPrivilegeValue failed for " + privilege);
            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = PrivilegeEnabled
            };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AdjustTokenPrivileges failed for " + privilege);
            var error = Marshal.GetLastWin32Error();
            if (error != 0)
                throw new Win32Exception(error, "Privilege was not assigned: " + privilege);
            Log.Info("Rename enabled " + privilege);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static void RefreshDevices()
    {
        try
        {
            SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "MMDevices", 0x0002, 1000, out _);
            Log.Info("Rename broadcast a settings change so Windows can reload endpoint names.");
        }
        catch (Exception ex)
        {
            Log.Error("Rename refresh broadcast failed. The new name is stored and usually appears without restarting Windows Audio.", ex);
        }
    }

    private static void WritePropertyStore(string deviceId, string name)
    {
        var clsid = EnumeratorId;
        var iid = EnumeratorInterface;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref iid, out var enumeratorPtr);
        if (hr < 0 || enumeratorPtr == IntPtr.Zero)
            Marshal.ThrowExceptionForHR(hr);

        try
        {
            Log.Info("Rename IPropertyStore open " + deviceId);
            var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(enumeratorPtr);
            enumerator.GetDevice(deviceId, out var device);
            device.OpenPropertyStore(2, out var store);
            var wrote = false;
            foreach (var key in new[] { DeviceDesc, FriendlyName })
            {
                var property = key;
                var variant = new PropVariant
                {
                    vt = 31,
                    pointer = Marshal.StringToCoTaskMemUni(name)
                };
                try
                {
                    store.SetValue(ref property, ref variant);
                    store.Commit();
                    wrote = true;
                    Log.Info($"Rename IPropertyStore committed pid {key.PropertyId} on {deviceId}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Rename IPropertyStore pid {key.PropertyId} failed on {deviceId}", ex);
                }
                finally
                {
                    PropVariantClear(ref variant);
                }
            }
            if (!wrote)
                throw new UnauthorizedAccessException("IPropertyStore rejected the endpoint name for " + deviceId);
        }
        finally
        {
            Marshal.Release(enumeratorPtr);
        }
    }

    private static IntPtr ProcessHandle() => System.Diagnostics.Process.GetCurrentProcess().Handle;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegCreateKeyEx(IntPtr hKey, string lpSubKey, int reserved, string? lpClass, uint dwOptions, uint samDesired, IntPtr security, out IntPtr phkResult, out uint disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueEx(IntPtr hKey, string lpValueName, IntPtr reserved, out uint type, byte[]? data, ref int size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueEx(IntPtr hKey, string lpValueName, int reserved, uint type, byte[] data, int size);

    [DllImport("advapi32.dll")]
    private static extern int RegFlushKey(IntPtr hKey);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges privileges, uint length, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(uint access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        uint GetState();
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, IntPtr value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
