using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;

namespace Mixer.Audio;

/// <summary>
/// VAC line number comes from the endpoint text or the connected KS filter, not from a renamed friendly name.
/// </summary>
internal static partial class VacLine
{
    public static int Read(MMDevice device, MMDeviceEnumerator enumerator)
    {
        var number = FromProperties(device);
        if (number > 0) return number;
        return FromTopology(device, enumerator);
    }

    public static bool TryNumber(string? text, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = LinePattern().Match(text);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out number)) return false;
        return number is > 0 and < 65;
    }

    private static int FromProperties(MMDevice device)
    {
        if (TryNumber(Safe(() => device.FriendlyName), out var number)) return number;
        if (TryNumber(Safe(() => device.DeviceFriendlyName), out number)) return number;
        if (TryNumber(Description(device), out number)) return number;
        try
        {
            var store = device.Properties;
            var count = Math.Min(store.Count, 64);
            for (var index = 0; index < count; index++)
            {
                var key = store.Get(index);
                if (store[key].Value is string text && TryNumber(text, out number))
                    return number;
            }
        }
        catch
        {
            // Some endpoints refuse a full property walk.
        }
        return 0;
    }

    private static int FromTopology(MMDevice device, MMDeviceEnumerator enumerator)
    {
        try
        {
            var topology = device.DeviceTopology;
            var count = Math.Min(topology.ConnectorCount, 8u);
            for (uint index = 0; index < count; index++)
            {
                var connector = topology.GetConnector(index);
                if (!connector.IsConnected) continue;
                var otherId = connector.ConnectedToDeviceId;
                if (string.IsNullOrWhiteSpace(otherId)) continue;
                if (TryNumber(otherId, out var fromId)) return fromId;
                try
                {
                    using var other = enumerator.GetDevice(otherId);
                    var number = FromProperties(other);
                    if (number > 0) return number;
                }
                catch
                {
                    // The connected filter may not be an MMDevice.
                }
            }
        }
        catch
        {
            // Topology is optional. The friendly name is the other source.
        }
        return 0;
    }

    private static string? Description(MMDevice device)
    {
        try
        {
            return device.Properties[PropertyKeys.PKEY_Device_DeviceDesc]?.Value as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? Safe(Func<string> read)
    {
        try { return read(); }
        catch { return null; }
    }

    [GeneratedRegex(@"(?i)\bline\s*(\d+)\b")]
    private static partial Regex LinePattern();
}
