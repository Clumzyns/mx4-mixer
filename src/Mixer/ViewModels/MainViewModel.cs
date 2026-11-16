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
    private bool _settingsOpen;
    private bool _chatMixEnabled;
    private bool _chatMixConnected;
    private int _chatMixGame = -1;
    private int _chatMixChat = -1;
    private string _chatMixDetail = "Looking for the Nova Pro base station…";
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
        _chatMixEnabled = config.ChatMixEnabled;
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
        _engine.PeaksChanged += (_, peaks) => ApplyPeaks(peaks);
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
                ChatMixDetail = "ChatMix is off. Game and Chat faders are manual.";
            try { ConfigStore.Save(_config); } catch (Exception ex) { Log.Error("Save failed", ex); }
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
        if (reading.HasLevels && reading.Game >= 0)
        {
            _chatMixGame = reading.Game;
            _chatMixChat = reading.Chat;
            Raise(nameof(ChatMixReadout));
            if (ChatMixEnabled)
                _engine.ApplyChatMix(reading.Game, reading.Chat);
        }
        ChatMixDetail = reading.Detail;
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

    private void ApplyPeaks(PeakUpdate peaks)
    {
        MicPeak = peaks.MicPeak;
        foreach (var channel in Channels)
        {
            var index = (int)channel.Id;
            channel.Peak = index < peaks.ChannelPeaks.Length ? peaks.ChannelPeaks[index] : 0;
            foreach (var app in channel.Apps)
                app.Peak = peaks.AppPeaks.TryGetValue(app.ExeKey, out var peak) ? peak : 0;
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
