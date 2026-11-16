using System.Collections.ObjectModel;
using System.Diagnostics;
using Mixer.Audio;
using Mixer.Config;
using Mixer.Services;

namespace Mixer.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AudioEngine _engine;
    private readonly MixerConfig _config;
    private readonly ChatMixMonitor _chatMix;
    private readonly Dictionary<string, AppChipViewModel> _chips = new(StringComparer.OrdinalIgnoreCase);
    private int _syncing;
    private string? _selectedOutputId;
    private string? _selectedInputId;
    private bool _micMuted;
    private double _micPeak;
    private string _status = "Looking for audio devices…";
    private bool _routingAvailable;
    private bool _startWithWindows;
    private bool _startMinimized;
    private bool _closeToTray = true;
    private bool _settingsOpen;
    private bool _chatMixEnabled;
    private bool _chatMixConnected;
    private bool _balanceArmed;
    private int _balanceSync;
    private double _chatMixBalance = 50;
    private bool _masterArmed;
    private int _masterSync;
    private double _masterVolume = 100;
    private bool _invertVolumeKnob;
    private int _lastKnobPosition = -1;
    private int _chatMixGame = -1;
    private int _chatMixChat = -1;
    private string _chatMixDetail = "Looking for the Nova Pro base station…";
    private string _lastReport = "";
    private string _sonarWarning = "";
    private bool _virtualMixActive;
    private string _gameCableId = "";
    private string _chatCableId = "";
    private string _mediaCableId = "";
    private string _auxCableId = "";

    public MainViewModel(AudioEngine engine, MixerConfig config, ChatMixMonitor chatMix)
    {
        _engine = engine;
        _config = config;
        _chatMix = chatMix;
        _startWithWindows = config.StartWithWindows;
        _startMinimized = config.StartMinimized;
        _closeToTray = config.CloseToTray;
        _chatMixEnabled = config.ChatMixEnabled;
        _chatMixBalance = Math.Clamp(config.ChatMixBalance, 0, 100);
        _masterVolume = ChatMixMath.Percent(config.MasterVolume);
        _invertVolumeKnob = config.InvertVolumeKnob;
        _chatMixGame = ChatMixMath.Percent(config.Channel(ChannelId.Game).Volume);
        _chatMixChat = ChatMixMath.Percent(config.Channel(ChannelId.Chat).Volume);
        Channels = new ObservableCollection<ChannelViewModel>(
            ChannelIds.All.Select(id => new ChannelViewModel(id, engine)));
        ChannelChoices = new ObservableCollection<ChannelChoice>(
            ChannelIds.All.Select(id => new ChannelChoice { Id = id, Name = id.Title() }));
        OutputChoices.Add(new DeviceOption { Id = "", Name = "Default" });
        ToggleMicCommand = new RelayCommand(() => MicMuted = !MicMuted);
        ToggleSettingsCommand = new RelayCommand(() => SettingsOpen = !SettingsOpen);
        OpenConfigCommand = new RelayCommand(OpenConfigFolder);
        CloseSonarCommand = new RelayCommand(() => _engine.CloseSteelSeries());
        OpenVacDownloadCommand = new RelayCommand(OpenVacDownload);
        RenameCablesCommand = new RelayCommand(RenameCables);
        _engine.StructureChanged += (_, snap) => Apply(snap);
    }

    public void ConsumePeaks()
    {
        if (!_engine.TryConsumePeaks(out var peaks)) return;
        ApplyPeaks(peaks);
    }

    public ObservableCollection<ChannelViewModel> Channels { get; }
    public ObservableCollection<ChannelChoice> ChannelChoices { get; }
    public ObservableCollection<DeviceOption> OutputChoices { get; } = new();
    public ObservableCollection<DeviceOption> InputDevices { get; } = new();
    public RelayCommand ToggleMicCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand OpenConfigCommand { get; }
    public RelayCommand CloseSonarCommand { get; }
    public RelayCommand OpenVacDownloadCommand { get; }
    public RelayCommand RenameCablesCommand { get; }
    public ObservableCollection<DeviceOption> VacCables { get; } = new();
    public string SetupWarning => _sonarWarning;
    public bool ShowSonarWarning => !string.IsNullOrWhiteSpace(_sonarWarning);
    public bool VirtualMixActive => _virtualMixActive;

    public string GameCableId { get => _gameCableId; set => SetCable(ChannelId.Game, ref _gameCableId, value); }
    public string ChatCableId { get => _chatCableId; set => SetCable(ChannelId.Chat, ref _chatCableId, value); }
    public string MediaCableId { get => _mediaCableId; set => SetCable(ChannelId.Media, ref _mediaCableId, value); }
    public string AuxCableId { get => _auxCableId; set => SetCable(ChannelId.Aux, ref _auxCableId, value); }
    public bool IsSyncing => _syncing > 0;
    public string VersionText => "Version " + AppInfo.Version;
    public string ConfigPath => ConfigStore.FilePath;
    public bool NoOutputs => OutputChoices.Count <= 1;

    public string? SelectedOutputId
    {
        get => _selectedOutputId;
        private set => Set(ref _selectedOutputId, value);
    }

    public string? SelectedInputId
    {
        get => _selectedInputId;
        private set => Set(ref _selectedInputId, value);
    }

    public bool MicMuted
    {
        get => _micMuted;
        set
        {
            if (!Set(ref _micMuted, value)) return;
            Raise(nameof(MicMuteLabel));
            if (!IsSyncing)
                _engine.SetMicMuted(value);
        }
    }

    public string MicMuteLabel => _micMuted ? "Muted" : "Live";
    public double MicPeak { get => _micPeak; private set => Set(ref _micPeak, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool RoutingAvailable { get => _routingAvailable; private set => Set(ref _routingAvailable, value); }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!Set(ref _startWithWindows, value)) return;
            _config.StartWithWindows = value;
            try
            {
                Autostart.Set(value);
                Status = value ? "Mixer will start with Windows." : "Mixer will not start with Windows.";
            }
            catch (Exception ex)
            {
                Log.Error("Startup registration failed", ex);
                Status = "Couldn't update the Windows startup entry.";
            }
            try { ConfigStore.Save(_config); } catch (Exception ex) { Log.Error("Save failed", ex); }
        }
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (!Set(ref _closeToTray, value)) return;
            _config.CloseToTray = value;
            try { ConfigStore.Save(_config); } catch (Exception ex) { Log.Error("Save failed", ex); }
        }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (!Set(ref _startMinimized, value)) return;
            _config.StartMinimized = value;
            try { ConfigStore.Save(_config); } catch (Exception ex) { Log.Error("Save failed", ex); }
        }
    }

    public bool SettingsOpen
    {
        get => _settingsOpen;
        set => Set(ref _settingsOpen, value);
    }

    public bool ChatMixEnabled
    {
        get => _chatMixEnabled;
        set
        {
            if (!Set(ref _chatMixEnabled, value)) return;
            _config.ChatMixEnabled = value;
            _chatMix.Enabled = value;
            if (!value)
                ChatMixDetail = "USB dial reading is off. The slider still sets Game and Chat.";
            try { ConfigStore.Save(_config); } catch (Exception ex) { Log.Error("Save failed", ex); }
        }
    }

    public bool InvertVolumeKnob
    {
        get => _invertVolumeKnob;
        set
        {
            if (!Set(ref _invertVolumeKnob, value)) return;
            _config.InvertVolumeKnob = value;
            _engine.SetKnobInverted(value);
            if (_lastKnobPosition >= 0)
                ApplyKnob(_lastKnobPosition);
            else
                try { ConfigStore.Save(_config); } catch (Exception ex) { Log.Error("Save failed", ex); }
        }
    }

    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            var percent = (int)Math.Round(double.IsFinite(value) ? value : 100, MidpointRounding.AwayFromZero);
            percent = Math.Clamp(percent, 0, 100);
            if (!IsSyncing && _masterSync == 0 && !_masterArmed) return;
            if (Math.Abs(_masterVolume - percent) < 0.05) return;
            _masterVolume = percent;
            Raise(nameof(MasterVolume));
            Raise(nameof(MasterVolumeLabel));
            if (IsSyncing || _masterSync > 0) return;
            _engine.SetMasterVolume(percent / 100f);
        }
    }

    public string MasterVolumeLabel => $"{Math.Round(_masterVolume):0}";

    public void ArmMasterFader()
    {
        var percent = ChatMixMath.Percent(_config.MasterVolume);
        _masterArmed = true;
        if (Math.Abs(_masterVolume - percent) > 0.01)
        {
            _masterSync++;
            try { MasterVolume = percent; }
            finally { _masterSync--; }
        }
        else
        {
            Raise(nameof(MasterVolume));
            Raise(nameof(MasterVolumeLabel));
        }
    }

    public double ChatMixBalance
    {
        get => _chatMixBalance;
        set
        {
            var balance = (int)Math.Round(double.IsFinite(value) ? value : 50, MidpointRounding.AwayFromZero);
            balance = Math.Clamp(balance, 0, 100);
            if (_balanceSync == 0 && !_balanceArmed) return;
            if (!Set(ref _chatMixBalance, balance)) return;
            Raise(nameof(ChatMixBalanceLabel));
            if (_balanceSync > 0) return;
            var (game, chat) = ChatMixMath.ToLevels(balance);
            _chatMixGame = game;
            _chatMixChat = chat;
            Raise(nameof(ChatMixReadout));
            _config.ChatMixBalance = balance;
            _engine.ApplyChatMix(game, chat);
        }
    }

    public string ChatMixBalanceLabel
    {
        get
        {
            var balance = (int)Math.Round(_chatMixBalance);
            var (game, chat) = ChatMixMath.ToLevels(balance);
            if (game >= 100 && chat >= 100) return balance + "   Both full";
            if (chat <= 0) return balance + "   Game only";
            if (game <= 0) return balance + "   Chat only";
            return $"{balance}   G {game}   C {chat}";
        }
    }

    public string LastReportText => string.IsNullOrEmpty(_lastReport)
        ? "Last report: none yet"
        : "Last report: " + _lastReport;

    /// <summary>
    /// Called once the window is shown so the slider's default value cannot overwrite config.
    /// </summary>
    public void ArmChatMixSlider()
    {
        var balance = Math.Clamp(_config.ChatMixBalance, 0, 100);
        _balanceArmed = true;
        if (Math.Abs(_chatMixBalance - balance) > 0.01)
        {
            _balanceSync++;
            try { ChatMixBalance = balance; }
            finally { _balanceSync--; }
        }
        else
        {
            Raise(nameof(ChatMixBalance));
            Raise(nameof(ChatMixBalanceLabel));
        }
    }

    public bool ChatMixConnected
    {
        get => _chatMixConnected;
        private set => Set(ref _chatMixConnected, value);
    }

    public string ChatMixReadout => _chatMixGame >= 0
        ? $"Game {_chatMixGame}   Chat {_chatMixChat}"
        : "No dial yet";

    public string ChatMixDetail
    {
        get => _chatMixDetail;
        private set => Set(ref _chatMixDetail, value);
    }

    public void OnChatMix(ChatMixReading reading)
    {
        ChatMixConnected = reading.Connected;
        if (!string.IsNullOrEmpty(reading.RawReport))
            LastReport = reading.RawReport;

        if (reading.Kind == ChatMixReportKind.Knob)
        {
            ApplyKnob(reading.Knob);
            return;
        }

        if (reading.Kind == ChatMixReportKind.Levels && reading.HasLevels && reading.Game >= 0 && ChatMixEnabled)
        {
            var balance = ChatMixMath.FromLevels(reading.Game, reading.Chat);
            PushBalance(balance, reading.Game, reading.Chat);
        }

        ChatMixDetail = reading.Detail;
    }

    private void SetMasterPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        _masterSync++;
        try { MasterVolume = percent; }
        finally { _masterSync--; }
        _engine.SetMasterVolume(percent / 100f);
    }

    private void PushBalance(int balance, int game, int chat)
    {
        _balanceSync++;
        try
        {
            ChatMixBalance = balance;
        }
        finally
        {
            _balanceSync--;
        }

        _chatMixGame = game;
        _chatMixChat = chat;
        Raise(nameof(ChatMixReadout));
        Raise(nameof(ChatMixBalanceLabel));
        if (_config.ChatMixBalance != balance)
            _config.ChatMixBalance = balance;
        _engine.ApplyChatMix(game, chat);
    }

    private void ApplyKnob(int position)
    {
        _lastKnobPosition = position;
        var percent = _engine.ApplyKnobPosition(position);
        SetMasterPercent(percent);
        ChatMixDetail = $"Volume knob {position}/{Math.Max(1, _config.VolumeKnobMax)} → master {percent}.";
    }

    private string LastReport
    {
        get => _lastReport;
        set
        {
            if (!Set(ref _lastReport, value)) return;
            Raise(nameof(LastReportText));
        }
    }

    public void SelectOutput(string id)
    {
        if (IsSyncing || string.IsNullOrEmpty(id)) return;
        if (string.Equals(_selectedOutputId, id, StringComparison.OrdinalIgnoreCase)) return;
        _engine.SetMasterOutput(id);
    }

    public void SelectInput(string id)
    {
        if (IsSyncing || string.IsNullOrEmpty(id)) return;
        if (string.Equals(_selectedInputId, id, StringComparison.OrdinalIgnoreCase)) return;
        _engine.SetMasterInput(id);
    }

    public void MoveApp(string exeKey, ChannelId channel) => _engine.Assign(exeKey, channel);

    private void Apply(MixerSnapshot snap)
    {
        _syncing++;
        foreach (var channel in Channels)
            channel.Syncing = true;
        try
        {
            RebuildOutputs(snap.Outputs, snap.Channels.Select(channel => channel.DeviceId), snap.VirtualMixActive);
            RebuildInputs(snap.Inputs);
            SelectedOutputId = snap.MasterOutputId;
            SelectedInputId = snap.MasterInputId;
            MasterVolume = ChatMixMath.Percent(snap.MasterVolume);
            MicMuted = snap.MicMuted;
            Status = snap.Status;
            RoutingAvailable = snap.RoutingAvailable;
            _sonarWarning = snap.SonarWarning;
            _virtualMixActive = snap.VirtualMixActive;
            Raise(nameof(SetupWarning));
            Raise(nameof(ShowSonarWarning));
            Raise(nameof(VirtualMixActive));
            Raise(nameof(NoOutputs));

            Replace(VacCables, snap.Cables.Select(cable => new DeviceOption { Id = cable.Id, Name = cable.Name }).ToList());
            SetCableFromSnap(ChannelId.Game, snap, ref _gameCableId, nameof(GameCableId));
            SetCableFromSnap(ChannelId.Chat, snap, ref _chatCableId, nameof(ChatCableId));
            SetCableFromSnap(ChannelId.Media, snap, ref _mediaCableId, nameof(MediaCableId));
            SetCableFromSnap(ChannelId.Aux, snap, ref _auxCableId, nameof(AuxCableId));

            foreach (var channelSnap in snap.Channels)
            {
                var channel = Channels.First(item => item.Id == channelSnap.Id);
                channel.Apply(channelSnap);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in snap.Apps)
            {
                seen.Add(app.ExeKey);
                if (!_chips.TryGetValue(app.ExeKey, out var chip))
                {
                    chip = new AppChipViewModel(_engine, app.ExeKey, app.Channel);
                    _chips[app.ExeKey] = chip;
                }
                chip.Syncing = true;
                chip.Apply(app);
                chip.Syncing = false;
                Place(chip, app.Channel);
            }

            foreach (var key in _chips.Keys.Where(key => !seen.Contains(key)).ToList())
            {
                var chip = _chips[key];
                _chips.Remove(key);
                foreach (var channel in Channels)
                    channel.Apps.Remove(chip);
            }
        }
        finally
        {
            foreach (var channel in Channels)
                channel.Syncing = false;
            _syncing--;
        }
    }

    private void Place(AppChipViewModel chip, ChannelId channelId)
    {
        var destination = Channels.First(channel => channel.Id == channelId);
        foreach (var channel in Channels)
        {
            if (!ReferenceEquals(channel, destination) && channel.Apps.Contains(chip))
                channel.Apps.Remove(chip);
        }
        if (!destination.Apps.Contains(chip))
            destination.Apps.Add(chip);
    }

    public void ApplyPeaks(PeakUpdate peaks)
    {
        if (Math.Abs(_micPeak - peaks.MicPeak) >= 0.02)
            MicPeak = peaks.MicPeak;
        foreach (var channel in Channels)
        {
            var index = (int)channel.Id;
            var next = index < peaks.ChannelPeaks.Length ? peaks.ChannelPeaks[index] : 0;
            if (Math.Abs(channel.Peak - next) >= 0.02)
                channel.Peak = next;
            foreach (var app in channel.Apps)
            {
                var appPeak = peaks.AppPeaks.TryGetValue(app.ExeKey, out var peak) ? peak : 0;
                if (Math.Abs(app.Peak - appPeak) >= 0.02)
                    app.Peak = appPeak;
            }
        }
    }

    private void RebuildOutputs(IReadOnlyList<DeviceSnapshot> devices, IEnumerable<string?> selectedIds, bool virtualMix)
    {
        var items = new List<DeviceOption> { new() { Id = "", Name = virtualMix ? "Master mix" : "Default" } };
        foreach (var device in devices)
            items.Add(new DeviceOption { Id = device.Id, Name = device.Name });
        foreach (var id in selectedIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (items.All(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
                items.Add(new DeviceOption { Id = id!, Name = "Disconnected device" });
        }
        Replace(OutputChoices, items);
    }

    private void RebuildInputs(IReadOnlyList<DeviceSnapshot> devices)
    {
        var items = devices.Select(device => new DeviceOption { Id = device.Id, Name = device.Name }).ToList();
        Replace(InputDevices, items);
    }

    private static void Replace(ObservableCollection<DeviceOption> target, List<DeviceOption> items)
    {
        if (target.Count == items.Count &&
            target.Zip(items).All(pair =>
                string.Equals(pair.First.Id, pair.Second.Id, StringComparison.OrdinalIgnoreCase) &&
                pair.First.Name == pair.Second.Name))
            return;
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private void SetCable(ChannelId channel, ref string field, string? value)
    {
        value ??= "";
        if (string.Equals(field, value, StringComparison.OrdinalIgnoreCase)) return;
        field = value;
        Raise(channel switch
        {
            ChannelId.Game => nameof(GameCableId),
            ChannelId.Chat => nameof(ChatCableId),
            ChannelId.Media => nameof(MediaCableId),
            _ => nameof(AuxCableId)
        });
        if (!IsSyncing && !string.IsNullOrEmpty(value))
            _engine.SetCable(channel, value);
    }

    private void SetCableFromSnap(ChannelId channel, MixerSnapshot snap, ref string field, string propertyName)
    {
        var id = "";
        var key = channel.Key();
        var assigned = snap.Channels.FirstOrDefault(item => item.Id == channel);
        if (assigned != null && _config.CableAssignments.TryGetValue(key, out var saved))
            id = saved;
        if (string.Equals(field, id, StringComparison.OrdinalIgnoreCase)) return;
        field = id;
        Raise(propertyName);
    }

    private void OpenVacDownload()
    {
        Process.Start(new ProcessStartInfo("https://vac.muzychenko.net/en/download.htm") { UseShellExecute = true });
    }

    private void RenameCables()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;
        Process.Start(new ProcessStartInfo(exe, "--rename-cables")
        {
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    private void OpenConfigFolder()
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.DirectoryPath);
            Process.Start(new ProcessStartInfo("explorer.exe", ConfigStore.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Open config folder failed", ex);
            Status = "Couldn't open the config folder.";
        }
    }
}
