using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Mixer.Audio;
using Mixer.Config;
using Mixer.Services;
using Mixer.ViewModels;

namespace Mixer;

public partial class App : System.Windows.Application
{
    public static bool IsExiting { get; private set; }

    private SingleInstance? _instance;
    private AudioEngine? _engine;
    private ChatMixMonitor? _chatMix;
    private TrayService? _tray;
    private MainWindow? _window;
    private MixerConfig? _config;
    private MainViewModel? _viewModel;
    private readonly object _readingGate = new();
    private DispatcherTimer? _dialUi;
    private ChatMixReading _latestReading;
    private int _dialQueued;
    private bool _dialDirty;
    private long _lastDialLog;
    private int _audioKnob = int.MinValue;
    private int _audioGame = int.MinValue;
    private int _audioChat = int.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Error("Unhandled", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task", args.Exception);
            args.SetObserved();
        };
        SessionEnding += (_, _) =>
        {
            IsExiting = true;
            TrySave();
        };

        if (e.Args.Any(arg => string.Equals(arg, "--rename-cables", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var count = VacRenamer.RenameAssigned();
                MessageBox.Show($"Renamed {count} Virtual Audio Cable endpoints.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Error("Rename failed", ex);
                MessageBox.Show("Couldn't rename the cables.\n\n" + ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        if (!SingleInstance.TryAcquire(ShowExisting, out _instance))
        {
            Shutdown();
            return;
        }

        try
        {
            _config = ConfigStore.Load();
            _engine = new AudioEngine(_config);
            _chatMix = new ChatMixMonitor { Enabled = _config.ChatMixEnabled };
            _viewModel = new MainViewModel(_engine, _config, _chatMix);
            _chatMix.Reading += OnChatMixReading;
            _window = new MainWindow(_viewModel);
            _tray = new TrayService(_window, _viewModel, ExitApplication);
            _chatMix.Start();
            _engine.Start();
            try { Autostart.Set(_config.StartWithWindows); }
            catch (Exception ex) { Log.Error("Startup registration failed", ex); }
            var tray = e.Args.Any(arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase));
            if (tray || _config.StartMinimized)
            {
                _window.ShowInTaskbar = false;
                _tray.NotifyHidden();
            }
            else
            {
                _window.Show();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            MessageBox.Show("Mixer couldn't start.\n\n" + ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApplication();
        }
    }

    public void ExitApplication()
    {
        if (IsExiting && _window == null) return;
        IsExiting = true;
        TrySave();
        try { _chatMix?.Dispose(); } catch { /* already stopped */ }
        try { _engine?.Dispose(); } catch { /* already stopped */ }
        try { _tray?.Dispose(); } catch { /* already stopped */ }
        try { _instance?.Dispose(); } catch { /* already stopped */ }
        Shutdown();
    }

    private void OnChatMixReading(object? sender, ChatMixReading reading)
    {
        var started = Stopwatch.GetTimestamp();
        if (ApplyAudio(reading))
            LogDial(reading, started);

        lock (_readingGate)
        {
            _latestReading = reading;
            _dialDirty = true;
        }

        if (Interlocked.CompareExchange(ref _dialQueued, 1, 0) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Render, StartDialUi);
    }

    private bool ApplyAudio(ChatMixReading reading)
    {
        if (_engine == null) return false;
        if (reading.Kind == ChatMixReportKind.Levels && reading.HasLevels && reading.Game >= 0)
        {
            if (reading.Game == _audioGame && reading.Chat == _audioChat) return false;
            _audioGame = reading.Game;
            _audioChat = reading.Chat;
            _engine.ApplyChatMix(reading.Game, reading.Chat);
            return true;
        }

        if (reading.Kind != ChatMixReportKind.Knob || reading.Knob == _audioKnob) return false;
        _audioKnob = reading.Knob;
        _engine.ApplyKnobPosition(reading.Knob);
        return true;
    }

    private void LogDial(ChatMixReading reading, long started)
    {
        var now = Environment.TickCount64;
        if (now - _lastDialLog < 1000) return;
        _lastDialLog = now;
        var microseconds = (Stopwatch.GetTimestamp() - started) * 1_000_000L / Stopwatch.Frequency;
        Log.Info($"Dial {reading.Kind} audio {microseconds}us");
    }

    private void StartDialUi()
    {
        _dialUi ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => PaintDial(), Dispatcher);
        PaintDial();
    }

    private void PaintDial()
    {
        ChatMixReading reading;
        bool dirty;
        lock (_readingGate)
        {
            reading = _latestReading;
            dirty = _dialDirty;
            _dialDirty = false;
        }

        if (dirty)
            _viewModel?.ShowDial(reading);

        lock (_readingGate)
            dirty = _dialDirty;
        if (dirty)
        {
            if (_dialUi is { IsEnabled: false })
                _dialUi.Start();
            return;
        }

        _dialUi?.Stop();
        Interlocked.Exchange(ref _dialQueued, 0);
        lock (_readingGate)
            dirty = _dialDirty;
        if (dirty && Interlocked.CompareExchange(ref _dialQueued, 1, 0) == 0 && _dialUi is { IsEnabled: false })
            _dialUi.Start();
    }

    private void ShowExisting() => Dispatcher.BeginInvoke(() => _window?.ShowFromTray());

    private void TrySave()
    {
        try
        {
            if (_config != null) ConfigStore.Save(_config);
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI", e.Exception);
        MessageBox.Show(
            "Mixer hit an unexpected error and kept running.\n\n" + e.Exception.Message,
            AppInfo.Name,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
