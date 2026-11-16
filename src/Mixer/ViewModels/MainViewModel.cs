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

    public MainViewModel(AudioEngine engine, MixerConfig config)
    {
        _engine = engine;
        _config = config;
        _startWithWindows = config.StartWithWindows;
        _startMinimized = config.StartMinimized;
        Channels = new ObservableCollection<ChannelViewModel>(
            ChannelIds.All.Select(id => new ChannelViewModel(id, engine)));
        ChannelChoices = new ObservableCollection<ChannelChoice>(
            ChannelIds.All.Select(id => new ChannelChoice { Id = id, Name = id.Title() }));
        OutputChoices.Add(new DeviceOption { Id = "", Name = "Default" });
        ToggleMicCommand = new RelayCommand(() => MicMuted = !MicMuted);
        ToggleSettingsCommand = new RelayCommand(() => SettingsOpen = !SettingsOpen);
        OpenConfigCommand = new RelayCommand(OpenConfigFolder);
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
            RebuildOutputs(snap.Outputs, snap.Channels.Select(channel => channel.DeviceId));
            RebuildInputs(snap.Inputs);
            SelectedOutputId = snap.MasterOutputId;
            SelectedInputId = snap.MasterInputId;
            MicMuted = snap.MicMuted;
            Status = snap.Status;
            RoutingAvailable = snap.RoutingAvailable;
            Raise(nameof(NoOutputs));

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

    private void RebuildOutputs(IReadOnlyList<DeviceSnapshot> devices, IEnumerable<string?> selectedIds)
    {
        var items = new List<DeviceOption> { new() { Id = "", Name = "Default" } };
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
