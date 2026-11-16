using System.Runtime.InteropServices;
using Mixer.Config;
using Mixer.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Mixer.Audio;

/// <summary>
/// Captures each VAC line and renders the mix on the master device.
/// Every stream is opened as 32-bit float in shared mode so Windows converts
/// 16-bit cable audio and 24-bit DAC audio. The render thread waits on the WASAPI event.
/// </summary>
internal sealed class VirtualMixGraph : IDisposable
{
    private static readonly Guid PcmSubformat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubformat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly Dictionary<ChannelId, ChannelTap> _taps = new();
    private readonly List<WasapiPump> _pumps = new();
    private readonly LiveMix _live;
    private readonly int _periodMs;
    private readonly bool _bypass;
    private ChannelTap[] _masterTaps = [];
    private float[] _mixLeft = new float[2048];
    private float[] _mixRight = new float[2048];
    private float _rampMaster = 1f;
    private int _clips;
    private long _lastDriftLog;
    private bool _disposed;

    private VirtualMixGraph(LiveMix live, int periodMs, bool bypass)
    {
        _live = live;
        _periodMs = periodMs;
        _bypass = bypass;
    }

    public static VirtualMixGraph? TryStart(
        MMDevice master,
        IReadOnlyDictionary<ChannelId, MMDevice> captures,
        IReadOnlyDictionary<ChannelId, string> renderIds,
        MixerConfig config,
        IReadOnlyDictionary<string, MMDevice> outputs,
        LiveMix live)
    {
        var period = config.MixLatencyMs is 10 or 40 ? config.MixLatencyMs : 20;
        var graph = new VirtualMixGraph(live, period, config.GameStraightThrough);
        try
        {
            foreach (var (channel, source) in captures)
            {
                if (channel is not (ChannelId.Game or ChannelId.Chat or ChannelId.Media or ChannelId.Aux))
                    continue;
                try
                {
                    var opened = graph.OpenClient(source, capture: true, $"{channel} capture");
                    var tap = new ChannelTap(channel, live, opened.SampleRate, period);
                    graph._taps[channel] = tap;
                    var pump = graph.StartCapture(channel, opened, tap);
                    graph._pumps.Add(pump);
                }
                catch (Exception ex)
                {
                    Log.Error($"Mix capture {channel} not started hr=0x{ex.HResult:X8}", ex);
                }
            }

            graph._masterTaps = graph._taps.Values.ToArray();
            if (graph._masterTaps.Length == 0)
            {
                graph.Dispose();
                return null;
            }

            var render = graph.OpenClient(master, capture: false, "render");
            graph._pumps.Add(graph.StartRender(render));
            Log.Info(config.GameStraightThrough
                ? $"Mix bypasses to Game only, period {period}ms."
                : $"Mixing VAC lines to {master.ID}, period {period}ms.");
            return graph;
        }
        catch (Exception ex)
        {
            Log.Error($"Virtual mix failed to start hr=0x{ex.HResult:X8}", ex);
            graph.Dispose();
            return null;
        }
    }

