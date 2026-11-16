using System.Runtime.InteropServices;
using System.Windows.Threading;
using Mixer.Audio.Com;
using Mixer.Config;
using Mixer.Services;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Mixer.Audio;

public sealed class AudioEngine : IDisposable
{
    private readonly MixerConfig _config;
    private readonly Dispatcher _dispatcher;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly DeviceNotifications _notifications;
    private readonly Dictionary<string, MMDevice> _render = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MMDevice> _capture = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, ProcessInfo> _procs = new();
    private readonly Dictionary<string, SessionMemory> _memory = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _routed = new();
    private readonly Dictionary<string, float> _appShown = new(StringComparer.OrdinalIgnoreCase);
    private readonly float[] _channelShown = new float[5];
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _scanTimer;
    private readonly DispatcherTimer _saveTimer;
    private AppEndpointRouter? _router;
    private List<LiveSession> _live = [];
    private string? _masterOutputId;
    private string? _masterInputId;
    private bool _micMuted;
    private float _micShown;
    private string _status = "Starting audio engine…";
    private bool _settingOutput;
    private bool _settingInput;
    private bool _gainQueued;
    private int _scanDepth;
    private bool _scanAgain;
    private bool _disposed;
    private readonly Dictionary<ChannelId, string> _virtualInputs = new();
    private VirtualMixGraph? _mix;
    private string _mixSignature = "";
    private bool _virtualMixActive;
    private bool _sonarMicrophone;
    private string _sonarWarning = "";
    private string _backendName = "";

