using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Mixer.Controls;

/// <summary>
/// A rotary knob with a tick ring. Value runs 0 to 100 over 270 degrees.
/// Drag around the knob, scroll, or use the arrow keys to turn it.
/// </summary>
public sealed class Dial : FrameworkElement
{
    private const double Sweep = 135;
    private const int Ticks = 40;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Dial),
        new FrameworkPropertyMetadata(50d,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            null, (_, value) => Math.Clamp(double.IsFinite((double)value) ? (double)value : 0d, 0d, 100d)));

    /// <summary>True draws the highlight from the top centre, for a balance. False draws it from the low stop.</summary>
    public static readonly DependencyProperty CenteredProperty = DependencyProperty.Register(
        nameof(Centered), typeof(bool), typeof(Dial), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowValueProperty = DependencyProperty.Register(
        nameof(ShowValue), typeof(bool), typeof(Dial), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(Dial), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public Dial()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool Centered { get => (bool)GetValue(CenteredProperty); set => SetValue(CenteredProperty, value); }
    public bool ShowValue { get => (bool)GetValue(ShowValueProperty); set => SetValue(ShowValueProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        var side = Math.Min(
            double.IsInfinity(available.Width) ? 300 : available.Width,
            double.IsInfinity(available.Height) ? 300 : available.Height);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 4) return;
        var k = side / 300.0;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        // A transparent square keeps the whole dial clickable, gaps between ticks included.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(centre.X - side / 2, centre.Y - side / 2, side, side));

        var minor = Faceplate.Pen(Faceplate.Ink, Math.Max(1, 1.3 * k));
        var major = Faceplate.Pen(Faceplate.Ink, Math.Max(1.4, 2 * k));
        for (var i = 0; i <= Ticks; i++)
        {
            var angle = -Sweep + i * (2 * Sweep / Ticks);
            var isMajor = i % 5 == 0;
            dc.DrawLine(isMajor ? major : minor, At(centre, angle, 114 * k), At(centre, angle, (isMajor ? 134 : 124) * k));
        }

        var pointer = (Value - 50) / 50 * Sweep;
        var from = Centered ? 0 : -Sweep;
        if (Math.Abs(pointer - from) > 0.5)
        {
            var low = Math.Min(from, pointer);
            var high = Math.Max(from, pointer);
            var radius = 141 * k;
            var arc = new StreamGeometry();
            using (var g = arc.Open())
            {
                g.BeginFigure(At(centre, low, radius), false, false);
                g.ArcTo(At(centre, high, radius), new Size(radius, radius), 0, high - low > 180, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, Faceplate.Pen(Faceplate.Accent, Math.Max(2, 5 * k)), arc);
        }

        dc.DrawEllipse(Faceplate.Ink, null, centre, 98 * k, 98 * k);
        var knurl = new Pen(Faceplate.Knurl, 6 * k) { DashStyle = new DashStyle([0.34, 0.8], 0) };
        knurl.Freeze();
        dc.DrawEllipse(null, knurl, centre, 93 * k, 93 * k);
        dc.DrawEllipse(Faceplate.KnobBody, null, centre, 81 * k, 81 * k);
        if (IsKeyboardFocused)
            dc.DrawEllipse(null, Faceplate.Pen(Faceplate.Accent, Math.Max(1, 1.5 * k)), centre, 103 * k, 103 * k);

        var inner = ShowValue ? 42 * k : 30 * k;
        dc.DrawLine(Faceplate.Pen(Faceplate.Accent, Math.Max(2.5, 6 * k), round: true), At(centre, pointer, inner), At(centre, pointer, 88 * k));

        if (!ShowValue) return;
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var number = new FormattedText(Math.Round(Value).ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Faceplate.Display, 46 * k, Faceplate.OnInk, dip);
        dc.DrawText(number, new Point(centre.X - number.Width / 2, centre.Y - number.Height / 2 - 4 * k));
        if (string.IsNullOrEmpty(Caption)) return;
        var caption = new FormattedText(Caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Faceplate.Label, Math.Max(8, 9.5 * k), Faceplate.OnInkSoft, dip);
        dc.DrawText(caption, new Point(centre.X - caption.Width / 2, centre.Y + 20 * k));
    }

    private static Point At(Point centre, double degrees, double radius)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(centre.X + radius * Math.Sin(radians), centre.Y - radius * Math.Cos(radians));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2 && Centered)
        {
            Value = 50;
            e.Handled = true;
            return;
        }
        CaptureMouse();
        Turn(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Turn(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
        Value = Math.Round(Value) + (e.Delta > 0 ? step : -step);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Right or Key.Up => 1,
            Key.Left or Key.Down => -1,
            Key.PageUp => 5,
            Key.PageDown => -5,
            _ => 0
        };
        if (step == 0) return;
        Value = Math.Round(Value) + step;
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();

    private void Turn(Point position)
    {
        var dx = position.X - ActualWidth / 2;
        var dy = position.Y - ActualHeight / 2;
        if (dx * dx + dy * dy < 36) return;
        var angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
        // In the gap at the bottom, stay on the side the knob is already on so it cannot jump end to end.
        if (Math.Abs(angle) > Sweep) angle = Value >= 50 ? Sweep : -Sweep;
        Value = Math.Round(50 + angle / Sweep * 50);
    }
}