    public void LogDrift()
    {
        var now = Environment.TickCount64;
        if (now - _lastDriftLog < 10000) return;
        _lastDriftLog = now;
        var clips = Interlocked.Exchange(ref _clips, 0);
        var renderFail = 0;
        var renderShort = 0;
        foreach (var pump in _pumps)
        {
            if (pump.Capture) continue;
            renderFail += pump.TakeFailures();
            renderShort += pump.TakeShort();
        }
        var minPadding = -1;
        foreach (var pump in _pumps)
        {
            if (pump.Capture) continue;
            var padding = pump.TakeMinPadding();
            if (padding >= 0) minPadding = padding;
        }
        Log.Info($"Mix health clips={clips} renderFail={renderFail} renderShort={renderShort} minPadding={minPadding} bypass={_bypass} period={_periodMs}ms");
        foreach (var (channel, tap) in _taps)
        {
            var (underrun, insert, overrun, buffered) = tap.TakeDrift();
            var pump = _pumps.FirstOrDefault(item => item.Channel == channel);
            var disc = pump?.TakeDiscontinuity() ?? 0;
            var silent = pump?.TakeSilent() ?? 0;
            var fail = pump?.TakeFailures() ?? 0;
            Log.Info($"Mix clock {channel} underrun={underrun} insert={insert} overrun={overrun} buffered={buffered} peak={tap.Peak:0.000} discontinuity={disc} silent={silent} captureFail={fail}");
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
        foreach (var pump in _pumps)
            pump.Dispose();
        _pumps.Clear();
        _taps.Clear();
    }

    private OpenedClient OpenClient(MMDevice device, bool capture, string label)
    {
        var client = device.AudioClient;
        var mix = client.MixFormat;
        Log.Info($"Mix {label} device format {Describe(mix)} endpoint={device.ID}");
        var channels = Math.Max(1, mix.Channels);
        var wanted = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, channels);
        var flags = AudioClientStreamFlags.EventCallback
            | AudioClientStreamFlags.AutoConvertPcm
            | AudioClientStreamFlags.SrcDefaultQuality;
        var hns = _periodMs * (capture ? 1 : 2) * 10_000L;
        WaveFormat stream = wanted;
        try
        {
            client.Initialize(AudioClientShareMode.Shared, flags, hns, 0, wanted, Guid.Empty);
            Log.Info($"Mix {label} stream IEEE float {wanted.SampleRate}Hz {wanted.Channels}ch AUTOCONVERTPCM|SRC_DEFAULT_QUALITY period={_periodMs}ms buffer={client.BufferSize}");
        }
        catch (Exception ex)
        {
            Log.Error($"Mix {label} float init failed hr=0x{ex.HResult:X8}. Opening the device mix format instead.", ex);
            try { client.Dispose(); } catch { /* failed init */ }
            client = device.AudioClient;
            stream = mix;
            client.Initialize(AudioClientShareMode.Shared, flags, hns, 0, stream, Guid.Empty);
            Log.Info($"Mix {label} stream fallback {Describe(stream)} buffer={client.BufferSize}");
        }

        var wait = new EventWaitHandle(false, EventResetMode.AutoReset);
        client.SetEventHandle(wait.SafeWaitHandle.DangerousGetHandle());
        return new OpenedClient(client, wait, stream, mix.SampleRate, channels);
    }

    private WasapiPump StartCapture(ChannelId channel, OpenedClient opened, ChannelTap tap)
    {
        var pump = new WasapiPump(channel, opened, capture: true);
        var capture = opened.Client.AudioCaptureClient;
        opened.Client.Start();
        pump.Thread = new Thread(() => CaptureLoop(pump, opened, capture, tap))
        {
            IsBackground = true,
            Name = "Mixer.Capture." + channel
        };
        pump.Thread.Start();
        return pump;
    }

    private void PrimeRender(OpenedClient opened, AudioRenderClient render)
    {
        var periodFrames = Math.Max(1, opened.SampleRate * _periodMs / 1000);
        var prime = Math.Min(opened.Client.BufferSize, periodFrames * 2);
        if (prime <= 0) return;
        var data = render.GetBuffer(prime);
        var bytes = prime * Math.Max(opened.Stream.BlockAlign, 1);
        var silence = new byte[bytes];
        Marshal.Copy(silence, 0, data, bytes);
        render.ReleaseBuffer(prime, AudioClientBufferFlags.Silent);
        Log.Info($"Mix render primed {prime} frames, buffer={opened.Client.BufferSize}, period={periodFrames}");
    }

    private WasapiPump StartRender(OpenedClient opened)
    {
        var pump = new WasapiPump(null, opened, capture: false);
        var render = opened.Client.AudioRenderClient;
        PrimeRender(opened, render);
        opened.Client.Start();
        pump.Thread = new Thread(() => RenderLoop(pump, opened, render))
        {
            IsBackground = true,
            Name = "Mixer.Render"
        };
        pump.Thread.Start();
        return pump;
    }

