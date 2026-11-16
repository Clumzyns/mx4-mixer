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

    /// <summary>
    /// Maps an absolute knob position onto 0-100 from the lowest and highest values seen.
    /// Returns false until two different positions have been observed.
    /// Low maps to Game, high maps to Chat.
    /// </summary>
    public static bool TryMapKnob(int value, ref int? min, ref int? max, out int balance, out bool rangeChanged)
    {
        value = Math.Clamp(value, 0, 255);
        rangeChanged = false;
        if (min is null || value < min)
        {
            min = value;
            rangeChanged = true;
        }
        if (max is null || value > max)
        {
            max = value;
            rangeChanged = true;
        }

        balance = 50;
        if (min is null || max is null || max.Value == min.Value) return false;
        balance = (int)Math.Round((value - min.Value) * 100.0 / (max.Value - min.Value), MidpointRounding.AwayFromZero);
        balance = Math.Clamp(balance, 0, 100);
        return true;
    }

    public static int Percent(float volume)
    {
        var pct = (int)Math.Round(VolumeMath.Clamp(volume) * 100d, MidpointRounding.AwayFromZero);
        return Math.Clamp(pct, 0, 100);
    }
}
