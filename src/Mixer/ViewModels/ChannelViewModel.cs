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
            Raise(nameof(DbLabel));
            if (!Syncing)
                _engine.SetChannelVolume(Id, (float)(value / 100.0));
        }
    }

    public string VolumeLabel => $"{Math.Round(_volume):0}";

    public string Number => ((int)Id + 1).ToString("00");
    public string UpperName => Name.ToUpperInvariant();
    /// <summary>The cable this channel records, or what the channel is for when it has none.</summary>
    public string RouteLabel => (string.IsNullOrEmpty(_cableLabel) ? Subtitle : _cableLabel).ToUpperInvariant();

    /// <summary>Fader gain in dB. The fader covers 60 dB and its bottom stop is silence.</summary>
    public string DbLabel
    {
        get
        {
            if (_volume <= 0.05) return "-∞";
            var db = (_volume / 100.0 - 1.0) * 60.0;
            return db > -0.05 ? "0.0" : "-" + Math.Abs(db).ToString("0.0");
        }
    }

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

    public string MuteLabel => _muted ? "MUTED" : "MUTE";

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

    /// <summary>Tells the device picker its selection again after its list was refilled.</summary>
    public void ReselectDevice() => Raise(nameof(DeviceId));

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
                Raise(nameof(RouteLabel));
            }
        }
        finally
        {
            Syncing = false;
        }
    }
}
