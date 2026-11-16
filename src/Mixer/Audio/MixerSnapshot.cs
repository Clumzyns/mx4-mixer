namespace Mixer.Audio;

public sealed class DeviceSnapshot
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

public sealed class AppSnapshot
{
    public required string ExeKey { get; init; }
    public required string DisplayName { get; init; }
    public string? ExePath { get; init; }
    public uint ProcessId { get; init; }
    public int SessionCount { get; init; }
    public float BaseVolume { get; init; }
    public bool Muted { get; init; }
    public ChannelId Channel { get; init; }
}

public sealed class ChannelSnapshot
{
    public ChannelId Id { get; init; }
    public float Volume { get; init; }
    public bool Muted { get; init; }
    public string? DeviceId { get; init; }
    public string CableLabel { get; init; } = "";
}

public sealed class MixerSnapshot
{
    public IReadOnlyList<DeviceSnapshot> Outputs { get; init; } = [];
    public IReadOnlyList<DeviceSnapshot> Inputs { get; init; } = [];
    public string? MasterOutputId { get; init; }
    public string? MasterInputId { get; init; }
    public float MasterVolume { get; init; } = 1f;
    public bool MicMuted { get; init; }
    public IReadOnlyList<ChannelSnapshot> Channels { get; init; } = [];
    public IReadOnlyList<AppSnapshot> Apps { get; init; } = [];
    public string Status { get; init; } = "";
    public bool RoutingAvailable { get; init; }
    public bool VirtualMixActive { get; init; }
    public string SonarWarning { get; init; } = "";
    public string BackendName { get; init; } = "";
    public IReadOnlyList<DeviceSnapshot> Cables { get; init; } = [];
}

public sealed class PeakUpdate
{
    public float MicPeak { get; init; }
    public float[] ChannelPeaks { get; init; } = new float[5];
    public Dictionary<string, float> AppPeaks { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
