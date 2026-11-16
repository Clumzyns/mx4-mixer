using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Mixer.Audio.Com;

namespace Mixer.Audio;

/// <summary>
/// Pushes channel fader and mute to the VAC render endpoint.
/// The enumerator, device, and endpoint volume are created on this MTA worker.
/// UI threads only pass endpoint id strings.
/// </summary>
internal sealed class CableVolume : IDisposable
{
    internal static readonly Guid Own = new("8f4e2a1c-6b3d-4e90-9c17-5a2d8f0b6e41");

    private readonly Dispatcher _dispatcher;
    private readonly Action<ChannelId, float, bool> _external;
    private readonly LiveMix _gains;
    private readonly object _gate = new();
    private readonly Dictionary<ChannelId, Binding> _bindings = new();
    private readonly Dictionary<ChannelId, Pending> _pending = new();
    private readonly List<Binding> _retired = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private bool _disposed;

    public CableVolume(Dispatcher dispatcher, Action<ChannelId, float, bool> external, LiveMix gains)
    {
        _dispatcher = dispatcher;
        _external = external;
        _gains = gains;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Mixer.CableVolume"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Sync(IReadOnlyList<Link> links)
    {
        var wake = false;
        lock (_gate)
        {
            var keep = new HashSet<ChannelId>();
            foreach (var link in links)
            {
                keep.Add(link.Channel);
                if (_bindings.TryGetValue(link.Channel, out var existing) &&
                    Same(existing.RenderId, link.RenderId) &&
                    Same(existing.CaptureId, link.CaptureId))
                {
                    if (existing.SameCommand(link.Volume, link.Muted)) continue;
                    existing.Remember(link.Volume, link.Muted);
                    Enqueue(link.Channel, link.Volume, link.Muted);
                    wake = true;
                    continue;
                }

                if (_bindings.Remove(link.Channel, out var previous))
                    _retired.Add(previous);
                var created = new Binding(link.Channel, link.RenderId, link.CaptureId, _dispatcher, _external, _gains, RequestHold);
                created.Remember(link.Volume, link.Muted);
                _bindings[link.Channel] = created;
                Enqueue(link.Channel, link.Volume, link.Muted);
                wake = true;
            }

            foreach (var channel in _bindings.Keys.Where(channel => !keep.Contains(channel)).ToList())
            {
                _retired.Add(_bindings[channel]);
                _bindings.Remove(channel);
                _pending.Remove(channel);
                wake = true;
            }
        }
        if (wake) _wake.Set();
    }

    public bool HasHardwareVolume(ChannelId id)
    {
        lock (_gate)
            return _bindings.TryGetValue(id, out var binding) && binding.HardwareVolume;
    }

    public bool HasHardwareMute(ChannelId id)
    {
        lock (_gate)
            return _bindings.TryGetValue(id, out var binding) && binding.HardwareMute;
    }

    public bool TrySetVolume(ChannelId id, float volume)
    {
        lock (_gate)
        {
            if (!_bindings.TryGetValue(id, out var binding)) return false;
            binding.Remember(volume, null);
            Enqueue(id, volume, null);
        }
        _wake.Set();
        return true;
    }

    public bool TrySetMute(ChannelId id, bool muted)
    {
        lock (_gate)
        {
            if (!_bindings.TryGetValue(id, out var binding)) return false;
            binding.Remember(null, muted);
            Enqueue(id, null, muted);
        }
        _wake.Set();
        return true;
    }

    public void Release(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) return;
        lock (_gate)
        {
            foreach (var channel in _bindings.Where(pair =>
                         Same(pair.Value.RenderId, endpointId) || Same(pair.Value.CaptureId, endpointId))
                     .Select(pair => pair.Key).ToList())
            {
                _retired.Add(_bindings[channel]);
                _bindings.Remove(channel);
                _pending.Remove(channel);
            }
        }
        _wake.Set();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        try { _thread.Join(1000); } catch { /* background */ }
        _wake.Dispose();
    }

    private void RequestHold(ChannelId id)
    {
        lock (_gate)
        {
            if (!_bindings.ContainsKey(id)) return;
            if (_pending.TryGetValue(id, out var pending))
            {
                pending.HoldCapture = true;
                _pending[id] = pending;
            }
            else
            {
                _pending[id] = new Pending { HoldCapture = true };
            }
        }
        _wake.Set();
    }

    private void Enqueue(ChannelId id, float? volume, bool? muted)
    {
        if (_pending.TryGetValue(id, out var pending))
        {
            if (volume.HasValue) pending.Volume = volume;
            if (muted.HasValue) pending.Muted = muted;
            _pending[id] = pending;
        }
        else
        {
            _pending[id] = new Pending { Volume = volume, Muted = muted };
        }
    }

