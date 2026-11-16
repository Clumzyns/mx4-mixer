using System.Collections.ObjectModel;
using System.Windows.Media;
using Mixer.Audio;

namespace Mixer.ViewModels;

public sealed class ChannelViewModel : ObservableObject
{
    private readonly AudioEngine _engine;
    private double _volume = 100;
    private bool _muted;
    private string _deviceId = "";
    private double _peak;

    public ChannelViewModel(ChannelId id, AudioEngine engine)
    {
        Id = id;
        _engine = engine;
        Name = id.Title();
        Subtitle = id.Subtitle();
        var color = (Color)ColorConverter.ConvertFromString(id.Accent());
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Accent = brush;
        Apps.CollectionChanged += (_, _) =>
        {
            Raise(nameof(HasApps));
            Raise(nameof(AppCountLabel));
        };
        ToggleMuteCommand = new RelayCommand(() => Muted = !Muted);
    }

    public ChannelId Id { get; }
    public string Name { get; }
    public string Subtitle { get; }
    private string _cableLabel = "";
    public string DisplayTitle => string.IsNullOrEmpty(_cableLabel) ? Name : $"{Name} ({_cableLabel})";
    public Brush Accent { get; }
    public ObservableCollection<AppChipViewModel> Apps { get; } = new();
    public RelayCommand ToggleMuteCommand { get; }
    public bool Syncing { get; set; }
    public bool HasApps => Apps.Count > 0;
    public string AppCountLabel => Apps.Count == 1 ? "1 app" : $"{Apps.Count} apps";

    public double Volume
    {
        get => _volume;
        set
        {
            if (Math.Abs(_volume - value) < 0.05) return;
            _volume = value;
            Raise();
            Raise(nameof(VolumeLabel));
            if (!Syncing)
                _engine.SetChannelVolume(Id, (float)(value / 100.0));
        }
    }

    public string VolumeLabel => $"{Math.Round(_volume):0}";

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value)) return;
            Raise(nameof(MuteLabel));
            if (!Syncing)
                _engine.SetChannelMuted(Id, value);
        }
    }

    public string MuteLabel => _muted ? "Muted" : "Mute";

    public string DeviceId
    {
        get => _deviceId;
        set => Set(ref _deviceId, value ?? "");
    }

    public double Peak
    {
        get => _peak;
        set => Set(ref _peak, value);
    }

    public void SetDeviceFromUi(string id)
    {
        if (string.Equals(_deviceId, id, StringComparison.OrdinalIgnoreCase)) return;
        DeviceId = id;
        if (!Syncing)
            _engine.SetChannelDevice(Id, string.IsNullOrEmpty(id) ? null : id);
    }

    public void Apply(ChannelSnapshot snap)
    {
        Syncing = true;
        try
        {
            Volume = snap.Volume * 100.0;
            Muted = snap.Muted;
            DeviceId = snap.DeviceId ?? "";
            if (_cableLabel != snap.CableLabel)
            {
                _cableLabel = snap.CableLabel;
                Raise(nameof(DisplayTitle));
            }
        }
        finally
        {
            Syncing = false;
        }
    }
}
