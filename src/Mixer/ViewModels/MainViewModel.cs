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
    private readonly BaseStationDisplay _display;
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
    private bool _customDisplay;
    private int _mixLatencyMs = 20;
    private bool _gameStraightThrough;
    private bool _limiterEnabled;
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

    public MainViewModel(AudioEngine engine, MixerConfig config, ChatMixMonitor chatMix, BaseStationDisplay display)
    {
        _engine = engine;
        _config = config;
        _chatMix = chatMix;
        _display = display;
        _startWithWindows = config.StartWithWindows;
        _startMinimized = config.StartMinimized;
        _closeToTray = config.CloseToTray;
        _chatMixEnabled = config.ChatMixEnabled;
        _chatMixBalance = Math.Clamp(config.ChatMixBalance, 0, 100);
        _masterVolume = ChatMixMath.Percent(config.MasterVolume);
        _invertVolumeKnob = config.InvertVolumeKnob;
        _customDisplay = config.CustomDisplay;
        _mixLatencyMs = config.MixLatencyMs is 10 or 40 ? config.MixLatencyMs : 20;
        _gameStraightThrough = config.GameStraightThrough;
        _limiterEnabled = config.LimiterEnabled;
        // The readout is the dial, not the channel faders. Until the base station reports, show the saved balance.
        (_chatMixGame, _chatMixChat) = ChatMixMath.ToLevels(config.ChatMixBalance);
        Channels = new ObservableCollection<ChannelViewModel>(
            Shown.Select(id => new ChannelViewModel(id, engine)));
        ChannelChoices = new ObservableCollection<ChannelChoice>(
            Shown.Select(id => new ChannelChoice { Id = id, Name = id.Title() }));
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
        RefreshScreenMirror();
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

    public string MicMuteLabel => _micMuted ? "MIC MUTED" : "MIC LIVE";
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
            var enabled = value;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Autostart.Set(enabled);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        Status = enabled ? "Mixer will start with Windows." : "Mixer will not start with Windows.");
                }
                catch (Exception ex)
                {
                    Log.Error("Startup registration failed", ex);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        Status = "Couldn't update the Windows startup entry.");
                }
            });
            ConfigStore.SaveSoon(_config);
        }
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (!Set(ref _closeToTray, value)) return;
            _config.CloseToTray = value;
            ConfigStore.SaveSoon(_config);
        }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (!Set(ref _startMinimized, value)) return;
            _config.StartMinimized = value;
            ConfigStore.SaveSoon(_config);
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
            ConfigStore.SaveSoon(_config);
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
                ConfigStore.SaveSoon(_config);
        }
    }

    public bool CustomDisplay
    {
        get => _customDisplay;
        set
        {
            if (!Set(ref _customDisplay, value)) return;
            _config.CustomDisplay = value;
            _display.Enabled = value;
            ConfigStore.SaveSoon(_config);
        }
    }

    public bool ShowSongOnDisplay
    {
        get => _display.ShowSong;
        set
        {
            if (_display.ShowSong == value) return;
            _config.ShowSongOnDisplay = value;
            _display.ShowSong = value;
            Raise(nameof(ShowSongOnDisplay));
            ConfigStore.SaveSoon(_config);
        }
    }

    public int[] LatencyChoices { get; } = [10, 20, 40];

    public int MixLatencyMs
    {
        get => _mixLatencyMs;
        set
        {
            var ms = value is 10 or 40 ? value : 20;
            if (_mixLatencyMs == ms) return;
            _mixLatencyMs = ms;
            Raise(nameof(MixLatencyMs));
            _config.MixLatencyMs = ms;
            _engine.SetMixLatency(ms);
            ConfigStore.SaveSoon(_config);
        }
    }

    public bool LimiterEnabled
    {
        get => _limiterEnabled;
        set
        {
            if (!Set(ref _limiterEnabled, value)) return;
            _config.LimiterEnabled = value;
            _engine.SetLimiterEnabled(value);
            ConfigStore.SaveSoon(_config);
        }
    }

    public bool GameStraightThrough
    {
        get => _gameStraightThrough;
        set
        {
            if (!Set(ref _gameStraightThrough, value)) return;
            _config.GameStraightThrough = value;
            _engine.SetGameStraightThrough(value);
            ConfigStore.SaveSoon(_config);
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
            Raise(nameof(KnobLabel));
            if (IsSyncing || _masterSync > 0) return;
            _engine.SetMasterVolume(percent / 100f);
        }
    }

    public string ChatMixGameLabel => ChatMixMath.ToLevels((int)Math.Round(_chatMixBalance)).Game.ToString();
    public string ChatMixChatLabel => ChatMixMath.ToLevels((int)Math.Round(_chatMixBalance)).Chat.ToString();

    /// <summary>Where the base station knob sits for the current master volume.</summary>
    public string KnobLabel
    {
        get
        {
            var max = Math.Max(1, _config.VolumeKnobMax);
            var loud = InvertVolumeKnob ? 1 - _masterVolume / 100.0 : _masterVolume / 100.0;
            return $"KNOB {(int)Math.Round((1 - loud) * max)} / {max}";
        }
    }

    private double _outLeft;
    private double _outRight;
    private double _limiterReduction;
    private double _outHold;
    private long _outHoldAt;
    private string _outPeakLabel = "-∞";
    private string _limiterLabel = "LIMITER";
    private bool _limiterWorking;

    public double OutLeft { get => _outLeft; private set => Set(ref _outLeft, value); }
    public double OutRight { get => _outRight; private set => Set(ref _outRight, value); }
    /// <summary>Limiter gain reduction as a share of 12 dB, for the reduction meter.</summary>
    public double LimiterReduction { get => _limiterReduction; private set => Set(ref _limiterReduction, value); }
    /// <summary>Highest output peak over the last second, in dBFS.</summary>
    public string OutPeakLabel { get => _outPeakLabel; private set => Set(ref _outPeakLabel, value); }
    public string LimiterLabel { get => _limiterLabel; private set => Set(ref _limiterLabel, value); }
    public bool LimiterWorking { get => _limiterWorking; private set => Set(ref _limiterWorking, value); }

    public string OutputName
    {
        get
        {
            var id = _selectedOutputId;
            var match = OutputChoices.FirstOrDefault(option => option.Id == id && option.Id.Length > 0);
            return (match?.Name ?? "No output").ToUpperInvariant();
        }
    }

    private void ApplyOutputMeter(PeakUpdate peaks)
    {
        if (Moved(_outLeft, peaks.OutLeft)) OutLeft = peaks.OutLeft;
        if (Moved(_outRight, peaks.OutRight)) OutRight = peaks.OutRight;
        var share = Math.Clamp(peaks.LimiterReductionDb / 12.0, 0, 1);
        if (Math.Abs(_limiterReduction - share) >= 0.004) LimiterReduction = share;
        LimiterWorking = peaks.LimiterReductionDb > 0.05f;
        LimiterLabel = !LimiterEnabled ? "LIMITER OFF"
            : peaks.LimiterReductionDb > 0.05f ? $"LIMITER −{peaks.LimiterReductionDb:0.0} dB"
            : "LIMITER IDLE";

        var now = Environment.TickCount64;
        var peak = Math.Max(peaks.OutLeft, peaks.OutRight);
        if (peak >= _outHold || now - _outHoldAt > 1000)
        {
            _outHold = peak;
            _outHoldAt = now;
            OutPeakLabel = peak <= 0.001 ? "-∞" : (20 * Math.Log10(peak)).ToString("+0.0;-0.0;0.0");
        }
    }

    private bool _screenIsSong;
    private string _screenTitle = "";
    private string _screenArtist = "";
    private double _screenProgress;
    private string _screenElapsed = "";
    private string _screenTotal = "";
    private bool _screenHasTimeline;
    private long _screenReadAt;

    /// <summary>True while the base station screen shows the playing song, false while it shows the mix.</summary>
    public bool ScreenIsSong { get => _screenIsSong; private set => Set(ref _screenIsSong, value); }
    public string ScreenTitle { get => _screenTitle; private set => Set(ref _screenTitle, value); }
    public string ScreenArtist { get => _screenArtist; private set => Set(ref _screenArtist, value); }
    public double ScreenProgress { get => _screenProgress; private set => Set(ref _screenProgress, value); }
    public string ScreenElapsed { get => _screenElapsed; private set => Set(ref _screenElapsed, value); }
    public string ScreenTotal { get => _screenTotal; private set => Set(ref _screenTotal, value); }
    public bool ScreenHasTimeline { get => _screenHasTimeline; private set => Set(ref _screenHasTimeline, value); }

    /// <summary>Copies what the base station screen shows into the mirror in the window, a few times a second.</summary>
    private void RefreshScreenMirror()
    {
        var now = Environment.TickCount64;
        if (now - _screenReadAt < 250) return;
        _screenReadAt = now;
        var song = _display.Enabled ? _display.SongOnScreen : null;
        ScreenIsSong = song != null;
        if (song == null) return;
        ScreenTitle = song.Title;
        ScreenArtist = song.Artist;
        ScreenHasTimeline = song.HasTimeline;
        if (!song.HasTimeline) return;
        var position = song.PositionAt(DateTimeOffset.UtcNow);
        ScreenProgress = position.Ticks / (double)song.Duration.Ticks;
        ScreenElapsed = Clock(position);
        ScreenTotal = Clock(song.Duration);
    }

    private static string Clock(TimeSpan time)
    {
        var seconds = Math.Max(0, (int)time.TotalSeconds);
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
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
            Raise(nameof(KnobLabel));
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
            Raise(nameof(ChatMixGameLabel));
            Raise(nameof(ChatMixChatLabel));
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
            Raise(nameof(ChatMixGameLabel));
            Raise(nameof(ChatMixChatLabel));
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

    /// <summary>
    /// Paints the latest dial reading. Audio gains are already applied on the HID thread.
    /// </summary>
    public void ShowDial(ChatMixReading reading)
    {
        ChatMixConnected = reading.Connected;
        if (!string.IsNullOrEmpty(reading.RawReport))
            LastReport = reading.RawReport;

        if (reading.Kind == ChatMixReportKind.Knob)
        {
            _lastKnobPosition = reading.Knob;
            var max = Math.Max(1, _config.VolumeKnobMax);
            var percent = ChatMixMath.VolumeFromKnob(reading.Knob, max, InvertVolumeKnob);
            _masterSync++;
            try { MasterVolume = percent; }
            finally { _masterSync--; }
            ChatMixDetail = $"Volume knob {reading.Knob}/{max} → master {percent}.";
            return;
        }

        if (reading.Kind == ChatMixReportKind.Levels && reading.HasLevels && reading.Game >= 0 && ChatMixEnabled)
        {
            var balance = ChatMixMath.FromLevels(reading.Game, reading.Chat);
            _balanceSync++;
            try { ChatMixBalance = balance; }
            finally { _balanceSync--; }
            _chatMixGame = reading.Game;
            _chatMixChat = reading.Chat;
            Raise(nameof(ChatMixReadout));
            if (_config.ChatMixBalance != balance)
                _config.ChatMixBalance = balance;
        }

        if (!string.IsNullOrEmpty(reading.Detail))
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
            var outputsRebuilt = RebuildOutputs(snap.Outputs, snap.Channels.Select(channel => channel.DeviceId), snap.VirtualMixActive);
            var inputsRebuilt = RebuildInputs(snap.Inputs);
            SelectedOutputId = snap.MasterOutputId;
            Raise(nameof(OutputName));
            SelectedInputId = snap.MasterInputId;
            // Refilling a list clears the picker's selection. The id has not changed, so say it again to reselect it.
            if (outputsRebuilt) Raise(nameof(SelectedOutputId));
            if (inputsRebuilt) Raise(nameof(SelectedInputId));
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

            var cablesRebuilt = Replace(VacCables, snap.Cables.Select(cable => new DeviceOption { Id = cable.Id, Name = cable.Name }).ToList());
            SetCableFromSnap(ChannelId.Game, snap, ref _gameCableId, nameof(GameCableId));
            SetCableFromSnap(ChannelId.Chat, snap, ref _chatCableId, nameof(ChatCableId));
            SetCableFromSnap(ChannelId.Media, snap, ref _mediaCableId, nameof(MediaCableId));
            SetCableFromSnap(ChannelId.Aux, snap, ref _auxCableId, nameof(AuxCableId));

            foreach (var channelSnap in snap.Channels)
            {
                var channel = Channels.FirstOrDefault(item => item.Id == channelSnap.Id);
                if (channel == null) continue;
                channel.Apply(channelSnap);
                if (outputsRebuilt) channel.ReselectDevice();
            }

            if (cablesRebuilt)
            {
                Raise(nameof(GameCableId));
                Raise(nameof(ChatCableId));
                Raise(nameof(MediaCableId));
                Raise(nameof(AuxCableId));
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
        var destination = Channels.FirstOrDefault(channel => channel.Id == channelId) ?? Channels[0];
        foreach (var channel in Channels)
        {
            if (!ReferenceEquals(channel, destination) && channel.Apps.Contains(chip))
                channel.Apps.Remove(chip);
        }
        if (!destination.Apps.Contains(chip))
            destination.Apps.Add(chip);
    }

    /// <summary>The strips in the window. Apps nobody has placed land in Game, so there is no strip for them.</summary>
    private static readonly ChannelId[] Shown = [ChannelId.Game, ChannelId.Chat, ChannelId.Media, ChannelId.Aux];

    /// <summary>True when a meter value changed enough to redraw. The meters are in dB, so small levels count too.</summary>
    private static bool Moved(double shown, double next) =>
        Math.Abs(shown - next) > Math.Max(0.0002, 0.03 * Math.Max(shown, next));

    public void ApplyPeaks(PeakUpdate peaks)
    {
        ApplyOutputMeter(peaks);
        if (Moved(_micPeak, peaks.MicPeak))
            MicPeak = peaks.MicPeak;
        foreach (var channel in Channels)
        {
            var index = (int)channel.Id;
            var next = index < peaks.ChannelPeaks.Length ? peaks.ChannelPeaks[index] : 0;
            if (Moved(channel.Peak, next))
                channel.Peak = next;
            foreach (var app in channel.Apps)
            {
                var appPeak = peaks.AppPeaks.TryGetValue(app.ExeKey, out var peak) ? peak : 0;
                if (Moved(app.Peak, appPeak))
                    app.Peak = appPeak;
            }
        }
    }

    private bool RebuildOutputs(IReadOnlyList<DeviceSnapshot> devices, IEnumerable<string?> selectedIds, bool virtualMix)
    {
        var items = new List<DeviceOption> { new() { Id = "", Name = virtualMix ? "Master mix" : "Default" } };
        foreach (var device in devices)
            items.Add(new DeviceOption { Id = device.Id, Name = device.Name });
        foreach (var id in selectedIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (items.All(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
                items.Add(new DeviceOption { Id = id!, Name = "Disconnected device" });
        }
        return Replace(OutputChoices, items);
    }

    private bool RebuildInputs(IReadOnlyList<DeviceSnapshot> devices)
    {
        var items = devices.Select(device => new DeviceOption { Id = device.Id, Name = device.Name }).ToList();
        return Replace(InputDevices, items);
    }

    /// <summary>Returns true when the list was refilled.</summary>
    private static bool Replace(ObservableCollection<DeviceOption> target, List<DeviceOption> items)
    {
        if (target.Count == items.Count &&
            target.Zip(items).All(pair =>
                string.Equals(pair.First.Id, pair.Second.Id, StringComparison.OrdinalIgnoreCase) &&
                pair.First.Name == pair.Second.Name))
            return false;
        target.Clear();
        foreach (var item in items)
            target.Add(item);
        return true;
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
        VacRenamer.LaunchElevated();
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
