namespace Mixer.Audio;

public static class VolumeMath
{
    public static float Clamp(float value)
    {
        if (float.IsNaN(value) || value < 0f) return 0f;
        if (value > 1f) return 1f;
        return value;
    }

    /// <summary>
    /// Values above 1 are treated as a percent (80 -&gt; 0.8) so a hand-edited config does not pin the fader.
    /// </summary>
    public static float NormalizeStored(float value)
    {
        if (float.IsNaN(value) || value < 0f) return 0f;
        if (value > 1f && value <= 100f) return value / 100f;
        return Clamp(value);
    }

    /// <summary>
    /// Slider position to amplitude. Full scale is 0 dB. Zero is exact silence.
    /// </summary>
    public static float FaderGain(float position)
    {
        position = Clamp(position);
        if (position <= 0f) return 0f;
        var db = (position - 1.0) * 60.0;
        return (float)Math.Pow(10.0, db / 20.0);
    }

    public static float Effective(float baseVolume, float channelVolume)
        => Clamp(Clamp(baseVolume) * Clamp(channelVolume));

    public static float BaseFromEffective(float effective, float channelVolume)
    {
        if (channelVolume <= 0.001f) return Clamp(effective);
        return Clamp(effective / channelVolume);
    }
}
