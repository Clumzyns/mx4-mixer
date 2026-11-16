using Mixer.Audio.Com;
using Mixer.Config;
using Mixer.Services;
using NAudio.CoreAudioApi;

namespace Mixer.Audio;

internal static class VacRenamer
{
    public static readonly (ChannelId Channel, string Name)[] Names =
    [
        (ChannelId.Game, "Mixer - Game"),
        (ChannelId.Chat, "Mixer - Chat"),
        (ChannelId.Media, "Mixer - Media"),
        (ChannelId.Aux, "Mixer - Aux")
    ];

    public static int RenameAssigned()
    {
        var config = ConfigStore.Load();
        using var enumerator = new MMDeviceEnumerator();
        var render = List(enumerator);
        var capture = List(enumerator, DataFlow.Capture);
        var cables = VacCables.Discover(render, capture, config.CableCaptures);
        var renamed = 0;
        foreach (var (channel, name) in Names)
        {
            var key = channel.Key();
            var line = config.CableLines.TryGetValue(key, out var saved) ? saved : 0;
            var cable = line > 0
                ? cables.FirstOrDefault(item => item.Order == line)
                : null;
            if (cable == null && config.CableAssignments.TryGetValue(key, out var renderId))
                cable = cables.FirstOrDefault(item => string.Equals(EndpointIds.Key(item.RenderId), EndpointIds.Key(renderId), StringComparison.OrdinalIgnoreCase));
            if (cable == null) continue;
            if (cable.Order is > 0 and < 1000)
                config.CableLines[key] = cable.Order;
            config.CableAssignments[key] = cable.RenderId;
            if (!string.IsNullOrWhiteSpace(cable.CaptureId))
                config.CableCaptures[key] = cable.CaptureId;
            if (RenameOne(enumerator, channel, cable.RenderId, name)) renamed++;
            if (!string.IsNullOrWhiteSpace(cable.CaptureId) && RenameOne(enumerator, channel, cable.CaptureId, name))
                renamed++;
        }

        ConfigStore.Save(config);
        return renamed;
    }

    private static bool RenameOne(MMDeviceEnumerator enumerator, ChannelId channel, string id, string name)
    {
        var old = ReadName(enumerator, id);
        try
        {
            EndpointRenamer.SetFriendlyName(id, name);
            var read = ReadName(enumerator, id);
            var ok = Matches(read, name);
            Log.Info($"Rename {channel} {id} old='{old}' new='{name}' read='{read}' result={(ok ? "ok" : "mismatch")}");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error($"Rename {channel} {id} old='{old}' new='{name}' failed", ex);
            return false;
        }
    }

    private static string ReadName(MMDeviceEnumerator enumerator, string id)
    {
        try
        {
            using var device = enumerator.GetDevice(id);
            return device.FriendlyName;
        }
        catch
        {
            return "";
        }
    }

    public static bool Matches(string? friendlyName, string expected)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return false;
        var label = VacCables.LabelOf(friendlyName);
        return label.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || friendlyName.Trim().StartsWith(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static List<(string Id, string Name, int Line)> List(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var list = new List<(string, string, int)>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            string name;
            try { name = device.FriendlyName; }
            catch { name = device.ID; }
            var line = VacLine.Read(device, enumerator);
            list.Add((device.ID, name, line));
            device.Dispose();
        }
        return list;
    }

    private static List<(string Id, string Name, int Line)> List(MMDeviceEnumerator enumerator)
        => List(enumerator, DataFlow.Render);
}
