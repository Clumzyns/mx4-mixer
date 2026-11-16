namespace Mixer.Audio;

/// <summary>
/// Sonar-style Game/Chat balance. 0 is Game only, 100 is Chat only, and 50 keeps both full.
/// </summary>
public static class ChatMixMath
{
    public static (int Game, int Chat) ToLevels(int balance)
    {
        balance = Math.Clamp(balance, 0, 100);
        if (balance <= 50)
            return (100, balance * 2);
        return ((100 - balance) * 2, 100);
    }

    public static int FromLevels(int game, int chat)
    {
        game = Math.Clamp(game, 0, 100);
        chat = Math.Clamp(chat, 0, 100);
        if (game >= 100 && chat >= 100) return 50;
        if (game >= chat)
            return (int)Math.Round(chat / 2.0, MidpointRounding.AwayFromZero);
        return 100 - (int)Math.Round(game / 2.0, MidpointRounding.AwayFromZero);
    }

    public const int VolumeKnobStepPercent = 2;

    /// <summary>
    /// Moves master volume by a fixed percent per knob step. Positive delta is louder
    /// unless invert is set. The result is clamped to 0-100.
    /// </summary>
    public static int ApplyVolumeStep(int volumePercent, int knobDelta, bool invert)
    {
        var steps = invert ? -knobDelta : knobDelta;
        return Math.Clamp(volumePercent + steps * VolumeKnobStepPercent, 0, 100);
    }

    public static int Percent(float volume)
    {
        var pct = (int)Math.Round(VolumeMath.Clamp(volume) * 100d, MidpointRounding.AwayFromZero);
        return Math.Clamp(pct, 0, 100);
    }
}
