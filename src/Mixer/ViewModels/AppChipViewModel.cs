using System.Windows.Media;
using Mixer.Audio;
using Mixer.Services;

namespace Mixer.ViewModels;

public sealed class AppChipViewModel : ObservableObject
{
    public const string DragFormat = "Mixer.AppExe";
    private readonly AudioEngine _engine;
    private string _displayName;
    private string? _exePath;
    private ImageSource? _icon;
    private int _sessionCount;
    private uint _processId;
    private double _baseVolume = 100;
    private bool _muted;
    private ChannelId _channel;
    private double _peak;

    public AppChipViewModel(AudioEngine engine, string exeKey, ChannelId channel)
    {
        _engine = engine;
        ExeKey = exeKey;
        _displayName = exeKey;
        _channel = channel;
        ToggleMuteCommand = new RelayCommand(() => Muted = !Muted);
    }

    public string ExeKey { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public bool Syncing { get; set; }

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (!Set(ref _displayName, value)) return;
            Raise(nameof(Initial));
        }
    }

    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "?" : char.ToUpperInvariant(DisplayName.Trim()[0]).ToString();

    public string Subtitle => _sessionCount > 1 ? $"{ExeKey}  ·  {_sessionCount} sessions" : ExeKey;

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (!Set(ref _icon, value)) return;
            Raise(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon != null;

    public double BaseVolume
    {
        get => _baseVolume;
        set
        {
            if (Math.Abs(_baseVolume - value) < 0.05) return;
            _baseVolume = value;
            Raise();
            Raise(nameof(BaseLabel));
            if (!Syncing)
                _engine.SetAppBaseVolume(ExeKey, (float)(value / 100.0));
        }
    }

    public string BaseLabel => $"{Math.Round(_baseVolume):0}";

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value)) return;
            Raise(nameof(MuteLabel));
            if (!Syncing)
                _engine.SetAppMuted(ExeKey, value);
        }
    }

    public string MuteLabel => _muted ? "MUTED" : "MUTE";

    public ChannelId Channel
    {
        get => _channel;
        set => Set(ref _channel, value);
    }

    public double Peak
    {
        get => _peak;
        set => Set(ref _peak, value);
    }

    public uint ProcessId => _processId;

    public void Apply(AppSnapshot snap)
    {
        Syncing = true;
        try
        {
            DisplayName = snap.DisplayName;
            if (_sessionCount != snap.SessionCount)
            {
                _sessionCount = snap.SessionCount;
                Raise(nameof(Subtitle));
            }
            _processId = snap.ProcessId;
            if (!string.Equals(_exePath, snap.ExePath, StringComparison.OrdinalIgnoreCase))
            {
                _exePath = snap.ExePath;
                Icon = IconLoader.Get(snap.ExePath);
            }
            BaseVolume = snap.BaseVolume * 100.0;
            Muted = snap.Muted;
            Channel = snap.Channel;
        }
        finally
        {
            Syncing = false;
        }
    }
}
