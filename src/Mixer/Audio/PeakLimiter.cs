namespace Mixer.Audio;

/// <summary>
/// Lookahead peak limiter for the final mix. Left and right share one gain so the image does not move.
/// Audio at or below the ceiling passes bit-exact, delayed by the lookahead.
/// Gain ramps down over the lookahead before a peak arrives and recovers with an exponential release.
/// </summary>
internal sealed class PeakLimiter
{
    /// <summary>Full scale. A single source at unity gain never engages the limiter.</summary>
    public const float Ceiling = 1f;
    public const double LookaheadMs = 2.0;
    public const double ReleaseMs = 150.0;

    private readonly int _look;
    private readonly float[] _delayLeft;
    private readonly float[] _delayRight;
    private int _delayAt;

    // Monotonic queue holding the lowest needed gain over the last _look + 1 frames.
    private readonly float[] _queueGain;
    private readonly long[] _queueFrame;
    private readonly int _queueSize;
    private int _queueHead;
    private int _queueCount;
    private long _frame;

    private readonly double _release;
    // Double, so the release keeps moving at 384 kHz where each step is below float resolution.
    private double _held = 1.0;

    // Moving average over _look frames turns each gain step into a linear ramp.
    private readonly float[] _box;
    private int _boxAt;
    private double _boxSum;
    private int _boxReduced;

    public PeakLimiter(int sampleRate)
    {
        sampleRate = Math.Max(8000, sampleRate);
        _look = Math.Max(8, (int)Math.Round(sampleRate * LookaheadMs / 1000.0));
        _delayLeft = new float[_look];
        _delayRight = new float[_look];
        _queueSize = _look + 2;
        _queueGain = new float[_queueSize];
        _queueFrame = new long[_queueSize];
        _box = new float[_look];
        Array.Fill(_box, 1f);
        _boxSum = _look;
        _release = 1.0 - Math.Exp(-1.0 / (sampleRate * ReleaseMs / 1000.0));
    }

    public int LookaheadFrames => _look;

    /// <summary>
    /// Limits in place. Returns the lowest gain applied in this block (1 means untouched)
    /// and adds the number of frames that were turned down to <paramref name="reducedFrames"/>.
    /// </summary>
    public float Process(float[] left, float[] right, int frames, ref int reducedFrames)
    {
        var lowest = 1f;
        for (var i = 0; i < frames; i++)
        {
            var inLeft = left[i];
            var inRight = right[i];
            if (!float.IsFinite(inLeft)) inLeft = 0f;
            if (!float.IsFinite(inRight)) inRight = 0f;

            var peak = Math.Max(Math.Abs(inLeft), Math.Abs(inRight));
            var need = peak > Ceiling ? Ceiling / peak : 1f;

            while (_queueCount > 0 && _queueGain[(_queueHead + _queueCount - 1) % _queueSize] >= need)
                _queueCount--;
            var tail = (_queueHead + _queueCount) % _queueSize;
            _queueGain[tail] = need;
            _queueFrame[tail] = _frame;
            _queueCount++;
            while (_queueFrame[_queueHead] < _frame - _look)
            {
                _queueHead = (_queueHead + 1) % _queueSize;
                _queueCount--;
            }
            var floor = _queueGain[_queueHead];
            _frame++;

            if (floor <= _held)
            {
                _held = floor;
            }
            else
            {
                _held = Math.Min(floor, _held + (1.0 - _held) * _release);
                // Within 0.01 dB of unity: finish the release so the path returns to bit-exact.
                if (_held > 0.999 && floor >= 1f) _held = 1.0;
            }

            var leaving = _box[_boxAt];
            var step = (float)_held;
            _box[_boxAt] = step;
            _boxAt = _boxAt + 1 == _look ? 0 : _boxAt + 1;
            if (leaving < 1f) _boxReduced--;
            if (step < 1f) _boxReduced++;
            _boxSum += step - leaving;

            var outLeft = _delayLeft[_delayAt];
            var outRight = _delayRight[_delayAt];
            _delayLeft[_delayAt] = inLeft;
            _delayRight[_delayAt] = inRight;
            _delayAt = _delayAt + 1 == _look ? 0 : _delayAt + 1;

            if (_boxReduced == 0)
            {
                // Nothing in the window was turned down. Pass the samples untouched and drop rounding drift.
                _boxSum = _look;
                left[i] = outLeft;
                right[i] = outRight;
                continue;
            }

            var gain = (float)(_boxSum / _look);
            if (gain > 1f) gain = 1f;
            if (gain < lowest) lowest = gain;
            reducedFrames++;
            left[i] = Math.Clamp(outLeft * gain, -Ceiling, Ceiling);
            right[i] = Math.Clamp(outRight * gain, -Ceiling, Ceiling);
        }
        return lowest;
    }
}
