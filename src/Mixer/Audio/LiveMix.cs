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
    /// Gain applied inside the mix. The cable fader is omitted when the render endpoint owns it.
    /// </summary>
    public float MixGain(ChannelId id)
    {
        var index = (int)id;
        var fader = Volatile.Read(ref _softwareFader[index]) != 0 ? Volatile.Read(ref _fader[index]) : 1f;
        var chat = Volatile.Read(ref _chatMix[index]);
        var mute = Volatile.Read(ref _softwareMute[index]) != 0 && Volatile.Read(ref _muted[index]) != 0 ? 0f : 1f;
        return VolumeMath.Clamp(fader) * VolumeMath.Clamp(chat) * mute;
    }

    public float Master => Volatile.Read(ref _master);

    public void SetMaster(float volume) => Volatile.Write(ref _master, VolumeMath.Clamp(volume));
}
