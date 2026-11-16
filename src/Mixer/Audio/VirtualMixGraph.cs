using Mixer.Config;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Mixer.Audio;

/// <summary>
/// Loopback-captures each virtual channel device, applies that channel's fader, and
/// renders one shared-mode stream to the master playback device.
/// A channel whose own output is a different real device is rendered there instead.
/// </summary>
internal sealed class VirtualMixGraph : IDisposable, ISampleProvider
{
    private const int Rate = 48000;
    private readonly WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
    private readonly Dictionary<ChannelId, ChannelTap> _taps = new();
    private readonly List<WasapiOut> _ownedOutputs = new();
    private readonly object _gate = new();
    private WasapiOut? _master;
    private bool _disposed;

    private VirtualMixGraph()
    {
        WaveFormat = _format;
    }

    public WaveFormat WaveFormat { get; }

    public static VirtualMixGraph? TryStart(
        MMDevice master,
        IReadOnlyDictionary<ChannelId, MMDevice> captures,
        IReadOnlyDictionary<ChannelId, string> renderIds,
        MixerConfig config,
        IReadOnlyDictionary<string, MMDevice> outputs)
    {
        var graph = new VirtualMixGraph();
        try
        {
            foreach (var (channel, source) in captures)
            {
                if (channel is not (ChannelId.Game or ChannelId.Chat or ChannelId.Media or ChannelId.Aux))
                    continue;
                renderIds.TryGetValue(channel, out var renderId);
                var ownId = config.Channel(channel).DeviceId;
                var separate = !string.IsNullOrWhiteSpace(ownId)
                    && !string.Equals(ownId, renderId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(ownId, source.ID, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(ownId, master.ID, StringComparison.OrdinalIgnoreCase)
                    && outputs.TryGetValue(ownId, out _);
                graph._taps[channel] = graph.StartTap(channel, source, config, separate, separate ? outputs[ownId!] : null);
            }

            if (graph._taps.Values.Any(tap => !tap.Separate))
            {
                var output = new WasapiOut(master, AudioClientShareMode.Shared, true, 20);
                var provider = (ISampleProvider)graph;
                if (master.AudioClient.MixFormat.SampleRate != Rate)
                    provider = new WdlResamplingSampleProvider(graph, master.AudioClient.MixFormat.SampleRate);
                output.Init(provider.ToWaveProvider());
                output.Play();
                graph._master = output;
                graph._ownedOutputs.Add(output);
            }

            return graph;
        }
        catch (Exception ex)
        {
            Services.Log.Error("Virtual mix failed to start", ex);
            graph.Dispose();
            return null;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        var frames = count / 2;
        var frame = new float[2];
        lock (_gate)
        {
            for (var i = 0; i < frames; i++)
            {
                float left = 0, right = 0;
                foreach (var tap in _taps.Values)
                {
                    if (tap.Separate) continue;
                    if (!tap.Ring.TryRead(frame)) continue;
                    if (tap.Muted) continue;
                    left += frame[0] * tap.Volume;
                    right += frame[1] * tap.Volume;
                }

                buffer[offset + i * 2] = Math.Clamp(left, -1f, 1f);
                buffer[offset + i * 2 + 1] = Math.Clamp(right, -1f, 1f);
            }
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

    private ChannelTap StartTap(ChannelId channel, MMDevice source, MixerConfig config, bool separate, MMDevice? ownOutput)
    {
        var tap = new ChannelTap(channel, config, separate);
        var capture = new WasapiCapture(source)
        {
            ShareMode = AudioClientShareMode.Shared
        };
        capture.DataAvailable += (_, args) => tap.Write(args.Buffer, args.BytesRecorded, capture.WaveFormat);
        capture.StartRecording();
        tap.Capture = capture;

        if (separate && ownOutput != null)
        {
            var output = new WasapiOut(ownOutput, AudioClientShareMode.Shared, true, 20);
            ISampleProvider provider = tap;
            if (ownOutput.AudioClient.MixFormat.SampleRate != Rate)
                provider = new WdlResamplingSampleProvider(tap, ownOutput.AudioClient.MixFormat.SampleRate);
            output.Init(provider.ToWaveProvider());
            output.Play();
            tap.Output = output;
            _ownedOutputs.Add(output);
        }

        return tap;
    }

    private sealed class ChannelTap : ISampleProvider, IDisposable
    {
        private readonly MixerConfig _config;
        private readonly FloatRing _ring = new(Rate / 5 * 2);
        public ChannelTap(ChannelId channel, MixerConfig config, bool separate)
        {
            Channel = channel;
            _config = config;
            Separate = separate;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        }

        public ChannelId Channel { get; }
        public bool Separate { get; }
        public FloatRing Ring => _ring;
        public WaveFormat WaveFormat { get; }
        public WasapiCapture? Capture { get; set; }
        public WasapiOut? Output { get; set; }
        public float Peak { get; private set; }
        public float Volume => _config.Channel(Channel).Volume;
        public bool Muted => _config.Channel(Channel).Muted;

        public void Write(byte[] buffer, int bytes, WaveFormat format)
        {
            var frames = BytesToStereo(buffer, bytes, format, out var samples);
            if (frames == 0) return;
            var peak = 0f;
            for (var i = 0; i < samples.Length; i++)
                peak = Math.Max(peak, Math.Abs(samples[i]));
            Peak = peak;
            _ring.Write(samples);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var frame = new float[2];
            var frames = count / 2;
            for (var i = 0; i < frames; i++)
            {
                if (!_ring.TryRead(frame) || Muted)
                {
                    buffer[offset + i * 2] = 0;
                    buffer[offset + i * 2 + 1] = 0;
                    continue;
                }

                buffer[offset + i * 2] = Math.Clamp(frame[0] * Volume, -1f, 1f);
                buffer[offset + i * 2 + 1] = Math.Clamp(frame[1] * Volume, -1f, 1f);
            }
            return count;
        }

        public void Dispose()
        {
            try { Capture?.StopRecording(); } catch { /* already stopped */ }
            try { Capture?.Dispose(); } catch { /* already stopped */ }
        }

        private static int BytesToStereo(byte[] buffer, int bytes, WaveFormat format, out float[] samples)
        {
            var channels = Math.Max(1, format.Channels);
            int frames;
            samples = [];
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                frames = bytes / (4 * channels);
                samples = new float[frames * 2];
                for (var frame = 0; frame < frames; frame++)
                {
                    var left = BitConverter.ToSingle(buffer, (frame * channels) * 4);
                    var right = channels > 1 ? BitConverter.ToSingle(buffer, (frame * channels + 1) * 4) : left;
                    samples[frame * 2] = left;
                    samples[frame * 2 + 1] = right;
                }
                return frames;
            }

            if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
            {
                frames = bytes / (2 * channels);
                samples = new float[frames * 2];
                for (var frame = 0; frame < frames; frame++)
                {
                    var left = BitConverter.ToInt16(buffer, (frame * channels) * 2) / 32768f;
                    var right = channels > 1 ? BitConverter.ToInt16(buffer, (frame * channels + 1) * 2) / 32768f : left;
                    samples[frame * 2] = left;
                    samples[frame * 2 + 1] = right;
                }
                return frames;
            }

            return 0;
        }
    }

    private sealed class FloatRing
    {
        private readonly float[] _data;
        private readonly object _gate = new();
        private int _read;
        private int _count;

        public FloatRing(int capacity) => _data = new float[capacity];

        public void Write(float[] samples)
        {
            lock (_gate)
            {
                foreach (var sample in samples)
                {
                    if (_count == _data.Length)
                    {
                        _read = (_read + 2) % _data.Length;
                        _count -= 2;
                        if (_count < 0) _count = 0;
                    }

                    var write = (_read + _count) % _data.Length;
                    _data[write] = sample;
                    _count++;
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
}
