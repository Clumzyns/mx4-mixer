using Mixer.Config;

namespace Mixer.Audio;

/// <summary>
/// Channel and master gains the mix thread reads once per buffer.
/// Writes are atomic so a HID report can publish a new gain without waiting for the UI or config save.
/// </summary>
internal sealed class LiveMix
{
    private readonly float[] _channel = [1f, 1f, 1f, 1f, 1f];
    private readonly int[] _muted = new int[5];
    private float _master = 1f;

    public void Load(MixerConfig config)
    {
        foreach (var id in ChannelIds.All)
        {
            SetChannel(id, config.Channel(id).Volume);
            SetMuted(id, config.Channel(id).Muted);
        }
        SetMaster(config.MasterVolume);
    }

    public float Channel(ChannelId id) => Volatile.Read(ref _channel[(int)id]);

    public void SetChannel(ChannelId id, float volume) =>
        Volatile.Write(ref _channel[(int)id], VolumeMath.Clamp(volume));

    public bool Muted(ChannelId id) => Volatile.Read(ref _muted[(int)id]) != 0;

    public void SetMuted(ChannelId id, bool muted) =>
        Volatile.Write(ref _muted[(int)id], muted ? 1 : 0);

    public float Audible(ChannelId id) => Muted(id) ? 0f : Channel(id);

    public float Master => Volatile.Read(ref _master);

    public void SetMaster(float volume) => Volatile.Write(ref _master, VolumeMath.Clamp(volume));
}