    private void CaptureLoop(WasapiPump pump, OpenedClient opened, AudioCaptureClient capture, ChannelTap tap)
    {
        var format = opened.Client.MixFormat;
        // The stream format is what GetBuffer returns. Re-read from the client we initialized.
        format = opened.Stream;
        while (!pump.Stop)
        {
            if (!opened.Wait.WaitOne(Math.Max(50, _periodMs * 4)))
                continue;
            if (pump.Stop) break;
            try
            {
                while (capture.GetNextPacketSize() > 0)
                {
                    var data = capture.GetBuffer(out var frames, out var flags, out _, out _);
                    if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
                        Interlocked.Increment(ref pump.Discontinuity);
                    var silent = (flags & AudioClientBufferFlags.Silent) != 0;
                    if (silent) Interlocked.Increment(ref pump.Silent);
                    if (frames > 0)
                    {
                        if (silent)
                            tap.WriteSilence(frames);
                        else
                            tap.WritePacket(data, frames, format);
                    }
                    capture.ReleaseBuffer(frames);
                }
            }
            catch (Exception ex)
            {
                var count = Interlocked.Increment(ref pump.Failures);
                if (count <= 3)
                    Log.Error($"Mix capture {pump.Channel} GetBuffer failed hr=0x{ex.HResult:X8}", ex);
            }
        }
    }

    private void RenderLoop(WasapiPump pump, OpenedClient opened, AudioRenderClient render)
    {
        MmThread.EnterProAudio();
        var channels = opened.Channels;
        var format = opened.Stream;
        while (!pump.Stop)
        {
            if (!opened.Wait.WaitOne())
                continue;
            if (pump.Stop) break;
            try
            {
                var padding = opened.Client.CurrentPadding;
                pump.NotePadding(padding);
                var frames = opened.Client.BufferSize - padding;
                if (frames <= 0)
                {
                    Interlocked.Increment(ref pump.Short);
                    continue;
                }

                var data = render.GetBuffer(frames);
                if (IsFloat(format))
                {
                    var samples = frames * channels;
                    if (pump.Scratch.Length < samples) pump.Scratch = new float[samples];
                    RenderMix(pump.Scratch, frames, channels, format);
                    Marshal.Copy(pump.Scratch, 0, data, samples);
                }
                else
                {
                    RenderMix(null, frames, channels, format);
                    if (pump.Bytes.Length < frames * format.BlockAlign) pump.Bytes = new byte[frames * format.BlockAlign];
                    EncodePcm(pump.Bytes, frames, format);
                    Marshal.Copy(pump.Bytes, 0, data, frames * format.BlockAlign);
                }
                render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
            }
            catch (Exception ex)
            {
                var count = Interlocked.Increment(ref pump.Failures);
                if (count <= 3)
                    Log.Error($"Mix render GetBuffer/ReleaseBuffer failed hr=0x{ex.HResult:X8}", ex);
            }
        }
    }

    private void RenderMix(float[]? dest, int frames, int channels, WaveFormat format)
    {
        if (_mixLeft.Length < frames) _mixLeft = new float[frames];
        if (_mixRight.Length < frames) _mixRight = new float[frames];
        Array.Clear(_mixLeft, 0, frames);
        Array.Clear(_mixRight, 0, frames);

        if (_bypass)
        {
            var game = _masterTaps.FirstOrDefault(tap => tap.Channel == ChannelId.Game);
            game?.MixInto(_mixLeft, _mixRight, frames, 1f, 1f);
            _rampMaster = 1f;
            WriteOutput(dest, frames, channels, format, limit: false, masterStart: 1f, masterEnd: 1f);
            return;
        }

        var masterEnd = _live.Master;
        var masterStart = _rampMaster;
        foreach (var tap in _masterTaps)
        {
            var end = tap.Gain;
            tap.MixInto(_mixLeft, _mixRight, frames, tap.Ramp, end);
            tap.Ramp = end;
        }
        WriteOutput(dest, frames, channels, format, limit: true, masterStart, masterEnd);
        _rampMaster = masterEnd;
    }

    private void WriteOutput(float[]? dest, int frames, int channels, WaveFormat format, bool limit, float masterStart, float masterEnd)
    {
        var clips = 0;
        for (var i = 0; i < frames; i++)
        {
            var master = frames == 1 ? masterEnd : masterStart + (masterEnd - masterStart) * ((i + 1f) / frames);
            var left = _mixLeft[i] * master;
            var right = _mixRight[i] * master;
            if (limit)
            {
                left = SoftLimit(left, ref clips);
                right = SoftLimit(right, ref clips);
            }
            if (dest == null)
            {
                _mixLeft[i] = left;
                _mixRight[i] = right;
            }
            else
            {
                Pack(dest, i, channels, left, right);
            }
        }
        if (clips > 0) Interlocked.Add(ref _clips, clips);
    }

