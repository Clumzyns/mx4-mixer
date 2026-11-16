using System.Runtime.InteropServices;
using Mixer.Config;
using Mixer.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Mixer.Audio;

/// <summary>
/// Loopback-captures each virtual channel in that device's mix format and renders the mix
/// in the master device's mix format. Event-driven WASAPI, about 10 ms periods.
/// Capture queues are trimmed to two periods so delay cannot build up.
/// </summary>
internal sealed class VirtualMixGraph : IDisposable, IWaveProvider
{
    private const int PeriodMs = 10;
    private readonly Dictionary<ChannelId, ChannelTap> _taps = new();
    private readonly List<WasapiOut> _ownedOutputs = new();
    private readonly LiveMix _live;
    private ChannelTap[] _masterTaps = [];
    private float[] _mixLeft = new float[2048];
    private float[] _mixRight = new float[2048];
    private long _lastDriftLog;
    private bool _disposed;

    private VirtualMixGraph(WaveFormat outputFormat, LiveMix live)
    {
        WaveFormat = outputFormat;
        _live = live;
    }

    public WaveFormat WaveFormat { get; }

    public static VirtualMixGraph? TryStart(
        MMDevice master,
        IReadOnlyDictionary<ChannelId, MMDevice> captures,
        IReadOnlyDictionary<ChannelId, string> renderIds,
        MixerConfig config,
        IReadOnlyDictionary<string, MMDevice> outputs,
        LiveMix live)
    {
        var outputFormat = PlaybackFormat(master);
        var graph = new VirtualMixGraph(outputFormat, live);
        try
        {
            foreach (var (channel, source) in captures)
            {
                if (channel is not (ChannelId.Game or ChannelId.Chat or ChannelId.Media or ChannelId.Aux))
                    continue;
                try
                {
                    graph._taps[channel] = graph.StartTap(channel, source, live, false, null, outputFormat);
                }
                catch (Exception ex)
                {
                    Log.Error($"Mix capture {channel} not started", ex);
                }
            }

            graph._masterTaps = graph._taps.Values.Where(tap => !tap.Separate).ToArray();
            if (graph._masterTaps.Length > 0)
            {
                var output = new WasapiOut(master, AudioClientShareMode.Shared, true, PeriodMs);
                output.Init(graph);
                output.Play();
                graph._ownedOutputs.Add(output);
                Log.Info($"Mix render {outputFormat.Encoding} {outputFormat.SampleRate}Hz {outputFormat.BitsPerSample}-bit {outputFormat.Channels}ch, event {PeriodMs}ms.");
            }

            return graph;
        }
        catch (Exception ex)
        {
            Log.Error("Virtual mix failed to start", ex);
            graph.Dispose();
            return null;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        MmThread.EnterProAudio();
        var channels = Math.Max(1, WaveFormat.Channels);
        var bytesPerFrame = WaveFormat.BlockAlign > 0 ? WaveFormat.BlockAlign : channels * WaveFormat.BitsPerSample / 8;
        if (bytesPerFrame <= 0) return 0;
        var frames = count / bytesPerFrame;
        if (frames <= 0) return 0;
        if (_mixLeft.Length < frames) _mixLeft = new float[frames];
        if (_mixRight.Length < frames) _mixRight = new float[frames];
        Array.Clear(_mixLeft, 0, frames);
        Array.Clear(_mixRight, 0, frames);
        var master = _live.Master;
        var taps = _masterTaps;
        for (var t = 0; t < taps.Length; t++)
        {
            var gain = taps[t].Gain;
            if (gain <= 0f) continue;
            taps[t].MixInto(_mixLeft, _mixRight, frames, gain);
        }

        for (var i = 0; i < frames; i++)
        {
            var left = Math.Clamp(_mixLeft[i] * master, -1f, 1f);
            var right = Math.Clamp(_mixRight[i] * master, -1f, 1f);
            Encode(WaveFormat, buffer, offset + i * bytesPerFrame, channels, left, right);
        }

        return count;
    }

    public void LogDrift()
    {
        var now = Environment.TickCount64;
        if (now - _lastDriftLog < 10000) return;
        _lastDriftLog = now;
        foreach (var (channel, tap) in _taps)
        {
            var (underrun, insert, overrun, buffered) = tap.TakeDrift();
            Log.Info($"Mix clock {channel} underrun={underrun} insert={insert} overrun={overrun} buffered={buffered}");
        }
    }

    public void CopyPeaks(Span<float> peaks)
    {
        foreach (var (channel, tap) in _taps)
        {
            var index = (int)channel;
            if (index >= 0 && index < peaks.Length)
                peaks[index] = tap.Peak;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var tap in _taps.Values)
            tap.Dispose();
        foreach (var output in _ownedOutputs)
        {
            try { output.Stop(); } catch { /* already stopped */ }
            try { output.Dispose(); } catch { /* already stopped */ }
        }
        _taps.Clear();
    }

    private ChannelTap StartTap(ChannelId channel, MMDevice source, LiveMix live, bool separate, MMDevice? ownOutput, WaveFormat destFormat)
    {
        var tap = new ChannelTap(channel, live, separate, destFormat.SampleRate);
        var capture = new WasapiCapture(source, true, PeriodMs)
        {
            ShareMode = AudioClientShareMode.Shared
        };
        capture.DataAvailable += (_, args) => tap.Write(args.Buffer, args.BytesRecorded, capture.WaveFormat);
        capture.StartRecording();
        tap.Capture = capture;
        Log.Info($"Mix capture {channel} started endpoint={source.ID} {capture.WaveFormat.SampleRate}Hz -> {destFormat.SampleRate}Hz event {PeriodMs}ms.");

        if (separate && ownOutput != null)
        {
            var output = new WasapiOut(ownOutput, AudioClientShareMode.Shared, true, PeriodMs);
            output.Init(new TapOutput(tap, destFormat));
            output.Play();
            _ownedOutputs.Add(output);
        }

        return tap;
    }

    private static WaveFormat PlaybackFormat(MMDevice device)
    {
        var mix = device.AudioClient.MixFormat;
        if (mix.Encoding == WaveFormatEncoding.IeeeFloat && mix.BitsPerSample == 32)
            return mix;
        if (mix.Encoding == WaveFormatEncoding.Pcm && mix.BitsPerSample == 16)
            return mix;
        return WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, Math.Max(1, mix.Channels));
    }

