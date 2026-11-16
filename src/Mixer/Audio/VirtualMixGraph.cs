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
    private readonly float[] _frame = new float[2];
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
        var master = _live.Master;
        var taps = _masterTaps;
        Span<float> gains = stackalloc float[taps.Length];
        for (var t = 0; t < taps.Length; t++)
            gains[t] = taps[t].Gain;
        Array.Clear(buffer, offset, count);
        for (var i = 0; i < frames; i++)
        {
            float left = 0, right = 0;
            for (var t = 0; t < taps.Length; t++)
            {
                if (gains[t] <= 0f || !taps[t].Ring.TryRead(_frame)) continue;
                left += _frame[0] * gains[t];
                right += _frame[1] * gains[t];
            }

            left = Math.Clamp(left * master, -1f, 1f);
            right = Math.Clamp(right * master, -1f, 1f);
            Encode(WaveFormat, buffer, offset + i * bytesPerFrame, channels, left, right);
        }

        return count;
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
            MmThread.EnterProAudio();
            var channels = Math.Max(1, WaveFormat.Channels);
            var bytesPerFrame = WaveFormat.BlockAlign > 0 ? WaveFormat.BlockAlign : channels * WaveFormat.BitsPerSample / 8;
            if (bytesPerFrame <= 0) return 0;
            var frames = count / bytesPerFrame;
            var gain = _tap.Gain;
            var frame = new float[2];
            for (var i = 0; i < frames; i++)
            {
                float left = 0, right = 0;
                if (gain > 0f && _tap.Ring.TryRead(frame))
                {
                    left = Math.Clamp(frame[0] * gain, -1f, 1f);
                    right = Math.Clamp(frame[1] * gain, -1f, 1f);
                }
                Encode(WaveFormat, buffer, offset + i * bytesPerFrame, channels, left, right);
            }

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
            Ring = new FloatRing(destRate);
        }

        public ChannelId Channel { get; }
        public bool Separate { get; }
        public FloatRing Ring { get; }
        public WasapiCapture? Capture { get; set; }
        public float Peak { get; private set; }
        public float Gain => _live.MixGain(Channel);

        public void Write(byte[] buffer, int bytes, WaveFormat format)
        {
            MmThread.EnterProAudio();
            var frames = Decode(buffer, bytes, format);
            if (frames <= 0) return;
            var outFrames = format.SampleRate == _destRate
                ? frames
                : (int)Math.Ceiling(frames * (_destRate / (double)Math.Max(1, format.SampleRate))) + 8;
            if (_resampled.Length < outFrames * 2) _resampled = new float[outFrames * 2];
            var produced = _resampler.Process(_decode, frames, format.SampleRate, _destRate, _resampled);
            if (produced <= 0) return;
            Ring.Write(_resampled.AsSpan(0, produced * 2));
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

    private sealed class FloatRing
    {
        private readonly float[] _data;
        private readonly int _maxSamples;
        private readonly object _gate = new();
        private int _read;
        private int _count;

        public FloatRing(int sampleRate)
        {
            var periodFrames = Math.Max(1, sampleRate * PeriodMs / 1000);
            _maxSamples = periodFrames * 2 * 2;
            _data = new float[_maxSamples];
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            lock (_gate)
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    if (_count == _data.Length)
                    {
                        _read = (_read + 2) % _data.Length;
                        _count -= 2;
                    }

                    _data[(_read + _count) % _data.Length] = samples[i];
                    _count++;
                }

                if (_count > _maxSamples)
                {
                    var drop = _count - _maxSamples;
                    if ((drop & 1) == 1) drop--;
                    _read = (_read + drop) % _data.Length;
                    _count -= drop;
                }
            }
        }

        public bool TryRead(float[] frame)
        {
            lock (_gate)
            {
                if (_count < 2) return false;
                frame[0] = _data[_read];
                frame[1] = _data[(_read + 1) % _data.Length];
                _read = (_read + 2) % _data.Length;
                _count -= 2;
                return true;
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