    private static float SoftLimit(float sample, ref int clips)
    {
        var abs = Math.Abs(sample);
        if (abs <= 0.9f) return sample;
        if (abs > 1f) clips++;
        var sign = Math.Sign(sample);
        var over = abs - 0.9f;
        return sign * (0.9f + 0.1f * over / (1f + over));
    }

    private static void Pack(float[] dest, int frame, int channels, float left, float right)
    {
        var at = frame * channels;
        dest[at] = left;
        if (channels > 1) dest[at + 1] = right;
        for (var c = 2; c < channels; c++) dest[at + c] = 0f;
    }

    private void EncodePcm(byte[] dest, int frames, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        for (var frame = 0; frame < frames; frame++)
        {
            WritePcm(dest, frame, 0, channels, format, _mixLeft[frame]);
            if (channels > 1) WritePcm(dest, frame, 1, channels, format, _mixRight[frame]);
        }
    }

    private static void WritePcm(byte[] dest, int frame, int channel, int channels, WaveFormat format, float sample)
    {
        var sampleBytes = format.BitsPerSample == 24 ? 3 : format.BitsPerSample == 32 ? 4 : 2;
        var offset = frame * format.BlockAlign + channel * sampleBytes;
        if (offset < 0 || offset + sampleBytes > dest.Length) return;
        sample = Math.Clamp(sample, -1f, 1f);
        if (format.BitsPerSample == 16)
        {
            var pcm = (short)Math.Clamp(Math.Round(sample * 32767f), short.MinValue, short.MaxValue);
            dest[offset] = (byte)pcm;
            dest[offset + 1] = (byte)(pcm >> 8);
            return;
        }
        if (format.BitsPerSample == 24)
        {
            var pcm = (int)Math.Clamp(Math.Round(sample * 8388607f), -8388608, 8388607);
            dest[offset] = (byte)pcm;
            dest[offset + 1] = (byte)(pcm >> 8);
            dest[offset + 2] = (byte)(pcm >> 16);
            return;
        }
        var wide = (int)Math.Clamp(Math.Round(sample * 8388607f), -8388608, 8388607) << 8;
        dest[offset] = (byte)wide;
        dest[offset + 1] = (byte)(wide >> 8);
        dest[offset + 2] = (byte)(wide >> 16);
        dest[offset + 3] = (byte)(wide >> 24);
    }