    private void Loop()
    {
        MMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            while (!_disposed)
            {
                _wake.WaitOne(250);
                if (_disposed) break;
                List<Binding> retired;
                List<(Binding Binding, Pending Pending)> batch;
                lock (_gate)
                {
                    retired = _retired.ToList();
                    _retired.Clear();
                    batch = new List<(Binding, Pending)>();
                    foreach (var pair in _pending)
                    {
                        if (_bindings.TryGetValue(pair.Key, out var binding))
                            batch.Add((binding, pair.Value));
                    }
                    _pending.Clear();
                }

                foreach (var binding in retired)
                    binding.Close();
                foreach (var (binding, pending) in batch)
                {
                    try { binding.Apply(enumerator, pending); }
                    catch (Exception ex) { Services.Log.Error("Cable volume write failed", ex); }
                }
            }
        }
        catch (Exception ex)
        {
            Services.Log.Error("Cable volume worker stopped", ex);
        }
        finally
        {
            List<Binding> leftover;
            lock (_gate)
            {
                leftover = _bindings.Values.Concat(_retired).ToList();
                _bindings.Clear();
                _retired.Clear();
            }
            foreach (var binding in leftover)
                binding.Close();
            try { enumerator?.Dispose(); } catch { /* already gone */ }
        }
    }

    private static bool Same(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(EndpointIds.Key(left), EndpointIds.Key(right), StringComparison.OrdinalIgnoreCase);
    }

    internal readonly record struct Link(ChannelId Channel, string RenderId, string? CaptureId, float Volume, bool Muted);

    private struct Pending
    {
        public float? Volume;
        public bool? Muted;
        public bool HoldCapture;
    }

    private sealed class Binding
    {
        private readonly ChannelId _channel;
        private readonly Dispatcher _dispatcher;
        private readonly Action<ChannelId, float, bool> _external;
        private readonly LiveMix _gains;
        private readonly Action<ChannelId> _hold;
        private MMDevice? _renderDevice;
        private MMDevice? _captureDevice;
        private AudioEndpointVolume? _renderVolume;
        private AudioEndpointVolume? _captureVolume;
        private int _hardwareVolume;
        private int _hardwareMute;
        private float? _commandedVolume;
        private bool? _commandedMute;
        private float? _appliedVolume;
        private bool? _appliedMute;
        private float? _loggedVolume;
        private bool? _loggedMute;
        private bool _closed;

        public Binding(ChannelId channel, string renderId, string? captureId, Dispatcher dispatcher, Action<ChannelId, float, bool> external, LiveMix gains, Action<ChannelId> hold)
        {
            _channel = channel;
            RenderId = renderId;
            CaptureId = captureId;
            _dispatcher = dispatcher;
            _external = external;
            _gains = gains;
            _hold = hold;
        }

        public string RenderId { get; }
        public string? CaptureId { get; }
        public bool HardwareVolume => Volatile.Read(ref _hardwareVolume) != 0;
        public bool HardwareMute => Volatile.Read(ref _hardwareMute) != 0;

        public void Remember(float? volume, bool? muted)
        {
            if (volume.HasValue) _commandedVolume = volume;
            if (muted.HasValue) _commandedMute = muted;
        }

        public bool SameCommand(float volume, bool muted) =>
            _commandedVolume is float current && Math.Abs(current - volume) < 0.0005f && _commandedMute == muted;

        public void Apply(MMDeviceEnumerator enumerator, Pending pending)
        {
            if (_closed) return;
            if (pending.Volume is float level)
                ApplyVolume(enumerator, Math.Clamp(level, 0f, 1f));
            if (pending.Muted is bool muted)
                ApplyMute(enumerator, muted);
            if (pending.HoldCapture)
                HoldCapture(enumerator);
        }

        public void Close()
        {
            _closed = true;
            ReleaseRender();
            ReleaseCapture();
        }

        private void ApplyVolume(MMDeviceEnumerator enumerator, float level)
        {
            if (_appliedVolume is float applied && Math.Abs(applied - level) < 0.0005f && _renderVolume != null)
                return;

            var hr = 0;
            var accepted = false;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    EnsureRender(enumerator);
                    _renderVolume!.MasterVolumeLevelScalar = level;
                    var read = _renderVolume.MasterVolumeLevelScalar;
                    accepted = HardwareVolume || Math.Abs(read - level) < 0.03f;
                    hr = 0;
                    break;
                }
                catch (Exception ex)
                {
                    hr = ex.HResult;
                    accepted = false;
                    ReleaseRender();
                    if (attempt == 0) continue;
                    Services.Log.Error($"Cable volume write failed for {_channel}", ex);
                }
            }

            _gains.SetSoftwareFader(_channel, !accepted);
            if (!accepted) return;
            _appliedVolume = level;
            if (_loggedVolume is float logged && Math.Abs(logged - level) < 0.005f) return;
            _loggedVolume = level;
            Services.Log.Info($"Fader {_channel} endpoint {RenderId} volume {level:0.00} hr=0x{hr:X8}");
        }

        private void ApplyMute(MMDeviceEnumerator enumerator, bool muted)
        {
            if (_appliedMute == muted && _renderVolume != null) return;
            var hr = 0;
            var accepted = false;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    EnsureRender(enumerator);
                    _renderVolume!.Mute = muted;
                    accepted = HardwareMute || _renderVolume.Mute == muted;
                    hr = 0;
                    break;
                }
                catch (Exception ex)
                {
                    hr = ex.HResult;
                    accepted = false;
                    ReleaseRender();
                    if (attempt == 0) continue;
                    Services.Log.Error($"Cable mute write failed for {_channel}", ex);
                }
            }

            _gains.SetSoftwareMute(_channel, !accepted);
            if (!accepted)
            {
                _gains.SetMuted(_channel, muted);
                return;
            }
            _appliedMute = muted;
            if (_loggedMute == muted) return;
            _loggedMute = muted;
            Services.Log.Info($"Fader {_channel} endpoint {RenderId} mute {muted} hr=0x{hr:X8}");
        }

        private void EnsureRender(MMDeviceEnumerator enumerator)
        {
            if (_renderVolume == null)
            {
                _renderDevice = enumerator.GetDevice(RenderId);
                _renderVolume = _renderDevice.AudioEndpointVolume;
                var support = _renderVolume.HardwareSupport;
                Volatile.Write(ref _hardwareVolume, (support & EEndpointHardwareSupport.Volume) != 0 ? 1 : 0);
                Volatile.Write(ref _hardwareMute, (support & EEndpointHardwareSupport.Mute) != 0 ? 1 : 0);
                _renderVolume.NotificationGuid = Own;
                _renderVolume.OnVolumeNotification += OnRender;
            }
            if (_captureVolume != null || string.IsNullOrWhiteSpace(CaptureId)) return;
            try
            {
                _captureDevice = enumerator.GetDevice(CaptureId);
                _captureVolume = _captureDevice.AudioEndpointVolume;
                _captureVolume.NotificationGuid = Own;
                _captureVolume.OnVolumeNotification += OnCapture;
                if (_captureVolume.MasterVolumeLevelScalar < 0.995f)
                    _captureVolume.MasterVolumeLevelScalar = 1f;
                if (_captureVolume.Mute)
                    _captureVolume.Mute = false;
            }
            catch (Exception ex)
            {
                Services.Log.Error($"Cable capture volume is unavailable for {_channel}", ex);
                ReleaseCapture();
            }
        }

        private void OnRender(AudioVolumeNotificationData data)
        {
            if (_closed || IsOwn(data)) return;
            var volume = data.MasterVolume;
            var muted = data.Muted;
            _dispatcher.BeginInvoke(() =>
            {
                if (!_closed) _external(_channel, volume, muted);
            });
        }

        private void OnCapture(AudioVolumeNotificationData data)
        {
            if (_closed || IsOwn(data)) return;
            if (data.MasterVolume >= 0.995f && !data.Muted) return;
            _hold(_channel);
        }

        private void HoldCapture(MMDeviceEnumerator enumerator)
        {
            if (_closed || string.IsNullOrWhiteSpace(CaptureId)) return;
            try
            {
                EnsureRender(enumerator);
                if (_captureVolume == null) return;
                if (_captureVolume.MasterVolumeLevelScalar < 0.995f)
                    _captureVolume.MasterVolumeLevelScalar = 1f;
                if (_captureVolume.Mute)
                    _captureVolume.Mute = false;
            }
            catch (Exception ex)
            {
                Services.Log.Error($"Cable capture hold failed for {_channel}", ex);
                ReleaseCapture();
            }
        }

        private void ReleaseRender()
        {
            if (_renderVolume != null)
            {
                try { _renderVolume.OnVolumeNotification -= OnRender; } catch { /* already gone */ }
                _renderVolume = null;
            }
            try { _renderDevice?.Dispose(); } catch { /* already gone */ }
            _renderDevice = null;
            _appliedVolume = null;
            _appliedMute = null;
            ReleaseCapture();
        }

        private void ReleaseCapture()
        {
            if (_captureVolume != null)
            {
                try { _captureVolume.OnVolumeNotification -= OnCapture; } catch { /* already gone */ }
                _captureVolume = null;
            }
            try { _captureDevice?.Dispose(); } catch { /* already gone */ }
            _captureDevice = null;
        }

        private static bool IsOwn(AudioVolumeNotificationData data) =>
            data.EventContext == Own || data.Guid == Own;
    }
}
