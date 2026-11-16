using System.Runtime.InteropServices;

namespace Mixer.Audio.Com;

internal static class HString
{
    public static IntPtr Create(string? value)
    {
        if (string.IsNullOrEmpty(value)) return IntPtr.Zero;
        var hr = WindowsCreateString(value, (uint)value.Length, out var handle);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return handle;
    }

    public static void Delete(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            WindowsDeleteString(handle);
    }

    public static string? Read(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        var raw = WindowsGetStringRawBuffer(handle, out var length);
        if (raw == IntPtr.Zero || length == 0) return null;
        return Marshal.PtrToStringUni(raw, (int)length);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}

/// <summary>
/// Sets the Windows default render or capture endpoint for the console, multimedia,
/// and communications roles. Same PolicyConfigClient CLSID EarTrumpet and SoundSwitch use.
/// The device id itself says whether the endpoint is playback or capture.
/// </summary>
internal static class DefaultEndpoint
{
    private static readonly Guid Clsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    // Win7+ / Win10 RS1+ / Win11, then the Win10 variant, then Vista. Same method order on all three.
    private static readonly Guid[] InterfaceIds =
    [
        new("F8679F50-850A-41CF-9C72-430F290290C8"),
        new("8F9FB2AA-1C0B-4D54-B6BB-B2F2A10CE03C"),
        new("568B9108-44BF-40B4-9006-86AFE5B5A620")
    ];

    // IUnknown occupies slots 0-2. SetDefaultEndpoint is the 11th interface method: slot 13.
    private const int SetDefaultEndpointSlot = 13;

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SetDefaultEndpointDelegate(
        IntPtr self,
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        uint role);

    public static void Set(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("A device id is required.", nameof(deviceId));

        var errors = new List<string>();
        foreach (var candidate in InterfaceIds)
        {
            var clsid = Clsid;
            var iid = candidate;
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref iid, out var instance);
            if (hr < 0 || instance == IntPtr.Zero)
            {
                errors.Add($"{candidate:D} 0x{hr:X8}");
                continue;
            }

            try
            {
                var fn = ReadSlot<SetDefaultEndpointDelegate>(instance, SetDefaultEndpointSlot);
                for (uint role = 0; role < 3; role++)
                {
                    var roleHr = fn(instance, deviceId, role);
                    if (roleHr < 0) Marshal.ThrowExceptionForHR(roleHr);
                }
                return;
            }
            catch (Exception ex)
            {
                errors.Add($"{candidate:D} {ex.Message}");
            }
            finally
            {
                Marshal.Release(instance);
            }
        }

