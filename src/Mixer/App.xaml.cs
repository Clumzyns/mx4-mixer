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
    private ChatMixReading _latestReading;
    private int _readingQueued;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
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
        if (_engine != null)
        {
            if (reading.Kind == ChatMixReportKind.Levels && reading.HasLevels && reading.Game >= 0)
                _engine.ApplyChatMix(reading.Game, reading.Chat);
            else if (reading.Kind == ChatMixReportKind.Knob)
                _engine.ApplyKnobPosition(reading.Knob);
        }

        lock (_readingGate) _latestReading = reading;
        if (Interlocked.CompareExchange(ref _readingQueued, 1, 0) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Render, DrainChatMix);
    }

    private void DrainChatMix()
    {
        ChatMixReading reading;
        lock (_readingGate) reading = _latestReading;
        Interlocked.Exchange(ref _readingQueued, 0);
        _viewModel?.OnChatMix(reading);
        bool newer;
        lock (_readingGate) newer = !reading.Equals(_latestReading);
        if (newer && Interlocked.CompareExchange(ref _readingQueued, 1, 0) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Render, DrainChatMix);
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
