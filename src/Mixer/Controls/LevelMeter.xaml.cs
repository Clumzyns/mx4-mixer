using System.Windows;
using System.Windows.Controls;

namespace Mixer.Controls;

public partial class LevelMeter : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(LevelMeter),
        new PropertyMetadata(0d, OnChanged));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation),
        typeof(Orientation),
        typeof(LevelMeter),
        new PropertyMetadata(Orientation.Vertical, OnChanged));

    public LevelMeter()
    {
        InitializeComponent();
        Loaded += (_, _) => Update();
        SizeChanged += (_, _) => Update();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LevelMeter)d).Update();

    private void Update()
    {
        if (Fill == null || Track == null) return;
        var amount = Math.Clamp(Value, 0d, 1d);
        if (Orientation == Orientation.Horizontal)
        {
            Fill.VerticalAlignment = VerticalAlignment.Stretch;
            Fill.HorizontalAlignment = HorizontalAlignment.Left;
            Fill.Width = Math.Max(0, Track.ActualWidth * amount);
            Fill.Height = double.NaN;
        }
        else
        {
            Fill.HorizontalAlignment = HorizontalAlignment.Stretch;
            Fill.VerticalAlignment = VerticalAlignment.Bottom;
            Fill.Height = Math.Max(0, Track.ActualHeight * amount);
            Fill.Width = double.NaN;
        }
    }
}
