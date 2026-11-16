using Mixer.Audio;

namespace Mixer.Config;

public sealed class ChannelSettings
{
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
    public string? DeviceId { get; set; }
}

public sealed class MixerConfig
{
    public int Version { get; set; } = 1;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool ChatMixEnabled { get; set; } = true;
    /// <summary>0 is Game only, 100 is Chat only, 50 keeps both full.</summary>
    public int ChatMixBalance { get; set; } = 50;
    /// <summary>When set, report 07 25 (the wired base-station knob) drives ChatMix. Off by default.</summary>
    public bool UseKnobAsChatMix { get; set; }
    public int? ChatMixKnobMin { get; set; }
    public int? ChatMixKnobMax { get; set; }
    public string? MasterOutputDeviceId { get; set; }
    public string? MasterInputDeviceId { get; set; }
    public Dictionary<string, ChannelSettings> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, float> BaseVolumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> AppMuted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CableAssignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static MixerConfig CreateDefault()
    {
        var config = new MixerConfig();
        config.EnsureShape();
        return config;
    }

    public void EnsureShape()
    {
        Channels = Normalize(Channels, static s =>
        {
            s ??= new ChannelSettings();
            s.Volume = VolumeMath.NormalizeStored(s.Volume);
            if (string.IsNullOrWhiteSpace(s.DeviceId)) s.DeviceId = null;
            return s;
        });
        foreach (var id in ChannelIds.All)
        {
            var key = id.Key();
            if (!Channels.ContainsKey(key))
                Channels[key] = new ChannelSettings();
        }

        Assignments = Normalize(Assignments, static value => value?.Trim().ToLowerInvariant() ?? "system");
        BaseVolumes = Normalize(BaseVolumes, static value => VolumeMath.NormalizeStored(value));
        AppMuted = Normalize(AppMuted, static value => value);
        CableAssignments = Normalize(CableAssignments, static value => value?.Trim() ?? "");
        foreach (var key in CableAssignments.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).ToList())
            CableAssignments.Remove(key);

        ChatMixBalance = Math.Clamp(ChatMixBalance, 0, 100);
        if (ChatMixKnobMin is int low && (low < 0 || low > 255)) ChatMixKnobMin = null;
        if (ChatMixKnobMax is int high && (high < 0 || high > 255)) ChatMixKnobMax = null;
        if (ChatMixKnobMin is int min && ChatMixKnobMax is int max && min > max)
        {
            ChatMixKnobMin = max;
            ChatMixKnobMax = min;
        }
    }

    public ChannelSettings Channel(ChannelId id)
    {
        var key = id.Key();
        if (!Channels.TryGetValue(key, out var settings) || settings == null)
        {
            settings = new ChannelSettings();
            Channels[key] = settings;
        }
        return settings;
    }

    private static Dictionary<string, T> Normalize<T>(Dictionary<string, T>? source, Func<T, T> clean)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (source == null) return result;
        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            result[pair.Key.Trim().ToLowerInvariant()] = clean(pair.Value);
        }
        return result;
    }
}