    private static bool IsFloat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32) return true;
        return format is WaveFormatExtensible ext && ext.SubFormat == FloatSubformat && format.BitsPerSample == 32;
    }

    private static string Describe(WaveFormat format)
    {
        var sub = format is WaveFormatExtensible ext
            ? ext.SubFormat == FloatSubformat ? " sub=float" : ext.SubFormat == PcmSubformat ? " sub=pcm" : " sub=" + ext.SubFormat
            : "";
        return $"{format.Encoding} {format.SampleRate}Hz {format.BitsPerSample}-bit {format.Channels}ch block={format.BlockAlign}{sub}";
    }

    private sealed class OpenedClient : IDisposable
    {
        public OpenedClient(AudioClient client, EventWaitHandle wait, WaveFormat stream, int sampleRate, int channels)
        {
            Client = client;
            Wait = wait;
            Stream = stream;
            SampleRate = sampleRate;
            Channels = channels;
        }

        public AudioClient Client { get; }
        public EventWaitHandle Wait { get; }
        public WaveFormat Stream { get; }
        public int SampleRate { get; }
        public int Channels { get; }

        public void Dispose()
        {
            try { Client.Stop(); } catch { /* already stopped */ }
            try { Client.Dispose(); } catch { /* already stopped */ }
            Wait.Dispose();
        }
    }

    private sealed class WasapiPump : IDisposable
    {
        public WasapiPump(ChannelId? channel, OpenedClient opened, bool capture)
        {
            Channel = channel;
            Opened = opened;
            Capture = capture;
        }

        public ChannelId? Channel { get; }
        public OpenedClient Opened { get; }
        public bool Capture { get; }
        public Thread? Thread { get; set; }
        public volatile bool Stop;
        public int Discontinuity;
        public int Silent;
        public int Failures;
        public int Short;
        private int _minPadding = int.MaxValue;
        public float[] Scratch = new float[2048];
        public byte[] Bytes = new byte[4096];

        public int TakeDiscontinuity() => Interlocked.Exchange(ref Discontinuity, 0);
        public int TakeSilent() => Interlocked.Exchange(ref Silent, 0);
        public int TakeFailures() => Interlocked.Exchange(ref Failures, 0);
        public int TakeShort() => Interlocked.Exchange(ref Short, 0);

        public void NotePadding(int padding)
        {
            int seen;
            do
            {
                seen = _minPadding;
                if (padding >= seen) return;
            }
            while (Interlocked.CompareExchange(ref _minPadding, padding, seen) != seen);
        }

        public int TakeMinPadding()
        {
            var value = Interlocked.Exchange(ref _minPadding, int.MaxValue);
            return value == int.MaxValue ? -1 : value;
        }

        public void Dispose()
        {
            Stop = true;
            try { Opened.Wait.Set(); } catch { /* already gone */ }
            try { Thread?.Join(500); } catch { /* background */ }
            Opened.Dispose();
        }
    }

    private sealed class ChannelTap
    {
        private readonly LiveMix _live;
        private readonly int _destRate;
        private readonly FrameResampler _resampler;
        private readonly DriftRing _ring;
        private float[] _decode = new float[4096];
        private float[] _resampled = new float[4096];
        private float[] _pullL = new float[512];
        private float[] _pullR = new float[512];
        private int _underrun;
        private int _insert;
        private int _overrun;
        private int _buffered;

        public ChannelTap(ChannelId channel, LiveMix live, int sourceRate, int periodMs)
        {
            Channel = channel;
            _live = live;
            _destRate = sourceRate;
            _resampler = new FrameResampler();
            _ring = new DriftRing(sourceRate, periodMs);
            Ramp = live.MixGain(channel);
        }

        public ChannelId Channel { get; }
        public float Peak { get; private set; }
        public float Gain => _live.MixGain(Channel);
        public float Ramp;

        public void WriteSilence(int frames)
        {
            if (frames <= 0) return;
            if (_decode.Length < frames * 2) _decode = new float[frames * 2];
            Array.Clear(_decode, 0, frames * 2);
            Peak = 0f;
            Commit(frames);
        }

        public void WritePacket(IntPtr data, int frames, WaveFormat format)
        {
            var channels = Math.Max(1, format.Channels);
            var interleaved = frames * 2;
            if (_decode.Length < interleaved) _decode = new float[interleaved];
            var bytes = frames * Math.Max(format.BlockAlign, 1);
            var raw = new byte[Math.Max(bytes, 1)];
            if (data != IntPtr.Zero && bytes > 0)
                Marshal.Copy(data, raw, 0, Math.Min(bytes, raw.Length));
            var peak = 0f;
            for (var frame = 0; frame < frames; frame++)
            {
                var (left, right) = Sample(raw, frame, channels, format);
                _decode[frame * 2] = left;
                _decode[frame * 2 + 1] = right;
                peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
            }
            Peak = peak;
            Commit(frames);
        }

        public void MixInto(float[] left, float[] right, int frames, float gainStart, float gainEnd)
        {
            if (frames <= 0) return;
            if (_pullL.Length < frames)
            {
                _pullL = new float[frames];
                _pullR = new float[frames];
            }
            var (missing, inserted, dropped, buffered) = _ring.Read(_pullL, _pullR, frames, 1f);
            for (var i = 0; i < frames; i++)
            {
                var gain = frames == 1 ? gainEnd : gainStart + (gainEnd - gainStart) * ((i + 1f) / frames);
                left[i] += _pullL[i] * gain;
                right[i] += _pullR[i] * gain;
            }
            if (missing > 0) Interlocked.Add(ref _underrun, missing);
            if (inserted > 0) Interlocked.Add(ref _insert, inserted);
            if (dropped > 0) Interlocked.Add(ref _overrun, dropped);
            Volatile.Write(ref _buffered, buffered);
        }

        public (int Underrun, int Insert, int Overrun, int Buffered) TakeDrift() =>
            (Interlocked.Exchange(ref _underrun, 0), Interlocked.Exchange(ref _insert, 0), Interlocked.Exchange(ref _overrun, 0), Volatile.Read(ref _buffered));

        private void Commit(int frames)
        {
            var produced = _resampler.Process(_decode, frames, _destRate, _destRate, EnsureResampled(frames));
            if (produced <= 0) return;
            var dropped = _ring.Write(_resampled, produced);
            if (dropped > 0) Interlocked.Add(ref _overrun, dropped);
        }

        private float[] EnsureResampled(int frames)
        {
            if (_resampled.Length < frames * 2) _resampled = new float[frames * 2];
            return _resampled;
        }

        private static (float Left, float Right) Sample(byte[] raw, int frame, int channels, WaveFormat format)
        {
            float At(int channel)
            {
                var sampleBytes = format.BitsPerSample switch
                {
                    24 => 3,
                    32 => 4,
                    _ => 2
                };
                var offset = frame * format.BlockAlign + channel * sampleBytes;
                if (offset < 0 || offset + sampleBytes > raw.Length) return 0f;
                if (IsFloatFormat(format))
                    return BitConverter.ToSingle(raw, offset);
                if (format.BitsPerSample == 16)
                    return BitConverter.ToInt16(raw, offset) / 32768f;
                if (format.BitsPerSample == 24)
                {
                    var value = raw[offset] | (raw[offset + 1] << 8) | (raw[offset + 2] << 16);
                    if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                    return value / 8388608f;
                }
                var packed = BitConverter.ToInt32(raw, offset);
                return format.BitsPerSample == 32 && format.BlockAlign / Math.Max(1, channels) == 4 && !IsFloatFormat(format)
                    ? (packed >> 8) / 8388608f
                    : packed / 2147483648f;
            }

            var left = At(0);
            var right = channels > 1 ? At(1) : left;
            return (left, right);
        }

        private static bool IsFloatFormat(WaveFormat format) =>
            format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == FloatSubformat);
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
                return frames;
            }

            var needed = (int)Math.Ceiling(frames * (outRate / (double)inRate)) + 4;
            if (output.Length < needed * 2) return 0;
            var step = inRate / (double)outRate;
            var produced = 0;
            var pos = _origin;
            while (pos < frames && produced < needed)
            {
                var index = (int)Math.Floor(pos);
                var frac = pos - index;
                float l0, r0, l1, r1;
                if (index <= 0)
                {
                    l0 = index < 0 ? _edgeL : input[0];
                    r0 = index < 0 ? _edgeR : input[1];
                    l1 = input[Math.Min(frames - 1, Math.Max(0, index + 1)) * 2];
                    r1 = input[Math.Min(frames - 1, Math.Max(0, index + 1)) * 2 + 1];
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

    private sealed class DriftRing
    {
        private readonly float[] _left;
        private readonly float[] _right;
        private readonly int _capacity;
        private readonly int _target;
        private readonly int _low;
        private readonly int _high;
        private readonly object _gate = new();
        private int _read;
        private int _count;
        private int _sinceCorrection = 2;
        private bool _primed;
        private float _holdL;
        private float _holdR;

        public DriftRing(int sampleRate, int periodMs)
        {
            var period = Math.Max(32, sampleRate * periodMs / 1000);
            _target = period * 4;
            _low = period * 2;
            _high = period * 6;
            _capacity = period * 12;
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
                if (!_primed)
                {
                    if (_count >= _target) _primed = true;
                }
                else if (++_sinceCorrection >= 2 && frames > 1)
                {
                    if (_count > _high)
                    {
                        _read = (_read + 1) % _capacity;
                        _count--;
                        dropped = 1;
                        _sinceCorrection = 0;
                    }
                    else if (_count < _low && _count > frames)
                    {
                        inserted = 1;
                        _sinceCorrection = 0;
                    }
                }

                var need = frames - inserted;
                var take = Math.Min(_count, Math.Max(0, need));
                for (var i = 0; i < take; i++)
                {
                    _holdL = _left[_read];
                    _holdR = _right[_read];
                    left[i] = _holdL * gain;
                    right[i] = _holdR * gain;
                    _read = (_read + 1) % _capacity;
                    _count--;
                }

                var filled = take;
                if (inserted == 1 && take > 0 && filled < frames)
                {
                    left[filled] = _holdL * gain;
                    right[filled] = _holdR * gain;
                    filled++;
                }
                else inserted = 0;

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
