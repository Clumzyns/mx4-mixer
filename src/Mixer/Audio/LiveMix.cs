using Mixer.Config;

namespace Mixer.Audio;

/// <summary>
/// Channel and master gains the mix thread reads once per buffer.
/// Writes are atomic so a HID report can publish a new gain without waiting for the UI or config save.
/// </summary>
internal sealed class LiveMix
{
    private readonly float[] _fader = [1f, 1f, 1f, 1f, 1f];
    private readonly float[] _chatMix = [1f, 1f, 1f, 1f, 1f];
    private readonly int[] _muted = new int[5];
    private readonly int[] _softwareFader = [1, 1, 1, 1, 1];
    private readonly int[] _softwareMute = [1, 1, 1, 1, 1];
    private float _master = 1f;

    public void Load(MixerConfig config)
    {
        var (game, chat) = ChatMixMath.ToLevels(config.ChatMixBalance);
        foreach (var id in ChannelIds.All)
        {
            SetFader(id, config.Channel(id).Volume);
            SetMuted(id, config.Channel(id).Muted);
            SetSoftwareFader(id, true);
            SetSoftwareMute(id, true);
            var mix = id == ChannelId.Game ? game / 100f : id == ChannelId.Chat ? chat / 100f : 1f;
            SetChatMix(id, mix);
        }
        SetMaster(config.MasterVolume);
    }

    public float Fader(ChannelId id) => Volatile.Read(ref _fader[(int)id]);

    public void SetFader(ChannelId id, float volume) =>
        Volatile.Write(ref _fader[(int)id], VolumeMath.Clamp(volume));

    public void SetChatMix(ChannelId id, float volume) =>
        Volatile.Write(ref _chatMix[(int)id], VolumeMath.Clamp(volume));

    public bool Muted(ChannelId id) => Volatile.Read(ref _muted[(int)id]) != 0;

    public void SetMuted(ChannelId id, bool muted) =>
        Volatile.Write(ref _muted[(int)id], muted ? 1 : 0);

    public void SetSoftwareFader(ChannelId id, bool software) =>
        Volatile.Write(ref _softwareFader[(int)id], software ? 1 : 0);

    public void SetSoftwareMute(ChannelId id, bool software) =>
        Volatile.Write(ref _softwareMute[(int)id], software ? 1 : 0);

    /// <summary>
    /// Gain inside the mix. The fader is a dB curve that is exact silence at zero,
    /// even when the cable endpoint does not mute at scalar 0.
    /// </summary>
    public float MixGain(ChannelId id)
    {
        var index = (int)id;
        if (Volatile.Read(ref _muted[index]) != 0) return 0f;
        var fader = VolumeMath.FaderGain(Volatile.Read(ref _fader[index]));
        if (fader <= 0f) return 0f;
        var chat = Volatile.Read(ref _chatMix[index]);
        if (chat <= 0f) return 0f;
        return VolumeMath.Clamp(fader * chat);
    }

    public float Master => Volatile.Read(ref _master);

    public void SetMaster(float volume) => Volatile.Write(ref _master, VolumeMath.Clamp(volume));
}
