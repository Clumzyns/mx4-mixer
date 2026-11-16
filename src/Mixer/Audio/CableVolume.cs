using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace Mixer.Audio;

/// <summary>
/// Pushes channel fader and mute to the VAC render endpoint on a background thread.
/// The capture endpoint stays at full volume. External Windows changes come back through OnVolumeNotification.
/// </summary>
internal sealed class CableVolume : IDisposable
{
    internal static readonly Guid Own = new("8f4e2a1c-6b3d-4e90-9c17-5a2d8f0b6e41");

    private readonly Dispatcher _dispatcher;
    private readonly Action<ChannelId, float, bool> _external;
    private readonly object _gate = new();
    private readonly Dictionary<ChannelId, Binding> _bindings = new();
    private readonly Dictionary<ChannelId, Pending> _pending = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private bool _disposed;

    public CableVolume(Dispatcher dispatcher, Action<ChannelId, float, bool> external)
    {
        _dispatcher = dispatcher;
        _external = external;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Mixer.CableVolume"
        };
        _thread.Start();
    }

    public void Sync(IReadOnlyList<Link> links)
    {
        lock (_gate)
        {
            var keep = new HashSet<ChannelId>();
            foreach (var link in links)
            {
                keep.Add(link.Channel);
                if (_bindings.TryGetValue(link.Channel, out var existing) &&
                    ReferenceEquals(existing.Render, link.Render) &&
                    ReferenceEquals(existing.Capture, link.Capture))
                    continue;
                if (_bindings.Remove(link.Channel, out var previous))
                    previous.Dispose();
                var created = Binding.TryCreate(link, _dispatcher, _external);
                if (created == null) continue;
                _bindings[link.Channel] = created;
                if (created.HardwareVolume)
                    Enqueue(link.Channel, link.Volume, created.HardwareMute ? link.Muted : null);
                else if (created.HardwareMute)
                    Enqueue(link.Channel, null, link.Muted);
            }

            foreach (var channel in _bindings.Keys.Where(channel => !keep.Contains(channel)).ToList())
            {
                _bindings[channel].Dispose();
                _bindings.Remove(channel);
                _pending.Remove(channel);
            }
        }
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
            if (!_bindings.TryGetValue(id, out var binding) || !binding.HardwareVolume) return false;
            Enqueue(id, volume, null);
            return true;
        }
    }

    public bool TrySetMute(ChannelId id, bool muted)
    {
        lock (_gate)
        {
            if (!_bindings.TryGetValue(id, out var binding) || !binding.HardwareMute) return false;
            Enqueue(id, null, muted);
            return true;
        }
    }

    public void Release(MMDevice device)
    {
        lock (_gate)
        {
            foreach (var channel in _bindings.Where(pair =>
                         ReferenceEquals(pair.Value.Render, device) || ReferenceEquals(pair.Value.Capture, device))
                     .Select(pair => pair.Key).ToList())
            {
                _bindings[channel].Dispose();
                _bindings.Remove(channel);
                _pending.Remove(channel);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        try { _thread.Join(500); } catch { /* background */ }
        lock (_gate)
        {
            foreach (var binding in _bindings.Values)
                binding.Dispose();
            _bindings.Clear();
        }
        _wake.Dispose();
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
        _wake.Set();
    }

    private void Loop()
    {
        while (!_disposed)
        {
            _wake.WaitOne(250);
            if (_disposed) break;
            List<(Binding Binding, Pending Pending)> batch;
            lock (_gate)
            {
                batch = new List<(Binding, Pending)>();
                foreach (var pair in _pending)
                {
                    if (_bindings.TryGetValue(pair.Key, out var binding))
                        batch.Add((binding, pair.Value));
                }
                _pending.Clear();
            }

            foreach (var (binding, pending) in batch)
            {
                try { binding.Apply(pending.Volume, pending.Muted); }
                catch (Exception ex) { Services.Log.Error("Cable volume write failed", ex); }
            }
        }
    }

    internal readonly record struct Link(ChannelId Channel, MMDevice Render, MMDevice? Capture, float Volume, bool Muted);

    private struct Pending
    {
        public float? Volume;
        public bool? Muted;
    }

    private sealed class Binding : IDisposable
    {
        private readonly ChannelId _channel;
        private readonly Dispatcher _dispatcher;
        private readonly Action<ChannelId, float, bool> _external;
        private readonly AudioEndpointVolume _renderVolume;
        private readonly AudioEndpointVolume? _captureVolume;
        private bool _disposed;

        private Binding(ChannelId channel, MMDevice render, MMDevice? capture, Dispatcher dispatcher, Action<ChannelId, float, bool> external, AudioEndpointVolume renderVolume, AudioEndpointVolume? captureVolume, bool hardwareVolume, bool hardwareMute)
        {
            _channel = channel;
            Render = render;
            Capture = capture;
            _dispatcher = dispatcher;
            _external = external;
            _renderVolume = renderVolume;
            _captureVolume = captureVolume;
            HardwareVolume = hardwareVolume;
            HardwareMute = hardwareMute;
            _renderVolume.NotificationGuid = Own;
            _renderVolume.OnVolumeNotification += OnRender;
            if (_captureVolume != null)
            {
                _captureVolume.NotificationGuid = Own;
                _captureVolume.OnVolumeNotification += OnCapture;
                HoldCapture();
            }
        }

        public MMDevice Render { get; }
        public MMDevice? Capture { get; }
        public bool HardwareVolume { get; }
        public bool HardwareMute { get; }

        public static Binding? TryCreate(Link link, Dispatcher dispatcher, Action<ChannelId, float, bool> external)
        {
            try
            {
                var renderVolume = link.Render.AudioEndpointVolume;
                var support = renderVolume.HardwareSupport;
                var hardwareVolume = (support & EEndpointHardwareSupport.Volume) != 0;
                var hardwareMute = (support & EEndpointHardwareSupport.Mute) != 0;
                AudioEndpointVolume? captureVolume = null;
                if (link.Capture != null)
                {
                    try { captureVolume = link.Capture.AudioEndpointVolume; }
                    catch { captureVolume = null; }
                }
                return new Binding(link.Channel, link.Render, link.Capture, dispatcher, external, renderVolume, captureVolume, hardwareVolume, hardwareMute);
            }
            catch (Exception ex)
            {
                Services.Log.Error("Cable endpoint volume is unavailable for " + link.Channel, ex);
                return null;
            }
        }

        public void Apply(float? volume, bool? muted)
        {
            if (_disposed) return;
            if (volume is float level && HardwareVolume)
                _renderVolume.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
            if (muted is bool flag && HardwareMute)
                _renderVolume.Mute = flag;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _renderVolume.OnVolumeNotification -= OnRender; } catch { /* already gone */ }
            if (_captureVolume != null)
            {
                try { _captureVolume.OnVolumeNotification -= OnCapture; } catch { /* already gone */ }
            }
        }

        private void OnRender(AudioVolumeNotificationData data)
        {
            if (_disposed || IsOwn(data)) return;
            var volume = data.MasterVolume;
            var muted = data.Muted;
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed) _external(_channel, volume, muted);
            });
        }

        private void OnCapture(AudioVolumeNotificationData data)
        {
            if (_disposed || _captureVolume == null || IsOwn(data)) return;
            if (data.MasterVolume >= 0.995f && !data.Muted) return;
            try { HoldCapture(); }
            catch { /* the next bind will try again */ }
        }

        private void HoldCapture()
        {
            if (_captureVolume == null) return;
            if (_captureVolume.MasterVolumeLevelScalar < 0.995f)
                _captureVolume.MasterVolumeLevelScalar = 1f;
            if (_captureVolume.Mute)
                _captureVolume.Mute = false;
        }

        private static bool IsOwn(AudioVolumeNotificationData data) =>
            data.EventContext == Own || data.Guid == Own;
    }
}
