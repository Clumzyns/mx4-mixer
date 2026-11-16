namespace Mixer.Services;

internal static class SafeTime
{
    public static TimeSpan Milliseconds(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds <= 0)
            return TimeSpan.Zero;
        if (milliseconds >= TimeSpan.MaxValue.TotalMilliseconds)
            return TimeSpan.MaxValue;
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
