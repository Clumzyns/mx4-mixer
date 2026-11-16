using System.Runtime.InteropServices;

namespace Mixer.Audio.Com;

internal static class EndpointRenamer
{
    private static readonly Guid EnumeratorId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EnumeratorInterface = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly PropertyKey DeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public static void SetFriendlyName(string deviceId, string name)
    {
        var clsid = EnumeratorId;
        var iid = EnumeratorInterface;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref iid, out var enumeratorPtr);
        if (hr < 0 || enumeratorPtr == IntPtr.Zero)
            Marshal.ThrowExceptionForHR(hr);

        try
        {
            var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(enumeratorPtr);
            enumerator.GetDevice(deviceId, out var device);
            device.OpenPropertyStore(2, out var store);
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
                }
                finally
                {
                    PropVariantClear(ref variant);
                }
            }
            store.Commit();
        }
        finally
        {
            Marshal.Release(enumeratorPtr);
        }
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