    private static void Encode(WaveFormat format, byte[] buffer, int offset, int channels, float left, float right)
    {
        for (var channel = 0; channel < channels; channel++)
        {
            var sample = channel == 0 ? left : channel == 1 ? right : 0f;
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                var bits = BitConverter.SingleToInt32Bits(sample);
                buffer[offset] = (byte)bits;
                buffer[offset + 1] = (byte)(bits >> 8);
                buffer[offset + 2] = (byte)(bits >> 16);
                buffer[offset + 3] = (byte)(bits >> 24);
                offset += 4;
            }
            else
            {
                var pcm = (short)Math.Clamp(Math.Round(sample * 32767f), short.MinValue, short.MaxValue);
                buffer[offset] = (byte)pcm;
                buffer[offset + 1] = (byte)(pcm >> 8);
                offset += 2;
            }
        }
    }

    private sealed class TapOutput : IWaveProvider
    {
        private readonly ChannelTap _tap;
        public TapOutput(ChannelTap tap, WaveFormat format)
        {
            _tap = tap;
            WaveFormat = format;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var channels = Math.Max(1, WaveFormat.Channels);
            var bytesPerFrame = WaveFormat.BlockAlign > 0 ? WaveFormat.BlockAlign : channels * WaveFormat.BitsPerSample / 8;
            if (bytesPerFrame <= 0) return 0;
            var frames = count / bytesPerFrame;
            var gain = _tap.Gain;
            var lefts = new float[Math.Max(1, frames)];
            var rights = new float[Math.Max(1, frames)];
            if (gain > 0f)
                _tap.MixInto(lefts, rights, frames, gain);
            for (var i = 0; i < frames; i++)
                Encode(WaveFormat, buffer, offset + i * bytesPerFrame, channels, Math.Clamp(lefts[i], -1f, 1f), Math.Clamp(rights[i], -1f, 1f));

            var written = frames * bytesPerFrame;
            if (written < count) Array.Clear(buffer, offset + written, count - written);
            return count;
        }
    }

    private sealed class ChannelTap : IDisposable
    {
        private readonly LiveMix _live;
        private readonly int _destRate;
        private readonly FrameResampler _resampler;
        private float[] _decode = new float[2048];
        private float[] _resampled = new float[4096];
        public ChannelTap(ChannelId channel, LiveMix live, bool separate, int destRate)
        {
            Channel = channel;
            _live = live;
            Separate = separate;
            _destRate = destRate;
            _resampler = new FrameResampler();
            _ring = new DriftRing(destRate);
        }

        public ChannelId Channel { get; }
        public bool Separate { get; }
        public WasapiCapture? Capture { get; set; }
        public float Peak { get; private set; }
        public float Gain => _live.MixGain(Channel);
        private readonly DriftRing _ring;
        private float[] _pullL = new float[512];
        private float[] _pullR = new float[512];
        private int _underrun;
        private int _insert;
        private int _overrun;

        public void MixInto(float[] left, float[] right, int frames, float gain)
        {
            if (frames <= 0) return;
            if (_pullL.Length < frames)
            {
                _pullL = new float[frames];
                _pullR = new float[frames];
            }
            var (missing, inserted, dropped, buffered) = _ring.Read(_pullL, _pullR, frames, gain);
            for (var i = 0; i < frames; i++)
            {
                left[i] += _pullL[i];
                right[i] += _pullR[i];
            }
            if (missing > 0) Interlocked.Add(ref _underrun, missing);
            if (inserted > 0) Interlocked.Add(ref _insert, inserted);
            if (dropped > 0) Interlocked.Add(ref _overrun, dropped);
            Volatile.Write(ref _buffered, buffered);
        }

        private int _buffered;

        public (int Underrun, int Insert, int Overrun, int Buffered) TakeDrift() =>
            (Interlocked.Exchange(ref _underrun, 0), Interlocked.Exchange(ref _insert, 0), Interlocked.Exchange(ref _overrun, 0), Volatile.Read(ref _buffered));

        public void Write(byte[] buffer, int bytes, WaveFormat format)
        {
            var frames = Decode(buffer, bytes, format);
            if (frames <= 0) return;
            var outFrames = format.SampleRate == _destRate
                ? frames
                : (int)Math.Ceiling(frames * (_destRate / (double)Math.Max(1, format.SampleRate))) + 8;
            if (_resampled.Length < outFrames * 2) _resampled = new float[outFrames * 2];
            var produced = _resampler.Process(_decode, frames, format.SampleRate, _destRate, _resampled);
            if (produced <= 0) return;
            var dropped = _ring.Write(_resampled, produced);
            if (dropped > 0) Interlocked.Add(ref _overrun, dropped);
        }

        public void Dispose()
        {
            try { Capture?.StopRecording(); } catch { /* already stopped */ }
            try { Capture?.Dispose(); } catch { /* already stopped */ }
        }

        private int Decode(byte[] buffer, int bytes, WaveFormat format)
        {
            var channels = Math.Max(1, format.Channels);
            int frames;
            var float32 = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
            var pcm16 = format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16;
            if (float32) frames = bytes / (4 * channels);
            else if (pcm16) frames = bytes / (2 * channels);
            else return 0;
            if (frames <= 0) return 0;
            if (_decode.Length < frames * 2) _decode = new float[frames * 2];
            var peak = 0f;
            for (var frame = 0; frame < frames; frame++)
            {
                float left, right;
                if (float32)
                {
                    left = BitConverter.ToSingle(buffer, (frame * channels) * 4);
                    right = channels > 1 ? BitConverter.ToSingle(buffer, (frame * channels + 1) * 4) : left;
                }
                else
                {
                    left = BitConverter.ToInt16(buffer, (frame * channels) * 2) / 32768f;
                    right = channels > 1 ? BitConverter.ToInt16(buffer, (frame * channels + 1) * 2) / 32768f : left;
                }
                _decode[frame * 2] = left;
                _decode[frame * 2 + 1] = right;
                peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
            }
            Peak = peak;
            return frames;
        }
    }

    private sealed class FrameResampler
    {
        private double _origin;
        private float _edgeL;
        private float _edgeR;

        public int Process(float[] input, int frames, int inRate, int outRate, float[] output)
        {
            if (frames <= 0) return 0;
            if (inRate == outRate)
            {
                var samples = frames * 2;
                if (output.Length < samples) return 0;
                Array.Copy(input, output, samples);
                _edgeL = input[samples - 2];
                _edgeR = input[samples - 1];
                return frames;
            }

            var needed = (int)Math.Ceiling(frames * (outRate / (double)inRate)) + 4;
            if (output.Length < needed * 2)
                return 0;
            // Caller grows the buffer. Signal by returning -1? We'll grow outside if 0 and rates differ.
            var step = inRate / (double)outRate;
            var produced = 0;
            var pos = _origin;
            while (pos < frames && produced < needed)
            {
                var index = (int)Math.Floor(pos);
                var frac = pos - index;
                float l0, r0, l1, r1;
                if (index < 0)
                {
                    l0 = _edgeL;
                    r0 = _edgeR;
                    l1 = input[0];
                    r1 = input[1];
                }
                else if (index >= frames - 1)
                {
                    l0 = input[(frames - 1) * 2];
                    r0 = input[(frames - 1) * 2 + 1];
                    l1 = l0;
                    r1 = r0;
                }
                else
                {
                    l0 = input[index * 2];
                    r0 = input[index * 2 + 1];
                    l1 = input[(index + 1) * 2];
                    r1 = input[(index + 1) * 2 + 1];
                }

                output[produced * 2] = (float)(l0 + (l1 - l0) * frac);
                output[produced * 2 + 1] = (float)(r0 + (r1 - r0) * frac);
                produced++;
                pos += step;
            }

            _edgeL = input[(frames - 1) * 2];
            _edgeR = input[(frames - 1) * 2 + 1];
            _origin = pos - frames;
            return produced;
        }
    }

    /// <summary>
    /// Stereo frames for one capture. The render clock drops or repeats a frame when this
    /// source runs ahead of or behind the master device.
    /// </summary>
    private sealed class DriftRing
    {
        private readonly float[] _left;
        private readonly float[] _right;
        private readonly int _capacity;
        private readonly int _target;
        private readonly int _period;
        private readonly object _gate = new();
        private int _read;
        private int _count;
        private float _holdL;
        private float _holdR;

        public DriftRing(int sampleRate)
        {
            _period = Math.Max(32, sampleRate * PeriodMs / 1000);
            _target = _period * 4;
            _capacity = _period * 12;
            _left = new float[_capacity];
            _right = new float[_capacity];
        }

        public int Write(float[] interleaved, int frames)
        {
            if (frames <= 0) return 0;
            lock (_gate)
            {
                var dropped = 0;
                for (var i = 0; i < frames; i++)
                {
                    if (_count == _capacity)
                    {
                        _read = (_read + 1) % _capacity;
                        _count--;
                        dropped++;
                    }

                    var at = (_read + _count) % _capacity;
                    _left[at] = interleaved[i * 2];
                    _right[at] = interleaved[i * 2 + 1];
                    _count++;
                }

                if (_count > _target + _period * 2)
                {
                    var extra = _count - (_target + _period);
                    _read = (_read + extra) % _capacity;
                    _count -= extra;
                    dropped += extra;
                }

                return dropped;
            }
        }

        public (int Missing, int Inserted, int Dropped, int Buffered) Read(float[] left, float[] right, int frames, float gain)
        {
            lock (_gate)
            {
                var missing = 0;
                var inserted = 0;
                var dropped = 0;
                if (_count > _target + _period)
                {
                    var extra = Math.Min(_count - _target, _period);
                    _read = (_read + extra) % _capacity;
                    _count -= extra;
                    dropped += extra;
                }
                else if (_count + _period < _target && _count > 2 && frames > 1)
                {
                    inserted = 1;
                }

                var need = frames - inserted;
                var take = Math.Min(_count, need);
                for (var i = 0; i < take; i++)
                {
                    _holdL = _left[_read];
                    _holdR = _right[_read];
                    left[i] = _holdL * gain;
                    right[i] = _holdR * gain;
                    _read = (_read + 1) % _capacity;
                    _count--;
                }

                if (inserted == 1 && take > 0)
                {
                    left[take] = _holdL * gain;
                    right[take] = _holdR * gain;
                    for (var i = take + 1; i < frames; i++)
                    {
                        left[i] = left[i - 1];
                        right[i] = right[i - 1];
                    }
                }

                var filled = take + inserted;
                if (filled < frames)
                {
                    missing = frames - filled;
                    for (var i = filled; i < frames; i++)
                    {
                        left[i] = _holdL * gain;
                        right[i] = _holdR * gain;
                    }
                }

                return (missing, inserted, dropped, _count);
            }
        }
    }

    private static class MmThread
    {
        [ThreadStatic] private static int _state;

        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        public static void EnterProAudio()
        {
            if (_state != 0) return;
            try
            {
                uint index = 0;
                var handle = AvSetMmThreadCharacteristics("Pro Audio", ref index);
                _state = handle == IntPtr.Zero ? 2 : 1;
            }
            catch
            {
                _state = 2;
            }
        }
    }
}
