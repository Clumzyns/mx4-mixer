namespace Mixer.Audio;

/// <summary>
/// Finds Eugene Muzychenko Virtual Audio Cable lines.
/// Each line is a render endpoint apps play into and a capture endpoint Mixer records.
/// </summary>
internal static class VacCables
{
    public static IReadOnlyList<VacCable> Discover(
        IEnumerable<(string Id, string Name)> render,
        IEnumerable<(string Id, string Name)> capture)
    {
        var playback = new Dictionary<string, (string Id, string Name, int Order)>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in render)
        {
            if (!TryKey(device.Name, out var key, out var order)) continue;
            playback[key] = (device.Id, device.Name, order);
        }

        var recording = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in capture)
        {
            if (!TryKey(device.Name, out var key, out _)) continue;
            recording[key] = device.Id;
        }

        var cables = new List<VacCable>();
        foreach (var (key, play) in playback)
        {
            recording.TryGetValue(key, out var captureId);
            cables.Add(new VacCable
            {
                Key = key,
                RenderId = play.Id,
                CaptureId = captureId,
                Label = LabelOf(play.Name),
                Order = play.Order
            });
        }

        return cables.OrderBy(cable => cable.Order).ThenBy(cable => cable.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool TryKey(string friendlyName, out string key, out int order)
    {
        key = "";
        order = 1000;
        if (string.IsNullOrWhiteSpace(friendlyName)) return false;
        var text = friendlyName.Trim();
        var lower = text.ToLowerInvariant();
        var vac = lower.Contains("virtual audio cable") || lower.StartsWith("mixer - ");
        if (!vac) return false;
        key = LabelOf(text).ToLowerInvariant();
        order = OrderOf(key);
        return key.Length > 0;
    }

    public static string LabelOf(string friendlyName)
    {
        var paren = friendlyName.IndexOf('(');
        var head = (paren > 0 ? friendlyName[..paren] : friendlyName).Trim();
        return head.Length == 0 ? friendlyName.Trim() : head;
    }

    private static int OrderOf(string key)
    {
        var parts = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && int.TryParse(parts[^1], out var number))
            return number;
        return 1000;
    }
}

internal sealed class VacCable
{
    public required string Key { get; init; }
    public required string RenderId { get; init; }
    public string? CaptureId { get; init; }
    public required string Label { get; init; }
    public int Order { get; init; }
}
