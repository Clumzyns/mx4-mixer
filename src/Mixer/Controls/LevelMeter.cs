using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mixer.Controls;

/// <summary>
/// A segmented level meter. Value is a peak amplitude from 0 to 1, drawn on the same 60 dB scale as the faders.
/// With <see cref="Linear"/> set, Value is drawn as a plain share of the length instead.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    private const double RangeDb = 60;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LevelMeter), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(LevelMeter), new FrameworkPropertyMetadata(Faceplate.Ink, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LinearProperty = DependencyProperty.Register(
        nameof(Linear), typeof(bool), typeof(LevelMeter), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>True fills a vertical meter from the top down, for gain reduction.</summary>
    public static readonly DependencyProperty ReversedProperty = DependencyProperty.Register(
        nameof(Reversed), typeof(bool), typeof(LevelMeter), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public LevelMeter()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public bool Linear { get => (bool)GetValue(LinearProperty); set => SetValue(LinearProperty, value); }
    public bool Reversed { get => (bool)GetValue(ReversedProperty); set => SetValue(ReversedProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var vertical = Orientation == Orientation.Vertical;
        var length = vertical ? ActualHeight : ActualWidth;
        var breadth = vertical ? ActualWidth : ActualHeight;
        if (length <= 0 || breadth <= 0) return;

        var value = double.IsFinite(Value) ? Math.Clamp(Value, 0, 1) : 0;
        var share = Linear ? value : value <= 0.001 ? 0 : Math.Clamp(1 + 20 * Math.Log10(value) / RangeDb, 0, 1);

        var segment = breadth >= 7 ? 5.0 : 4.0;
        var gap = breadth >= 7 ? 3.0 : 2.0;
        var count = Math.Max(1, (int)Math.Floor((length + gap) / (segment + gap)));
        var lit = (int)Math.Round(share * count);
        if (share > 0 && lit == 0) lit = 1;

        for (var i = 0; i < count; i++)
        {
            var brush = i < lit ? Fill : Faceplate.Dim;
            var offset = i * (segment + gap);
            Rect rect;
            if (!vertical) rect = new Rect(offset, 0, segment, breadth);
            else if (Reversed) rect = new Rect(0, offset, breadth, segment);
            else rect = new Rect(0, length - offset - segment, breadth, segment);
            dc.DrawRectangle(brush, null, rect);
        }
    }
}
