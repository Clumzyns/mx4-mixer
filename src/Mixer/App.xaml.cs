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
    private TrayService? _tray;
    private MainWindow? _window;
    private MixerConfig? _config;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        SessionEnding += (_, _) =>
        {
            IsExiting = true;
            TrySave();
        };

        if (!SingleInstance.TryAcquire(ShowExisting, out _instance))
        {
            Shutdown();
            return;
        }

        try
        {
            _config = ConfigStore.Load();
            _engine = new AudioEngine(_config);
            var viewModel = new MainViewModel(_engine, _config);
            _window = new MainWindow(viewModel);
            _tray = new TrayService(_window, viewModel, ExitApplication);
            _engine.Start();
            if (_config.StartMinimized)
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
        try { _engine?.Dispose(); } catch { /* already stopped */ }
        try { _tray?.Dispose(); } catch { /* already stopped */ }
        try { _instance?.Dispose(); } catch { /* already stopped */ }
        Shutdown();
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
