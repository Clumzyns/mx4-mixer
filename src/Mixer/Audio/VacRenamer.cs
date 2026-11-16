using Mixer.Audio.Com;
using Mixer.Config;
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
        var render = List(enumerator, DataFlow.Render);
        var capture = List(enumerator, DataFlow.Capture);
        var cables = VacCables.Discover(render, capture, config.CableCaptures);
        var renamed = 0;
        foreach (var (channel, name) in Names)
        {
            if (!config.CableAssignments.TryGetValue(channel.Key(), out var renderId)) continue;
            var cable = cables.FirstOrDefault(item => string.Equals(EndpointIds.Key(item.RenderId), EndpointIds.Key(renderId), StringComparison.OrdinalIgnoreCase));
            if (cable == null) continue;
            EndpointRenamer.SetFriendlyName(cable.RenderId, name);
            renamed++;
            if (!string.IsNullOrWhiteSpace(cable.CaptureId))
            {
                EndpointRenamer.SetFriendlyName(cable.CaptureId, name);
                renamed++;
            }
            config.CableAssignments[channel.Key()] = cable.RenderId;
            if (!string.IsNullOrWhiteSpace(cable.CaptureId))
                config.CableCaptures[channel.Key()] = cable.CaptureId;
        }

        config.CableNamesApplied = true;
        ConfigStore.Save(config);
        return renamed;
    }

    public static bool Matches(string? friendlyName, string expected)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return false;
        var label = VacCables.LabelOf(friendlyName);
        return label.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || friendlyName.Trim().StartsWith(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static List<(string Id, string Name)> List(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var list = new List<(string, string)>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            string name;
            try { name = device.FriendlyName; }
            catch { name = device.ID; }
            list.Add((device.ID, name));
            device.Dispose();
        }
        return list;
    }
}