        throw new InvalidOperationException("PolicyConfig rejected the default device. " + string.Join(" | ", errors));
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    private static T ReadSlot<T>(IntPtr instance, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(instance);
        var method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }
}

/// <summary>
/// Per-app default endpoint. Undocumented but stable WinRT factory
/// Windows.Media.Internal.AudioPolicyConfig, activated from AudioSes.dll the way SoundSwitch does
/// for an unpackaged exe. Slots match EarTrumpet: 19 stubs, then Set at vtable index 25.
/// </summary>
internal sealed class AppEndpointRouter : IDisposable
{
    private const int SetSlot = 25;
    private const int GetSlot = 26;
    private IntPtr _factory;
    private readonly Dictionary<string, int> _logged = new(StringComparer.Ordinal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr self, ref Guid iid, out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetEndpointDelegate(IntPtr self, uint processId, int flow, uint role, IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetEndpointDelegate(IntPtr self, uint processId, int flow, uint role, out IntPtr deviceId);

    [DllImport("AudioSes.dll", PreserveSig = true)]
    private static extern int DllGetActivationFactory(IntPtr classId, out IntPtr factory);

    private AppEndpointRouter(IntPtr factory) => _factory = factory;

    public static AppEndpointRouter? TryCreate()
    {
        var version = Environment.OSVersion.Version;
        if (version.Major < 10 || version.Build < 17134)
        {
            Services.Log.Info($"Per-app routing needs Windows 10 1803 or later. This build is {version}.");
            return null;
        }

        IntPtr className = IntPtr.Zero;
        IntPtr raw = IntPtr.Zero;
        try
        {
            className = HString.Create("Windows.Media.Internal.AudioPolicyConfig");
            var hr = DllGetActivationFactory(className, out raw);
            if (hr < 0 || raw == IntPtr.Zero)
                throw new InvalidOperationException($"DllGetActivationFactory returned 0x{hr:X8}.");

            var qi = ReadSlot<QueryInterfaceDelegate>(raw, 0);
            var errors = new List<string>();
            foreach (var candidate in IidOrder())
            {
                var iid = candidate;
                var qiHr = qi(raw, ref iid, out var policy);
                if (qiHr < 0 || policy == IntPtr.Zero)
                {
                    errors.Add($"{candidate:D} 0x{qiHr:X8}");
                    continue;
                }

                Marshal.Release(raw);
                raw = IntPtr.Zero;
                Services.Log.Info($"Per-app routing using AudioPolicyConfig {candidate:D}.");
                return new AppEndpointRouter(policy);
            }

            throw new InvalidOperationException("AudioPolicyConfig interface was not found. " + string.Join(" | ", errors));
        }
        catch (Exception ex)
        {
            Services.Log.Error("Per-app routing is unavailable. Channel volume still works.", ex);
            if (raw != IntPtr.Zero) Marshal.Release(raw);
            return null;
        }
        finally
        {
            HString.Delete(className);
        }
    }

    public bool SetEndpoint(uint processId, string? deviceId)
    {
        if (_factory == IntPtr.Zero || processId == 0) return false;
        var handle = HString.Create(deviceId);
        try
        {
            var set = ReadSlot<SetEndpointDelegate>(_factory, SetSlot);
            var multimediaOk = false;
            foreach (uint role in new uint[] { 0, 1, 2 })
            {
                var hr = set(_factory, processId, 0, role, handle);
                if (role == 1) multimediaOk = hr >= 0;
                if (hr < 0) LogFailure(processId, deviceId, role, hr);
            }
            return multimediaOk;
        }
        catch (Exception ex)
        {
            Services.Log.Error($"SetPersistedDefaultAudioEndpoint pid {processId}", ex);
            return false;
        }
        finally
        {
            HString.Delete(handle);
        }
    }

    public string? TryGetEndpoint(uint processId)
    {
        if (_factory == IntPtr.Zero || processId == 0) return null;
        try
        {
            var get = ReadSlot<GetEndpointDelegate>(_factory, GetSlot);
            var hr = get(_factory, processId, 0, 1, out var handle);
            if (hr < 0 || handle == IntPtr.Zero) return null;
            try { return HString.Read(handle); }
            finally { HString.Delete(handle); }
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_factory == IntPtr.Zero) return;
        Marshal.Release(_factory);
        _factory = IntPtr.Zero;
    }

    private void LogFailure(uint processId, string? deviceId, uint role, int hr)
    {
        var key = processId + "|" + (deviceId ?? "default") + "|" + role + "|" + hr.ToString("X8");
        var count = _logged.TryGetValue(key, out var seen) ? seen + 1 : 1;
        _logged[key] = count;
        if (count <= 2)
            Services.Log.Info($"SetPersistedDefaultAudioEndpoint pid {processId} role {role} -> 0x{hr:X8} ({deviceId ?? "default"})");
    }

    private static IEnumerable<Guid> IidOrder()
    {
        var modern = new Guid("AB3D4648-E242-459F-B02F-541C70306324");
        var downlevel = new Guid("2A59116D-6C4F-45E0-A74F-707E3FEF9258");
        var legacy = new Guid("32AA8E18-6496-4E24-9F94-B800E7ECCC45");
        if (Environment.OSVersion.Version.Build >= 21390)
            return [modern, downlevel, legacy];
        return [downlevel, modern, legacy];
    }

    private static T ReadSlot<T>(IntPtr instance, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(instance);
        var method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }
}
