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
        if (Fill == null || Track == null || FillScale == null) return;
        var amount = Math.Clamp(Value, 0d, 1d);
        if (Orientation == Orientation.Horizontal)
        {
            Fill.RenderTransformOrigin = new Point(0, 0.5);
            FillScale.ScaleX = amount;
            FillScale.ScaleY = 1;
        }
        else
        {
            Fill.RenderTransformOrigin = new Point(0.5, 1);
            FillScale.ScaleX = 1;
            FillScale.ScaleY = amount;
        }
    }
}
