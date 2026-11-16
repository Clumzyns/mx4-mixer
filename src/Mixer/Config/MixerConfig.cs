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
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool CableNamesApplied { get; set; }
    public bool ChatMixEnabled { get; set; } = true;
    /// <summary>0 is Game only, 100 is Chat only, 50 keeps both full.</summary>
    public int ChatMixBalance { get; set; } = 50;
    /// <summary>Master output gain, 0-1. The base-station volume knob nudges this.</summary>
    public float MasterVolume { get; set; } = 1f;
    /// <summary>When set, NN 0 is silent and the top of the knob is full. Off means NN is attenuation.</summary>
    public bool InvertVolumeKnob { get; set; }
    /// <summary>Highest 07 25 position seen. Defaults to 56, the base station's silence stop.</summary>
    public int VolumeKnobMax { get; set; } = ChatMixMath.DefaultVolumeKnobMax;
    public string? MasterOutputDeviceId { get; set; }
    public string? MasterInputDeviceId { get; set; }
    public Dictionary<string, ChannelSettings> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, float> BaseVolumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> AppMuted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CableAssignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CableCaptures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
        CableCaptures = Normalize(CableCaptures, static value => value?.Trim() ?? "");
        foreach (var key in CableCaptures.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).ToList())
            CableCaptures.Remove(key);

        ChatMixBalance = Math.Clamp(ChatMixBalance, 0, 100);
        MasterVolume = VolumeMath.NormalizeStored(MasterVolume);
        if (VolumeKnobMax < 1) VolumeKnobMax = ChatMixMath.DefaultVolumeKnobMax;
        if (VolumeKnobMax > 255) VolumeKnobMax = 255;
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
