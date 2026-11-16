using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Mixer.Audio;
using Mixer.ViewModels;

namespace Mixer;

public partial class MainWindow : Window
{
    private bool _dragArmed;
    private Point _dragStart;
    private FrameworkElement? _dragElement;

    public event EventHandler? HiddenToTray;

    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(WndProc);
            TrySetBorderColor(handle);
        };
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.IsExiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && !App.IsExiting)
        {
            WindowState = WindowState.Normal;
            HideToTray();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && DataContext is MainViewModel viewModel && viewModel.SettingsOpen)
        {
            viewModel.SettingsOpen = false;
            e.Handled = true;
        }
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        try { DragMove(); }
        catch (InvalidOperationException) { /* button already released */ }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SettingsOpen = !viewModel.SettingsOpen;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void SettingsBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SettingsOpen = false;
    }

    private void EatClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ColumnScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = ColumnScroll.ViewportWidth;
        var height = ColumnScroll.ViewportHeight;
        if (!double.IsNaN(width) && width > 0)
            ColumnHost.Width = Math.Max(1120, width);
        if (!double.IsNaN(height) && height > 0)
            ColumnHost.Height = height;
    }

    private void OutputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.IsSyncing) return;
        if (sender is ComboBox box && box.SelectedValue is string id && id.Length > 0)
            viewModel.SelectOutput(id);
    }

    private void InputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.IsSyncing) return;
        if (sender is ComboBox box && box.SelectedValue is string id && id.Length > 0)
            viewModel.SelectInput(id);
    }

    private void ChannelDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.IsSyncing) return;
        if (sender is not ComboBox box || box.DataContext is not ChannelViewModel channel) return;
        if (box.SelectedValue is not string id) return;
        channel.SetDeviceFromUi(id);
    }

    private void ChipChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.IsSyncing) return;
        if (sender is not ComboBox box || box.DataContext is not AppChipViewModel chip) return;
        if (box.SelectedValue is not ChannelId id || chip.Channel == id) return;
        viewModel.MoveApp(chip.ExeKey, id);
    }

    private void Channel_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(AppChipViewModel.DragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        if (sender is Border border) border.Tag = "over";
    }

    private void Channel_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border) border.Tag = null;
    }

    private void Channel_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border) border.Tag = null;
        if (!e.Data.GetDataPresent(AppChipViewModel.DragFormat)) return;
        if (e.Data.GetData(AppChipViewModel.DragFormat) is not string exeKey) return;
        if (sender is FrameworkElement element && element.DataContext is ChannelViewModel channel && DataContext is MainViewModel viewModel)
            viewModel.MoveApp(exeKey, channel.Id);
        e.Handled = true;
    }

    private void Chip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource as DependencyObject))
        {
            _dragArmed = false;
            return;
        }
        _dragArmed = true;
        _dragStart = e.GetPosition(null);
        _dragElement = sender as FrameworkElement;
    }

    private void Chip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || !ReferenceEquals(sender, _dragElement)) return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragArmed = false;
        if (sender is FrameworkElement element && element.DataContext is AppChipViewModel chip)
            DragDrop.DoDragDrop(element, new DataObject(AppChipViewModel.DragFormat, chip.ExeKey), DragDropEffects.Move);
    }

    private void Chip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragArmed = false;

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Slider or ComboBox or Button or Thumb or RepeatButton or TextBoxBase)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ApplyWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ApplyWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, 2);
        if (monitor == IntPtr.Zero) return;
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = info.rcWork;
        var bounds = info.rcMonitor;
        mmi.ptMaxPosition.x = work.Left - bounds.Left;
        mmi.ptMaxPosition.y = work.Top - bounds.Top;
        mmi.ptMaxSize.x = work.Right - work.Left;
        mmi.ptMaxSize.y = work.Bottom - work.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private static void TrySetBorderColor(IntPtr hwnd)
    {
        var color = 0x00120F0D;
        try
        {
            DwmSetWindowAttribute(hwnd, 34, ref color, 4);
            DwmSetWindowAttribute(hwnd, 35, ref color, 4);
        }
        catch (DllNotFoundException)
        {
            // Windows 10 builds without this attribute still get the dark client area.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointNative ptReserved;
        public PointNative ptMaxSize;
        public PointNative ptMaxPosition;
        public PointNative ptMinTrackSize;
        public PointNative ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectNative rcMonitor;
        public RectNative rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
