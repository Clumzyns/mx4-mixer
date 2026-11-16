using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mixer.Controls;

public partial class Fader : UserControl
{
    private static readonly SolidColorBrush Accent = Freeze("#B6F23A");
    private static readonly SolidColorBrush MutedBrush = Freeze("#5C6573");

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(Fader),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnVisualChanged));

    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted),
        typeof(bool),
        typeof(Fader),
        new PropertyMetadata(false, OnVisualChanged));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation),
        typeof(Orientation),
        typeof(Fader),
        new PropertyMetadata(Orientation.Vertical, OnVisualChanged));

    public Fader()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateFill();
        SizeChanged += (_, _) => UpdateFill();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Fader)d).UpdateFill();

    private void UpdateFill()
    {
        if (VFill == null || HFill == null) return;
        var horizontal = Orientation == Orientation.Horizontal;
        VerticalLayout.Visibility = horizontal ? Visibility.Collapsed : Visibility.Visible;
        HorizontalLayout.Visibility = horizontal ? Visibility.Visible : Visibility.Collapsed;
        var brush = IsMuted ? MutedBrush : Accent;
        var amount = Math.Clamp(Value / 100d, 0d, 1d);
        VFill.Background = brush;
        HFill.Background = brush;
        VFill.Height = Math.Max(0, VGroove.ActualHeight * amount);
        HFill.Width = Math.Max(0, HGroove.ActualWidth * amount);
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
