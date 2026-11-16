using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
    private readonly BlockingCollection<Action> _inbox = new();
    private readonly DeviceNotifications _notifications;
    private MMDeviceEnumerator _enumerator = null!;
    private Thread? _audioThread;
    private int _wakeQueued;
    private int _scanRequested;
    private int _scanImmediate;
    private long _nextScanAllowed;
    private long _nextPeaks;
    private readonly Dictionary<string, MMDevice> _render = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MMDevice> _capture = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, ProcessInfo> _procs = new();
    private readonly Dictionary<string, SessionMemory> _memory = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _routed = new();
    private readonly HashSet<string> _sessionHooks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _appShown = new(StringComparer.OrdinalIgnoreCase);
    private readonly float[] _channelShown = new float[5];
    private PeakUpdate? _pendingPeaks;
    private int _peaksDirty;
    private AppEndpointRouter? _router;
    private List<LiveSession> _live = [];
    private string? _masterOutputId;
    private string? _masterInputId;
    private bool _micMuted;
    private float _micShown;
    private float _outLeftShown;
    private float _outRightShown;
    private float _reductionShown;
    private string _status = "Starting audio engine…";
    private bool _settingOutput;
    private bool _settingInput;
    private int _scanDepth;
    private bool _scanAgain;
    private bool _disposed;
    private readonly Dictionary<ChannelId, string> _virtualInputs = new();
    private readonly Dictionary<ChannelId, string> _virtualCaptures = new();
    private List<VacCable> _cables = [];
    private VirtualMixGraph? _mix;
    private string _mixSignature = "";
    private bool _virtualMixActive;
    private string _sonarWarning = "";
    private string _backendName = "";
    private readonly LiveMix _gains = new();
    private readonly CableVolume _cableVolume;

    public AudioEngine(MixerConfig config)
    {
        _config = config;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _cableVolume = new CableVolume(ApplyEndpointVolume, _gains);
        _notifications = new DeviceNotifications(this);
        _gains.Load(config);
        SetKnobInverted(config.InvertVolumeKnob);
    }

    public event EventHandler<MixerSnapshot>? StructureChanged;

    public void Start()
    {
        Log.Info($"Mixer {AppInfo.Version} on Windows build {Environment.OSVersion.Version.Build}.");
        _audioThread = new Thread(RunAudio)
        {
            IsBackground = true,
            Name = "Mixer.Audio"
        };
        _audioThread.SetApartmentState(ApartmentState.MTA);
        _audioThread.Start();
    }

    private void RunAudio()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
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
                ? "Per-app routing is unavailable on this Windows build. Volume and mute still work."
                : "Windows playback is the Game cable and calls use Chat. The mix plays on the master output.";
            RefreshDevices();
            TryRestoreMasters();
            Scan();
            _nextScanAllowed = Environment.TickCount64 + 800;
            while (!_disposed)
            {
                DrainInbox(WaitMs());
                if (_disposed) break;
                ServiceAudio();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio thread stopped", ex);
        }
        finally
        {
            StopMix();
            try { _enumerator?.UnregisterEndpointNotificationCallback(_notifications); } catch { /* already gone */ }
            DisposeDevices(_render);
            DisposeDevices(_capture);
            try { _router?.Dispose(); } catch { /* ignore */ }
            try { _enumerator?.Dispose(); } catch { /* ignore */ }
            try { ConfigStore.Save(_config); } catch { /* exit path */ }
        }
    }

    private int WaitMs()
    {
        var now = Environment.TickCount64;
        var wait = (int)Math.Max(1, _nextPeaks - now);
        if (wait <= 0 || _nextPeaks == 0) wait = 33;
        if (Volatile.Read(ref _scanRequested) != 0)
        {
            var due = Volatile.Read(ref _scanImmediate) != 0 ? now : _nextScanAllowed;
            wait = (int)Math.Min(wait, Math.Max(1, due - now));
        }
        return Math.Clamp(wait, 1, 800);
    }

    private void DrainInbox(int waitMs)
    {
        if (!_inbox.TryTake(out var work, waitMs))
        {
            Interlocked.Exchange(ref _wakeQueued, 0);
            return;
        }

        Interlocked.Exchange(ref _wakeQueued, 0);
        Invoke(work);
        while (_inbox.TryTake(out work))
            Invoke(work);
    }

    private void Invoke(Action work)
    {
        try { work(); }
        catch (Exception ex) { Log.Error("Audio command failed", ex); }
    }

    private void ServiceAudio()
    {
        var now = Environment.TickCount64;
        if (now >= _nextPeaks)
        {
            _nextPeaks = now + 33;
            try { PublishPeaks(); } catch (Exception ex) { Log.Error("Peak meter failed", ex); }
            try { _mix?.LogDrift(); } catch (Exception ex) { Log.Error("Drift log failed", ex); }
        }

        var immediate = Volatile.Read(ref _scanImmediate) != 0;
        var due = Volatile.Read(ref _scanRequested) != 0 && (immediate || now >= _nextScanAllowed);
        if (!due) return;
        Interlocked.Exchange(ref _scanImmediate, 0);
        Interlocked.Exchange(ref _scanRequested, 0);
        Scan();
        _nextScanAllowed = Environment.TickCount64 + 800;
    }

    private void Post(Action work)
    {
        if (_disposed) return;
        try { _inbox.Add(work); }
        catch (InvalidOperationException) { /* shutting down */ }
    }

    private void Wake()
    {
        if (Interlocked.Exchange(ref _wakeQueued, 1) == 1) return;
        try { _inbox.Add(static () => { }); }
        catch (InvalidOperationException) { /* shutting down */ }
    }

    private void RequestScanSoon(bool immediate)
    {
        Interlocked.Exchange(ref _scanRequested, 1);
        if (immediate) Interlocked.Exchange(ref _scanImmediate, 1);
        Wake();
    }

    public bool TryConsumePeaks(out PeakUpdate peaks)
    {
        var pending = _pendingPeaks;
        if (Interlocked.Exchange(ref _peaksDirty, 0) == 0 || pending == null)
        {
            peaks = new PeakUpdate();
            return false;
        }
        peaks = pending;
        return true;
    }

    public void RequestScan() => RequestScanSoon(immediate: false);

    public void SetMasterOutput(string deviceId) => Post(() =>
    {
        SetMaster(DataFlow.Render, deviceId, output: true);
        RequestScanSoon(immediate: true);
    });

    public void SetMasterInput(string deviceId) => Post(() =>
    {
        SetMaster(DataFlow.Capture, deviceId, output: false);
        RequestScanSoon(immediate: true);
    });

    public void SetMicMuted(bool muted) => Post(() =>
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
    });

    public void SetMasterVolume(float volume)
    {
        var next = VolumeMath.Clamp(volume);
        _gains.SetMaster(next);
        if (Math.Abs(_config.MasterVolume - next) <= 0.0005f) return;
        _config.MasterVolume = next;
        if (!_virtualMixActive) QueueGain();
        ScheduleSave();
    }

    /// <summary>
    /// 07 25 position. NN is attenuation unless inverted. The live master gain updates before this returns.
    /// </summary>
    public int ApplyKnobPosition(int position)
    {
        position = Math.Clamp(position, 0, 255);
        var max = Math.Max(1, _config.VolumeKnobMax);
        if (position > max)
        {
            max = position;
            _config.VolumeKnobMax = max;
        }

        var percent = ChatMixMath.VolumeFromKnob(position, max, Volatile.Read(ref _invertKnob) != 0);
        SetMasterVolume(percent / 100f);
        return percent;
    }

    private int _invertKnob;
    private int _appliedGame = int.MinValue;
    private int _appliedChat = int.MinValue;

    public void SetKnobInverted(bool invert) => Volatile.Write(ref _invertKnob, invert ? 1 : 0);

    public void SetChannelVolume(ChannelId id, float volume)
    {
        var next = VolumeMath.Clamp(volume);
        _gains.SetFader(id, next);
        _config.Channel(id).Volume = next;
        var silent = next <= 0f || _config.Channel(id).Muted;
        _cableVolume.TrySetVolume(id, silent ? 0f : 1f);
        _cableVolume.TrySetMute(id, silent);
        if (!_virtualMixActive) QueueGain();
        ScheduleSave();
    }

    /// <summary>
    /// Game and Chat faders follow the ChatMix balance. 0-100 on each side, center is 100/100.
    /// </summary>
    public void ApplyChatMix(int game, int chat)
    {
        game = Math.Clamp(game, 0, 100);
        chat = Math.Clamp(chat, 0, 100);
        _gains.SetChatMix(ChannelId.Game, game / 100f);
        _gains.SetChatMix(ChannelId.Chat, chat / 100f);
        _gains.SetChatMix(ChannelId.Media, 1f);
        _gains.SetChatMix(ChannelId.Aux, 1f);
        _gains.SetChatMix(ChannelId.System, 1f);
        if (game == _appliedGame && chat == _appliedChat) return;
        _appliedGame = game;
        _appliedChat = chat;
        var balance = ChatMixMath.FromLevels(game, chat);
        if (_config.ChatMixBalance != balance)
            _config.ChatMixBalance = balance;
        ScheduleSave();
    }

    public void SetChannelMuted(ChannelId id, bool muted)
    {
        _gains.SetMuted(id, muted);
        _config.Channel(id).Muted = muted;
        var silent = muted || _config.Channel(id).Volume <= 0f;
        _cableVolume.TrySetMute(id, silent);
        if (!silent) _cableVolume.TrySetVolume(id, 1f);
        if (!_virtualMixActive) QueueGain();
        ScheduleSave();
    }

    public void SetChannelDevice(ChannelId id, string? deviceId) => Post(() =>
    {
        if (id is ChannelId.Game or ChannelId.Chat or ChannelId.Media or ChannelId.Aux)
            deviceId = null;
        _config.Channel(id).DeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        _routed.Clear();
        RouteAll();
        ScheduleSave();
        _status = string.IsNullOrWhiteSpace(deviceId)
            ? $"{id.Title()} follows the master output."
            : $"{id.Title()} is pinned to its own device.";
        Notify();
        RequestScanSoon(immediate: true);
    });

    public void Assign(string exeKey, ChannelId channel)
    {
        if (string.IsNullOrWhiteSpace(exeKey)) return;
        var key = exeKey.Trim().ToLowerInvariant();
        Post(() =>
        {
            _config.Assignments[key] = channel.Key();
            _routed.Clear();
            ApplyGains();
            RouteAll();
            ScheduleSave();
            Notify();
        });
    }

    public void SetAppBaseVolume(string exeKey, float volume)
    {
        if (string.IsNullOrWhiteSpace(exeKey)) return;
        var key = exeKey.Trim().ToLowerInvariant();
        var level = VolumeMath.Clamp(volume);
        Post(() =>
        {
            _config.BaseVolumes[key] = level;
            ApplyGains();
            ScheduleSave();
        });
    }

    public void SetMixLatency(int milliseconds) => Post(() =>
    {
        _config.MixLatencyMs = milliseconds is 10 or 40 ? milliseconds : 20;
        ScheduleSave();
        RequestScanSoon(immediate: true);
    });

    public void SetGameStraightThrough(bool enabled) => Post(() =>
    {
        _config.GameStraightThrough = enabled;
        ScheduleSave();
        RequestScanSoon(immediate: true);
    });

    public void SetLimiterEnabled(bool enabled) => Post(() =>
    {
        _config.LimiterEnabled = enabled;
        ScheduleSave();
        RequestScanSoon(immediate: true);
    });

    public void SetCable(ChannelId channel, string renderId)
    {
        if (channel is not (ChannelId.Game or ChannelId.Chat or ChannelId.Media or ChannelId.Aux)) return;
        Post(() =>
        {
            _config.CableAssignments[channel.Key()] = renderId;
            _routed.Clear();
            ScheduleSave();
            RequestScanSoon(immediate: true);
        });
    }

    public int CloseSteelSeries()
    {
        Post(() =>
        {
            var closed = SteelSeriesApps.Close();
            _sonarWarning = closed > 0
                ? $"Closed {closed} SteelSeries process{(closed == 1 ? "" : "es")}."
                : "No SteelSeries process was running.";
            Notify();
            RequestScanSoon(immediate: true);
        });
        return 0;
    }

    public void SetAppMuted(string exeKey, bool muted)
    {
        if (string.IsNullOrWhiteSpace(exeKey)) return;
        var key = exeKey.Trim().ToLowerInvariant();
        Post(() =>
        {
            _config.AppMuted[key] = muted;
            ApplyGains();
            ScheduleSave();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Wake();
        try { _audioThread?.Join(2000); } catch { /* background */ }
        try { _cableVolume.Dispose(); } catch { /* already stopped */ }
        _inbox.Dispose();
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

        var cablesOwnPlayback = output && _virtualInputs.ContainsKey(ChannelId.Game);
        if (!cablesOwnPlayback && !SameDevice(TryDefault(flow), deviceId))
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
        else if (cablesOwnPlayback)
        {
            _status = "Master output updated. Windows playback is Game and calls use Chat.";
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

            var throughMix = _virtualMixActive && _virtualInputs.ContainsKey(channel);
            var factor = _gains.MixGain(channel);
            var scale = throughMix ? 1f : factor * _gains.Master;
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
        var tree = ProcessPath.Snapshot();
        var targets = new Dictionary<uint, string>();
        void Claim(uint pid, string exeKey)
        {
            if (pid == 0 || pid == (uint)Environment.ProcessId) return;
            if (string.Equals(exeKey, "system.sounds", StringComparison.OrdinalIgnoreCase)) return;
            var device = DesiredDevice(exeKey) ?? "";
            if (!targets.ContainsKey(pid))
                targets[pid] = exeKey + "\n" + device;
            foreach (var child in ProcessPath.Descendants(pid, tree))
            {
                if (child == 0 || child == (uint)Environment.ProcessId) continue;
                if (!targets.ContainsKey(child))
                    targets[child] = exeKey + "\n" + device;
            }
        }

        foreach (var session in _live)
            Claim(session.Pid, session.ExeKey);
        foreach (var node in tree)
        {
            if (_config.Assignments.ContainsKey(node.ExeKey) || DefaultApps.Map.ContainsKey(node.ExeKey))
                Claim(node.Pid, node.ExeKey);
        }

        foreach (var (pid, packed) in targets)
        {
            var split = packed.Split('\n');
            var exe = split[0];
            var device = split.Length > 1 && split[1].Length > 0 ? split[1] : null;
            var key = RouteKey(device);
            if (_routed.TryGetValue(pid, out var previous) && string.Equals(previous, key, StringComparison.OrdinalIgnoreCase))
                continue;
            var persisted = _router.TryGetEndpoint(pid);
            if (EndpointMatches(persisted, device))
            {
                _routed[pid] = key;
                continue;
            }
            if (!_router.SetEndpoint(pid, device))
                continue;
            _routed[pid] = key;
            Log.Info($"Routed {exe} pid {pid} to {Resolve(exe)}");
        }
    }

    private static string RouteKey(string? device) =>
        string.IsNullOrEmpty(device) ? "" : EndpointIds.Key(device);

    private void WatchSessions(MMDevice device)
    {
        if (!_sessionHooks.Add(device.ID)) return;
        try
        {
            device.AudioSessionManager.OnSessionCreated += OnSessionCreated;
        }
        catch (Exception ex)
        {
            _sessionHooks.Remove(device.ID);
            Log.Error("Session notifications unavailable for " + device.ID, ex);
        }
    }

    private void OnSessionCreated(object sender, IAudioSessionControl session) => Post(RefreshRoutes);

    private void RefreshRoutes()
    {
        _live = CollectSessions();
        ApplyGains();
        RouteAll();
        Notify();
    }

    private string? DesiredDevice(string exeKey)
    {
        var channel = Resolve(exeKey);
        if (channel is ChannelId.Game or ChannelId.Chat or ChannelId.Media or ChannelId.Aux &&
            _virtualInputs.TryGetValue(channel, out var cable))
            return cable;
        if (channel is ChannelId.Game or ChannelId.Chat or ChannelId.Media or ChannelId.Aux)
            return null;
        var id = _config.Channel(channel).DeviceId;
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private bool EndpointMatches(string? actual, string? desired)
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

    private void PublishPeaks()
    {
        if (_disposed) return;
        float micRaw = 0;
        var micMuted = _micMuted;
        var micId = _masterInputId;
        if (!micMuted && micId != null && _capture.TryGetValue(micId, out var mic))
        {
            try { micRaw = mic.AudioMeterInformation.MasterPeakValue; }
            catch { micRaw = 0; }
        }
        _micShown = Decay(_micShown, micRaw);

        var sessions = _live;
        var raw = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
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
            if (_gains.Muted((ChannelId)i) && _gains.MixGain((ChannelId)i) <= 0f)
                _channelShown[i] = 0;
        }

        var (outLeft, outRight, gain, limiting) = _mix?.TakeOutputMeter() ?? (0f, 0f, 1f, false);
        _outLeftShown = Decay(_outLeftShown, outLeft);
        _outRightShown = Decay(_outRightShown, outRight);
        var reduction = gain < 1f ? (float)(-20.0 * Math.Log10(Math.Max(gain, 0.0001f))) : 0f;
        _reductionShown = Math.Max(reduction, _reductionShown * 0.84f);
        if (_reductionShown < 0.05f) _reductionShown = 0f;

        _pendingPeaks = new PeakUpdate
        {
            MicPeak = _micShown,
            OutLeft = _outLeftShown,
            OutRight = _outRightShown,
            LimiterReductionDb = _reductionShown,
            LimiterOn = limiting,
            ChannelPeaks = (float[])_channelShown.Clone(),
            AppPeaks = new Dictionary<string, float>(_appShown, StringComparer.OrdinalIgnoreCase)
        };
        Volatile.Write(ref _peaksDirty, 1);
    }

    private void ApplyEndpointVolume(ChannelId id, float volume, bool muted) =>
        Post(() => ApplyEndpointVolumeCore(id, volume, muted));

    private void ApplyEndpointVolumeCore(ChannelId id, float volume, bool muted)
    {
        var next = VolumeMath.Clamp(volume);
        var settings = _config.Channel(id);
        var volumeChanged = Math.Abs(settings.Volume - next) > 0.01f;
        var muteChanged = settings.Muted != muted;
        if (!volumeChanged && !muteChanged) return;
        settings.Volume = next;
        settings.Muted = muted;
        _gains.SetFader(id, next);
        _gains.SetMuted(id, muted);
        ScheduleSave();
        Notify();
    }

    private void RefreshDevices()
    {
        Replace(_render, DataFlow.Render);
        Replace(_capture, DataFlow.Capture);
        if (!_settingOutput)
        {
            // The output chosen in Mixer wins over the Windows default, which is usually the Game cable.
            var saved = _config.MasterOutputDeviceId;
            var chosen = !string.IsNullOrEmpty(saved) && !IsVirtualRender(saved);
            if (chosen && _render.ContainsKey(saved!))
            {
                if (!SameDevice(_masterOutputId, saved))
                {
                    _masterOutputId = saved;
                    _routed.Clear();
                    Log.Info("Master output is the saved device " + saved);
                }
            }
            else
            {
                // Nothing chosen yet, or the chosen device is unplugged. Follow Windows until it returns.
                var current = TryDefault(DataFlow.Render);
                if (!string.IsNullOrEmpty(current) && !SameDevice(_masterOutputId, current) && !IsVirtualRender(current))
                {
                    _masterOutputId = current;
                    if (!chosen)
                    {
                        _config.MasterOutputDeviceId = current;
                        ScheduleSave();
                    }
                }
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
                if (flow == DataFlow.Render)
                    WatchSessions(map[id]);
            }
            else
            {
                map[id] = device;
                if (flow == DataFlow.Render)
                    WatchSessions(device);
            }
        }

        foreach (var id in map.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            _cableVolume.Release(id);
            _sessionHooks.Remove(id);
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
            var label = "";
            if (_virtualInputs.TryGetValue(id, out var renderId))
            {
                var cable = _cables.FirstOrDefault(item => SameDevice(item.RenderId, renderId));
                label = cable != null && cable.Order is > 0 and < 1000 ? "Line " + cable.Order : "";
            }
            return new ChannelSnapshot
            {
                Id = id,
                Volume = settings.Volume,
                Muted = settings.Muted,
                DeviceId = settings.DeviceId,
                CableLabel = label
            };
        }).ToList();

        var snapshot = new MixerSnapshot
        {
            Outputs = outputs,
            Inputs = inputs,
            MasterOutputId = _masterOutputId,
            MasterInputId = _masterInputId,
            MasterVolume = _config.MasterVolume,
            MicMuted = _micMuted,
            Channels = channels,
            Apps = apps,
            Status = _status,
            RoutingAvailable = _router != null,
            VirtualMixActive = _virtualMixActive,
            SonarWarning = _sonarWarning,
            BackendName = _backendName,
            Cables = _cables.Select(cable => new DeviceSnapshot { Id = cable.RenderId, Name = cable.Label }).ToList()
        };
        if (_dispatcher.CheckAccess())
            StructureChanged?.Invoke(this, snapshot);
        else
            _dispatcher.BeginInvoke(() => StructureChanged?.Invoke(this, snapshot));
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
        var alive = new HashSet<uint>(ProcessPath.Snapshot().Select(node => node.Pid));
        foreach (var pid in pids)
            alive.Add(pid);
        foreach (var pid in _routed.Keys.Where(pid => !alive.Contains(pid)).ToList())
            _routed.Remove(pid);
    }

    private int _gainQueued;

    private void QueueGain()
    {
        Interlocked.Exchange(ref _gainQueued, 1);
        Post(RunGain);
    }

    private void RunGain()
    {
        while (Interlocked.Exchange(ref _gainQueued, 0) == 1)
            ApplyGains();
    }

    private void ScheduleSave() => ConfigStore.SaveSoon(_config);

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
        _virtualCaptures.Clear();
        _backendName = "";
        ClearPinnedMixOutputs();
        var render = _render.Values.Select(device => (device.ID, DeviceSnap(device).Name, VacLine.Read(device, _enumerator)));
        var capture = _capture.Values.Select(device => (device.ID, DeviceSnap(device).Name, VacLine.Read(device, _enumerator)));
        _cables = VacCables.Discover(render, capture, _config.CableCaptures).ToList();
        BindChannelsToLines();

        var gg = SteelSeriesApps.Running();
        if (_cables.Count == 0)
            _sonarWarning = "No Virtual Audio Cable lines found. Install VAC, set the cable count to 4, then restart Mixer.";
        else if (_cables.Count < 4)
            _sonarWarning = $"Found {_cables.Count} VAC line{(_cables.Count == 1 ? "" : "s")}. Open VAC Control Panel and set Cables to 4 so Game, Chat, Media, and Aux each have one.";
        else if (gg.Count > 0)
            _sonarWarning = "SteelSeries is running (" + string.Join(", ", gg.Distinct(StringComparer.OrdinalIgnoreCase)) + "). Quit it so the ChatMix dial stays with Mixer.";
        else if (_masterOutputId != null && _virtualInputs.Values.Any(id => SameDevice(id, _masterOutputId)))
            _sonarWarning = "The master output is a virtual cable. Choose your IEM DAC, for example Fosi Audio K5 Pro.";
        else
        {
            _sonarWarning = "";
            if (_virtualInputs.Count > 0)
                _status = "Mixing VAC lines into the master output. Windows playback is Game and calls use Chat.";
        }

        RememberCaptures();
        SyncCableVolumes();
        ApplyWindowsDefaults();
        OfferCableRename();
    }

    private string _bindingReport = "";
    private string _windowsDefaultReport = "";

    private void BindChannelsToLines()
    {
        var usedLines = new HashSet<int>();
        var usedRenders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var report = new StringBuilder();
        foreach (var channel in new[] { ChannelId.Game, ChannelId.Chat, ChannelId.Media, ChannelId.Aux })
        {
            var key = channel.Key();
            var line = _config.CableLines.TryGetValue(key, out var saved) ? saved : 0;
            var byGuid = _config.CableAssignments.TryGetValue(key, out var renderId)
                ? _cables.FirstOrDefault(cable => SameDevice(cable.RenderId, renderId))
                : null;
            if (line <= 0 && byGuid != null && byGuid.Order is > 0 and < 1000)
                line = byGuid.Order;
            var cable = line > 0
                ? _cables.FirstOrDefault(item => item.Order == line && !usedLines.Contains(item.Order))
                : null;
            if (cable == null && byGuid != null && !usedRenders.Contains(EndpointIds.Key(byGuid.RenderId)))
                cable = byGuid;
            if (cable == null && line <= 0 && byGuid == null)
            {
                cable = _cables.FirstOrDefault(item =>
                    item.Order is > 0 and < 1000 &&
                    !usedLines.Contains(item.Order) &&
                    !string.IsNullOrWhiteSpace(item.CaptureId));
                if (cable != null)
                    line = cable.Order;
            }
            if (cable == null)
            {
                report.AppendLine($"Mix capture {channel} not started: no VAC line is available.");
                continue;
            }
            if (cable.Order is > 0 and < 1000)
            {
                line = cable.Order;
                usedLines.Add(cable.Order);
            }
            usedRenders.Add(EndpointIds.Key(cable.RenderId));
            if (line > 0 && (!_config.CableLines.TryGetValue(key, out var have) || have != line))
            {
                _config.CableLines[key] = line;
                changed = true;
            }
            if (!_config.CableAssignments.TryGetValue(key, out var assigned) || !SameDevice(assigned, cable.RenderId))
            {
                _config.CableAssignments[key] = cable.RenderId;
                changed = true;
                _routed.Clear();
                report.AppendLine($"Channel {channel} remapped to Line {cable.Order} render={cable.RenderId}");
            }
            if (!string.IsNullOrWhiteSpace(cable.CaptureId) &&
                (!_config.CableCaptures.TryGetValue(key, out var savedCapture) || !SameDevice(savedCapture, cable.CaptureId)))
            {
                _config.CableCaptures[key] = cable.CaptureId;
                changed = true;
            }
            _virtualInputs[channel] = cable.RenderId;
            var lineText = cable.Order is > 0 and < 1000 ? "Line " + cable.Order : "unnumbered";
            if (string.IsNullOrWhiteSpace(cable.CaptureId))
            {
                report.AppendLine($"Mix capture {channel} not started: {lineText} render={cable.RenderId} has no recording endpoint.");
                continue;
            }
            _virtualCaptures[channel] = cable.CaptureId;
            _backendName = "VAC";
            report.AppendLine($"Mix capture {channel} follows {lineText} render={cable.RenderId} capture={cable.CaptureId}");
        }
        var text = report.ToString().TrimEnd();
        if (!string.Equals(text, _bindingReport, StringComparison.Ordinal))
        {
            _bindingReport = text;
            if (text.Length > 0) Log.Info(text);
        }
        if (changed)
            ScheduleSave();
    }

    private void ApplyWindowsDefaults()
    {
        if (!_virtualInputs.TryGetValue(ChannelId.Game, out var game) || string.IsNullOrWhiteSpace(game))
            return;
        _virtualInputs.TryGetValue(ChannelId.Chat, out var chat);
        var report = new StringBuilder();
        _settingOutput = true;
        try
        {
            if (!IsRenderRole(Role.Console, game) || !IsRenderRole(Role.Multimedia, game))
            {
                DefaultEndpoint.Set(game, 0, 1);
                report.AppendLine($"Windows default playback is Game ({game}).");
            }
            if (!string.IsNullOrWhiteSpace(chat) && !IsRenderRole(Role.Communications, chat))
            {
                DefaultEndpoint.Set(chat, 2);
                report.AppendLine($"Windows communications playback is Chat ({chat}).");
            }
        }
        catch (Exception ex)
        {
            if (_windowsDefaultReport != "error")
            {
                _windowsDefaultReport = "error";
                Log.Error("Set Windows default devices failed", ex);
            }
            return;
        }
        finally
        {
            _settingOutput = false;
        }

        var text = report.ToString().TrimEnd();
        if (text.Length == 0 || text == _windowsDefaultReport) return;
        _windowsDefaultReport = text;
        Log.Info(text);
    }

    private bool IsRenderRole(Role role, string id)
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, role)) return false;
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            return SameDevice(device.ID, id);
        }
        catch
        {
            return false;
        }
    }

    private bool IsVirtualRender(string id) =>
        _virtualInputs.Values.Any(item => SameDevice(item, id))
        || _cables.Any(cable => SameDevice(cable.RenderId, id));

    private void ClearPinnedMixOutputs()
    {
        var changed = false;
        foreach (var channel in new[] { ChannelId.Game, ChannelId.Chat, ChannelId.Media, ChannelId.Aux })
        {
            if (string.IsNullOrWhiteSpace(_config.Channel(channel).DeviceId)) continue;
            _config.Channel(channel).DeviceId = null;
            changed = true;
        }
        if (!changed) return;
        _routed.Clear();
        Log.Info("Cleared per-channel output devices. Game, Chat, Media, and Aux play into their VAC cables.");
        ScheduleSave();
    }

    private void RememberCaptures()
    {
        var changed = false;
        foreach (var channel in new[] { ChannelId.Game, ChannelId.Chat, ChannelId.Media, ChannelId.Aux })
        {
            if (!_virtualCaptures.TryGetValue(channel, out var captureId)) continue;
            var key = channel.Key();
            if (_config.CableCaptures.TryGetValue(key, out var saved) && SameDevice(saved, captureId)) continue;
            _config.CableCaptures[key] = captureId;
            changed = true;
        }
        if (changed) ScheduleSave();
    }

    private void SyncCableVolumes()
    {
        var links = new List<CableVolume.Link>();
        foreach (var channel in new[] { ChannelId.Game, ChannelId.Chat, ChannelId.Media, ChannelId.Aux })
        {
            if (!_virtualInputs.TryGetValue(channel, out var renderId)) continue;
            if (!TryFindDevice(_render, renderId, out var render)) continue;
            MMDevice? capture = null;
            if (_virtualCaptures.TryGetValue(channel, out var captureId))
                TryFindDevice(_capture, captureId, out capture);
            var settings = _config.Channel(channel);
            links.Add(new CableVolume.Link(channel, render.ID, capture?.ID, settings.Volume, settings.Muted));
        }
        _cableVolume.Sync(links);
        foreach (var channel in new[] { ChannelId.Game, ChannelId.Chat, ChannelId.Media, ChannelId.Aux })
        {
            _gains.SetSoftwareFader(channel, !_cableVolume.HasHardwareVolume(channel));
            _gains.SetSoftwareMute(channel, !_cableVolume.HasHardwareMute(channel));
        }
    }

    private bool TryFindDevice(Dictionary<string, MMDevice> map, string id, out MMDevice device)
    {
        if (map.TryGetValue(id, out device!)) return true;
        foreach (var pair in map)
        {
            if (!SameDevice(pair.Key, id)) continue;
            device = pair.Value;
            return true;
        }
        device = null!;
        return false;
    }

    private string _renameTicket = "";

    private void OfferCableRename()
    {
        if (_virtualInputs.Count == 0) return;
        var pending = MismatchSignature();
        if (pending.Length == 0 || pending == _renameTicket) return;
        _renameTicket = pending;
        try { ConfigStore.Save(_config); }
        catch (Exception ex) { Log.Error("Save failed", ex); }
        try
        {
            Log.Info("Endpoint names do not match their channels. Asking Windows for permission to rename them.");
            VacRenamer.LaunchElevated();
        }
        catch (Exception ex)
        {
            Log.Error("Cable rename was not started", ex);
        }
    }

    private string MismatchSignature()
    {
        var parts = new List<string>();
        foreach (var (channel, expected) in VacRenamer.Names)
        {
            if (_virtualInputs.TryGetValue(channel, out var renderId) &&
                TryFindDevice(_render, renderId, out var render) &&
                !VacRenamer.Matches(Friendly(render), expected))
                parts.Add(EndpointIds.Key(render.ID) + "=" + expected);
            if (_virtualCaptures.TryGetValue(channel, out var captureId) &&
                TryFindDevice(_capture, captureId, out var capture) &&
                !VacRenamer.Matches(Friendly(capture), expected))
                parts.Add(EndpointIds.Key(capture.ID) + "=" + expected);
        }
        return string.Join("|", parts);
    }

    private static string Friendly(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch { return device.ID; }
    }

    private void UpdateMix()
    {
        var masterIsVirtual = _masterOutputId != null && _virtualInputs.Values.Any(id => SameDevice(id, _masterOutputId));
        var canMix = _virtualCaptures.Count > 0 && _masterOutputId != null && !masterIsVirtual && _render.ContainsKey(_masterOutputId);
        var latency = _config.MixLatencyMs is 10 or 40 ? _config.MixLatencyMs : 20;
        var signature = canMix
            ? _masterOutputId + "|" + string.Join(",", _virtualCaptures.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value))
              + "|" + string.Join(",", ChannelIds.All.Select(id => id + ":" + (_config.Channel(id).DeviceId ?? "")))
              + "|lat=" + latency + "|bypass=" + (_config.GameStraightThrough ? 1 : 0)
              + "|limit=" + (_config.LimiterEnabled ? 1 : 0)
            : "";
        _virtualMixActive = canMix;
        if (signature == _mixSignature) return;
        StopMix();
        _mixSignature = signature;
        if (!canMix) return;
        var captures = new Dictionary<ChannelId, MMDevice>();
        foreach (var (channel, id) in _virtualCaptures)
        {
            if (_capture.TryGetValue(id, out var device))
                captures[channel] = device;
        }
        _mix = VirtualMixGraph.TryStart(_render[_masterOutputId!], captures, _virtualInputs, _config, _render, _gains);
        if (_mix == null)
        {
            _virtualMixActive = false;
            _sonarWarning = "Couldn't start the mix to the master output.";
        }
        else
        {
            Log.Info("Mixing VAC lines to " + _masterOutputId);
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