    public AudioEngine(MixerConfig config)
    {
        _config = config;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _enumerator = new MMDeviceEnumerator();
        _notifications = new DeviceNotifications(this);
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => TickMeters(), _dispatcher);
        _scanTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) => Scan(), _dispatcher);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            try { ConfigStore.Save(_config); }
            catch (Exception ex) { Log.Error("Save failed", ex); }
        };
    }

    public event EventHandler<MixerSnapshot>? StructureChanged;
    public event EventHandler<PeakUpdate>? PeaksChanged;

    public void Start()
    {
        Log.Info($"Mixer {AppInfo.Version} on Windows build {Environment.OSVersion.Version.Build}.");
        try
        {
            var hr = _enumerator.RegisterEndpointNotificationCallback(_notifications);
            if (hr < 0) Log.Info($"Device notifications unavailable (0x{hr:X8}).");
        }
        catch (Exception ex)
        {
            Log.Error("Device notifications unavailable", ex);
        }

        _router = AppEndpointRouter.TryCreate();
        _status = _router == null
            ? "Per-app device routing is unavailable on this Windows build. Volume and mute still work."
            : "Default follows the master output. A channel can pin its own device.";
        RefreshDevices();
        TryRestoreMasters();
        Scan();
        _meterTimer.Start();
        _scanTimer.Start();
    }

    public void RequestScan()
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(new Action(Scan));
    }

    public void SetMasterOutput(string deviceId) => SetMaster(DataFlow.Render, deviceId, output: true);

    public void SetMasterInput(string deviceId) => SetMaster(DataFlow.Capture, deviceId, output: false);

    public void SetMicMuted(bool muted)
    {
        if (_masterInputId == null || !_capture.TryGetValue(_masterInputId, out var device))
        {
            _status = "No microphone is selected.";
            Notify();
            return;
        }

        try
        {
            device.AudioEndpointVolume.Mute = muted;
            _micMuted = muted;
        }
        catch (Exception ex)
        {
            Log.Error("Mic mute failed", ex);
            _status = "Couldn't mute the microphone.";
        }
        Notify();
    }

    public void SetChannelVolume(ChannelId id, float volume)
    {
        _config.Channel(id).Volume = VolumeMath.Clamp(volume);
        QueueGain();
        ScheduleSave();
    }

    /// <summary>
    /// Game and Chat faders follow the Nova Pro dial. 0-100 on each side, center is 100/100.
    /// </summary>
    public void ApplyChatMix(int game, int chat)
    {
        var gameVolume = VolumeMath.Clamp(game / 100f);
        var chatVolume = VolumeMath.Clamp(chat / 100f);
        var gameChanged = Math.Abs(_config.Channel(ChannelId.Game).Volume - gameVolume) > 0.005f;
        var chatChanged = Math.Abs(_config.Channel(ChannelId.Chat).Volume - chatVolume) > 0.005f;
        if (!gameChanged && !chatChanged) return;
        _config.Channel(ChannelId.Game).Volume = gameVolume;
        _config.Channel(ChannelId.Chat).Volume = chatVolume;
        QueueGain();
        ScheduleSave();
        Notify();
    }

    public void SetChannelMuted(ChannelId id, bool muted)
    {
        _config.Channel(id).Muted = muted;
        QueueGain();
        ScheduleSave();
        Notify();
    }

    public void SetChannelDevice(ChannelId id, string? deviceId)
    {
        _config.Channel(id).DeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        _routed.Clear();
        RouteAll();
        ScheduleSave();
        _status = string.IsNullOrWhiteSpace(deviceId)
            ? $"{id.Title()} follows the master output."
            : $"{id.Title()} is pinned to its own device.";
        Notify();
    }

    public void Assign(string exeKey, ChannelId channel)
    {
        if (string.IsNullOrWhiteSpace(exeKey)) return;
        _config.Assignments[exeKey.Trim().ToLowerInvariant()] = channel.Key();
        _routed.Clear();
        ApplyGains();
        RouteAll();
        ScheduleSave();
        Notify();
    }

    public void SetAppBaseVolume(string exeKey, float volume)
    {
        if (string.IsNullOrWhiteSpace(exeKey)) return;
        _config.BaseVolumes[exeKey.Trim().ToLowerInvariant()] = VolumeMath.Clamp(volume);
        QueueGain();
        ScheduleSave();
    }

    public int CloseSteelSeries()
    {
        var closed = SteelSeriesApps.Close();
        _sonarWarning = closed > 0
            ? $"Closed {closed} SteelSeries process{(closed == 1 ? "" : "es")}."
            : "No SteelSeries process was running.";
        RequestScan();
        return closed;
    }

    public void SetAppMuted(string exeKey, bool muted)
    {
        if (string.IsNullOrWhiteSpace(exeKey)) return;
        _config.AppMuted[exeKey.Trim().ToLowerInvariant()] = muted;
        QueueGain();
        ScheduleSave();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _meterTimer.Stop();
        _scanTimer.Stop();
        _saveTimer.Stop();
        StopMix();
        try { ConfigStore.Save(_config); } catch { /* exit path */ }
        try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); } catch { /* already gone */ }
        DisposeDevices(_render);
        DisposeDevices(_capture);
        try { _router?.Dispose(); } catch { /* ignore */ }
        try { _enumerator.Dispose(); } catch { /* ignore */ }
    }

    private void SetMaster(DataFlow flow, string deviceId, bool output)
    {
        var map = output ? _render : _capture;
        if (string.IsNullOrWhiteSpace(deviceId) || !map.ContainsKey(deviceId))
        {
            _status = "That device is no longer available.";
            Notify();
            return;
        }

        if (!SameDevice(TryDefault(flow), deviceId))
        {
            if (output) _settingOutput = true;
            else _settingInput = true;
            try
            {
                DefaultEndpoint.Set(deviceId);
                _status = output ? "Default output updated for console, media, and calls." : "Default microphone updated for console, media, and calls.";
            }
            catch (Exception ex)
            {
                Log.Error("Set default device failed", ex);
                _status = "Couldn't change the Windows default device.";
            }
            finally
            {
                if (output) _settingOutput = false;
                else _settingInput = false;
            }
        }

        if (output)
        {
            _masterOutputId = deviceId;
            _config.MasterOutputDeviceId = deviceId;
            _routed.Clear();
            RouteAll();
        }
        else
        {
            _masterInputId = deviceId;
            _config.MasterInputDeviceId = deviceId;
            ReadMicMute();
        }

        ScheduleSave();
        Notify();
    }

    private void TryRestoreMasters()
    {
        var output = _config.MasterOutputDeviceId;
        if (!string.IsNullOrEmpty(output) && _render.ContainsKey(output))
            SetMaster(DataFlow.Render, output, output: true);
        else
            _masterOutputId = TryDefault(DataFlow.Render);

        var input = _config.MasterInputDeviceId;
        if (!string.IsNullOrEmpty(input) && _capture.ContainsKey(input))
            SetMaster(DataFlow.Capture, input, output: false);
        else
            _masterInputId = TryDefault(DataFlow.Capture);
    }

    private void Scan()
    {
        if (_disposed) return;
        if (_scanDepth > 0)
        {
            _scanAgain = true;
            return;
        }

        _scanDepth++;
        try
        {
            RefreshDevices();
            DiscoverVirtualInputs();
            UpdateMix();
            _live = CollectSessions();
            ApplyGains();
            RouteAll();
            PruneMemory();
            Notify();
        }
        catch (Exception ex)
        {
            Log.Error("Session scan failed", ex);
            _status = "The audio session scan hit an error. It will try again.";
        }
        finally
        {
            _scanDepth--;
            if (_scanAgain && !_disposed)
            {
                _scanAgain = false;
                Scan();
            }
        }
    }

    private List<LiveSession> CollectSessions()
    {
        var live = new List<LiveSession>();
        foreach (var device in _render.Values.ToList())
        {
            try
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                if (sessions == null) continue;
                var count = sessions.Count;
                for (var i = 0; i < count; i++)
                {
                    AudioSessionControl session;
                    try { session = sessions[i]; }
                    catch { continue; }

                    try
                    {
                        if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                    }
                    catch { continue; }

                    var system = false;
                    try { system = session.IsSystemSoundsSession; } catch { /* not session2 */ }

                    uint pid = 0;
                    if (!system)
                    {
                        try { pid = session.GetProcessID; } catch { pid = 0; }
                    }

                    string instanceId;
                    try { instanceId = session.GetSessionInstanceIdentifier; }
                    catch { instanceId = ""; }
                    if (string.IsNullOrWhiteSpace(instanceId))
                        instanceId = device.ID + ":" + pid + ":" + i;

                    if (!_procs.TryGetValue(pid, out var info) || system)
                    {
                        info = ProcessPath.Describe(system ? 0 : pid, system);
                        if (!system && pid != 0) _procs[pid] = info;
                    }

                    live.Add(new LiveSession
                    {
                        Control = session,
                        InstanceId = instanceId,
                        ExeKey = info.ExeKey,
                        DisplayName = info.DisplayName,
                        ExePath = info.Path,
                        Pid = system ? 0 : pid
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error("Device session list failed", ex);
            }
        }
        return live;
    }

    private void ApplyGains()
    {
        foreach (var session in _live)
            ApplySession(session);
    }

    private void ApplySession(LiveSession session)
    {
        try
        {
            var volume = session.Control.SimpleAudioVolume;
            if (volume == null) return;
            var channel = Resolve(session.ExeKey);
            var settings = _config.Channel(channel);
            var current = volume.Volume;
            if (!_config.BaseVolumes.TryGetValue(session.ExeKey, out var baseVolume))
            {
                baseVolume = VolumeMath.Clamp(current);
                _config.BaseVolumes[session.ExeKey] = baseVolume;
                ScheduleSave();
            }

            if (!_memory.TryGetValue(session.InstanceId, out var memory))
            {
                memory = new SessionMemory();
                _memory[session.InstanceId] = memory;
            }

            var scale = _virtualMixActive ? 1f : settings.Volume;
            var external = memory.HasWritten && Math.Abs(current - memory.LastVolume) > 0.02f;
            if (external && scale > 0.001f)
            {
                baseVolume = VolumeMath.BaseFromEffective(current, scale);
                _config.BaseVolumes[session.ExeKey] = baseVolume;
                memory.LastVolume = current;
                memory.HasWritten = true;
                ScheduleSave();
            }
            else
            {
                var desired = VolumeMath.Effective(_config.BaseVolumes[session.ExeKey], scale);
                if (Math.Abs(current - desired) > 0.01f)
                {
                    volume.Volume = desired;
                    memory.LastVolume = desired;
                }
                else
                {
                    memory.LastVolume = current;
                }
                memory.HasWritten = true;
            }

            var appMuted = _config.AppMuted.TryGetValue(session.ExeKey, out var muted) && muted;
            var wantMute = _virtualMixActive ? appMuted : settings.Muted || appMuted;
            if (volume.Mute != wantMute)
                volume.Mute = wantMute;
        }
        catch (Exception ex)
        {
            Log.Error("Session volume failed for " + session.ExeKey, ex);
        }
    }

    private void RouteAll()
    {
        if (_router == null) return;
        var seen = new HashSet<uint>();
        foreach (var session in _live)
        {
            if (session.Pid == 0 || session.ExeKey == "system.sounds" || !seen.Add(session.Pid))
                continue;
            var desired = DesiredDevice(session.ExeKey);
            var key = desired ?? "";
            if (_routed.TryGetValue(session.Pid, out var previous) && string.Equals(previous, key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!_router.SetEndpoint(session.Pid, desired))
                continue;
            var actual = _router.TryGetEndpoint(session.Pid);
            if (actual == null || EndpointMatches(actual, desired))
                _routed[session.Pid] = key;
        }
    }

    private string? DesiredDevice(string exeKey)
    {
        var channel = Resolve(exeKey);
        if (_virtualInputs.TryGetValue(channel, out var virtualId))
            return virtualId;
        var id = _config.Channel(channel).DeviceId;
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private bool EndpointMatches(string actual, string? desired)
    {
        if (string.IsNullOrEmpty(desired))
            return string.IsNullOrEmpty(actual) || SameDevice(actual, _masterOutputId);
        return SameDevice(actual, desired);
    }

    private ChannelId Resolve(string exeKey)
    {
        if (_config.Assignments.TryGetValue(exeKey, out var raw) && ChannelIds.TryParse(raw, out var id))
            return id;
        if (DefaultApps.Map.TryGetValue(exeKey, out var mapped))
            return mapped;
        return ChannelId.System;
    }

    private void TickMeters()
    {
        if (_disposed) return;
        float micRaw = 0;
        if (!_micMuted && _masterInputId != null && _capture.TryGetValue(_masterInputId, out var mic))
        {
            try { micRaw = mic.AudioMeterInformation.MasterPeakValue; }
            catch { micRaw = 0; }
        }
        _micShown = Decay(_micShown, micRaw);

        var raw = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in _live)
        {
            float peak = 0;
            try { peak = session.Control.AudioMeterInformation?.MasterPeakValue ?? 0; }
            catch { peak = 0; }
            if (!raw.TryGetValue(session.ExeKey, out var current) || peak > current)
                raw[session.ExeKey] = peak;
        }

        foreach (var key in _appShown.Keys.ToList())
        {
            raw.TryGetValue(key, out var sample);
            var shown = Decay(_appShown[key], sample);
            if (shown <= 0f) _appShown.Remove(key);
            else _appShown[key] = shown;
        }
        foreach (var pair in raw)
        {
            if (!_appShown.ContainsKey(pair.Key))
                _appShown[pair.Key] = pair.Value;
        }

        Array.Clear(_channelShown);
        _mix?.CopyPeaks(_channelShown);
        foreach (var pair in _appShown)
        {
            var index = (int)Resolve(pair.Key);
            if (pair.Value > _channelShown[index])
                _channelShown[index] = pair.Value;
        }
        for (var i = 0; i < _channelShown.Length; i++)
        {
            if (_config.Channel((ChannelId)i).Muted)
                _channelShown[i] = 0;
        }

        PeaksChanged?.Invoke(this, new PeakUpdate
        {
            MicPeak = _micShown,
            ChannelPeaks = (float[])_channelShown.Clone(),
            AppPeaks = new Dictionary<string, float>(_appShown, StringComparer.OrdinalIgnoreCase)
        });
    }

    private void RefreshDevices()
    {
        Replace(_render, DataFlow.Render);
        Replace(_capture, DataFlow.Capture);
        if (!_settingOutput)
        {
            var current = TryDefault(DataFlow.Render);
            if (!string.IsNullOrEmpty(current) && !SameDevice(_masterOutputId, current))
            {
                _masterOutputId = current;
                _config.MasterOutputDeviceId = current;
                ScheduleSave();
            }
        }
        if (!_settingInput)
        {
            var current = TryDefault(DataFlow.Capture);
            if (!string.IsNullOrEmpty(current) && !SameDevice(_masterInputId, current))
            {
                _masterInputId = current;
                _config.MasterInputDeviceId = current;
                ScheduleSave();
            }
        }
        ReadMicMute();
    }

    private void Replace(Dictionary<string, MMDevice> map, DataFlow flow)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MMDeviceCollection? collection = null;
        try
        {
            collection = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        }
        catch (Exception ex)
        {
            Log.Error("Enumerate " + flow, ex);
            return;
        }

        var count = collection.Count;
        for (var i = 0; i < count; i++)
        {
            MMDevice device;
            try { device = collection[i]; }
            catch { continue; }
            var id = device.ID;
            seen.Add(id);
            if (map.ContainsKey(id))
            {
                try { device.Dispose(); } catch { /* duplicate wrapper */ }
            }
            else
            {
                map[id] = device;
            }
        }

        foreach (var id in map.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            try { map[id].Dispose(); } catch { /* already dead */ }
            map.Remove(id);
        }
    }

    private void ReadMicMute()
    {
        if (_masterInputId == null || !_capture.TryGetValue(_masterInputId, out var device)) return;
        try { _micMuted = device.AudioEndpointVolume.Mute; }
        catch { /* keep the last known mute */ }
    }

    private string? TryDefault(DataFlow flow)
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)) return null;
            using var device = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    private void Notify()
    {
        var groups = new Dictionary<string, List<LiveSession>>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in _live)
        {
            if (!groups.TryGetValue(session.ExeKey, out var list))
            {
                list = [];
                groups[session.ExeKey] = list;
            }
            list.Add(session);
        }

        var apps = new List<AppSnapshot>();
        foreach (var pair in groups.OrderBy(item => item.Value[0].DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var first = pair.Value[0];
            _config.BaseVolumes.TryGetValue(pair.Key, out var baseVolume);
            var muted = _config.AppMuted.TryGetValue(pair.Key, out var flag) && flag;
            apps.Add(new AppSnapshot
            {
                ExeKey = pair.Key,
                DisplayName = first.DisplayName,
                ExePath = first.ExePath,
                ProcessId = first.Pid,
                SessionCount = pair.Value.Count,
                BaseVolume = baseVolume,
                Muted = muted,
                Channel = Resolve(pair.Key)
            });
        }

        var outputs = _render.Values.Select(DeviceSnap).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var inputs = _capture.Values.Select(DeviceSnap).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var channels = ChannelIds.All.Select(id =>
        {
            var settings = _config.Channel(id);
            return new ChannelSnapshot
            {
                Id = id,
                Volume = settings.Volume,
                Muted = settings.Muted,
                DeviceId = settings.DeviceId
            };
        }).ToList();

        StructureChanged?.Invoke(this, new MixerSnapshot
        {
            Outputs = outputs,
            Inputs = inputs,
            MasterOutputId = _masterOutputId,
            MasterInputId = _masterInputId,
            MicMuted = _micMuted,
            Channels = channels,
            Apps = apps,
            Status = _status,
            RoutingAvailable = _router != null,
            VirtualMixActive = _virtualMixActive,
            SonarWarning = _sonarWarning,
            BackendName = _backendName
        });
    }

    private static DeviceSnapshot DeviceSnap(MMDevice device)
    {
        string name;
        try { name = device.FriendlyName; }
        catch { name = device.ID; }
        if (string.IsNullOrWhiteSpace(name)) name = device.ID;
        return new DeviceSnapshot { Id = device.ID, Name = name };
    }

    private void PruneMemory()
    {
        var instances = new HashSet<string>(_live.Select(session => session.InstanceId), StringComparer.Ordinal);
        foreach (var key in _memory.Keys.Where(key => !instances.Contains(key)).ToList())
            _memory.Remove(key);
        var pids = new HashSet<uint>(_live.Select(session => session.Pid));
        foreach (var pid in _procs.Keys.Where(pid => !pids.Contains(pid)).ToList())
            _procs.Remove(pid);
        foreach (var pid in _routed.Keys.Where(pid => !pids.Contains(pid)).ToList())
            _routed.Remove(pid);
    }

    private void QueueGain()
    {
        if (_gainQueued) return;
        _gainQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _gainQueued = false;
            if (!_disposed) ApplyGains();
        }));
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static void DisposeDevices(Dictionary<string, MMDevice> map)
    {
        foreach (var device in map.Values)
        {
            try { device.Dispose(); } catch { /* ignore */ }
        }
        map.Clear();
    }

    private static float Decay(float shown, float sample)
    {
        var next = Math.Max(sample, shown * 0.84f);
        return next < 0.004f ? 0f : next;
    }

    private static bool SameDevice(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(EndpointIds.Key(left), EndpointIds.Key(right), StringComparison.OrdinalIgnoreCase);
    }

    private void DiscoverVirtualInputs()
    {
        _virtualInputs.Clear();
        _sonarMicrophone = false;
        _backendName = "";
        foreach (var device in _render.Values)
        {
            var name = DeviceSnap(device).Name;
            if (!VirtualBackends.TryMap(name, out var channel, out var backend)) continue;
            _virtualInputs[channel] = device.ID;
            _backendName = backend;
        }

        foreach (var device in _capture.Values)
        {
            if (VirtualBackends.IsSonarMicrophone(DeviceSnap(device).Name))
                _sonarMicrophone = true;
        }

        var gg = SteelSeriesApps.Running();
        if (gg.Count > 0)
            _sonarWarning = "SteelSeries is running (" + string.Join(", ", gg.Distinct(StringComparer.OrdinalIgnoreCase)) + "). It will grab the dial and mix on top of Mixer.";
        else if (_virtualInputs.Count == 0)
            _sonarWarning = "Sonar virtual devices were not found. Install SteelSeries Sonar once, then quit GG. Mixer will use SteelSeries Sonar - Gaming, Chat, Media, and Aux.";
        else if (_masterOutputId != null && _virtualInputs.Values.Any(id => SameDevice(id, _masterOutputId)))
            _sonarWarning = "The master output is a Sonar device. Choose your IEM DAC, for example Fosi Audio K5 Pro.";
        else
        {
            _sonarWarning = "";
            _status = "Mixing " + _backendName + " into the master output."
                + (_sonarMicrophone ? " Sonar microphone is present." : "");
        }
    }

    private void UpdateMix()
    {
        var masterIsVirtual = _masterOutputId != null && _virtualInputs.Values.Any(id => SameDevice(id, _masterOutputId));
        var canMix = _virtualInputs.Count > 0 && _masterOutputId != null && !masterIsVirtual && _render.ContainsKey(_masterOutputId);
        var signature = canMix
            ? _backendName + "|" + _masterOutputId + "|" + string.Join(",", _virtualInputs.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value))
              + "|" + string.Join(",", ChannelIds.All.Select(id => id + ":" + (_config.Channel(id).DeviceId ?? "")))
            : "";
        _virtualMixActive = canMix;
        if (signature == _mixSignature) return;
        StopMix();
        _mixSignature = signature;
        if (!canMix) return;
        var sources = new Dictionary<ChannelId, MMDevice>();
        foreach (var (channel, id) in _virtualInputs)
        {
            if (_render.TryGetValue(id, out var device))
                sources[channel] = device;
        }
        _mix = VirtualMixGraph.TryStart(_render[_masterOutputId!], sources, _config, _render);
        if (_mix == null)
        {
            _virtualMixActive = false;
            _sonarWarning = "Couldn't start the mix to the master output.";
        }
        else
        {
            Log.Info("Mixing " + _backendName + " channels to " + _masterOutputId);
        }
    }

    private void StopMix()
    {
        try { _mix?.Dispose(); } catch { /* already stopped */ }
        _mix = null;
        _mixSignature = "";
    }

    private sealed class LiveSession
    {
        public required AudioSessionControl Control { get; init; }
        public required string InstanceId { get; init; }
        public required string ExeKey { get; init; }
        public required string DisplayName { get; init; }
        public string? ExePath { get; init; }
        public uint Pid { get; init; }
    }

    private sealed class SessionMemory
    {
        public float LastVolume;
        public bool HasWritten;
    }
}

[ComVisible(true)]
public sealed class DeviceNotifications : IMMNotificationClient
{
    private readonly AudioEngine _engine;

    public DeviceNotifications(AudioEngine engine) => _engine = engine;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _engine.RequestScan();

    public void OnDeviceAdded(string pwstrDeviceId) => _engine.RequestScan();

    public void OnDeviceRemoved(string deviceId) => _engine.RequestScan();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _engine.RequestScan();

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }
}
