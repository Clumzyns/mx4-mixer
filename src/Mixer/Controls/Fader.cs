using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mixer.Controls;

/// <summary>
/// A slot with a sliding cap. Value runs 0 to 100. Vertical faders put 100 at the top.
/// Drag the cap, click the slot to jump, or scroll.
/// </summary>
public sealed class Fader : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Fader),
        new FrameworkPropertyMetadata(100d,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            null, (_, value) => Math.Clamp(double.IsFinite((double)value) ? (double)value : 0d, 0d, 100d)));

    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted), typeof(bool), typeof(Fader), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(Fader),
        new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    private double _grabOffset;

    public Fader()
    {
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsMuted { get => (bool)GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    private bool Vertical => Orientation == Orientation.Vertical;
    private double CapLength => Vertical ? Faceplate.FaderInset * 2 : 12;
    private double Travel => Math.Max(1, (Vertical ? ActualHeight : ActualWidth) - CapLength);

    protected override Size MeasureOverride(Size available) =>
        Vertical
            ? new Size(52, double.IsInfinity(available.Height) ? 200 : available.Height)
            : new Size(double.IsInfinity(available.Width) ? 120 : available.Width, 18);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var amount = Value / 100.0;
        var line = IsMuted ? Faceplate.CapOff : Faceplate.Accent;
        if (Vertical)
        {
            var x = ActualWidth / 2;
            dc.DrawRoundedRectangle(Faceplate.Ink, null, new Rect(x - 3, 4, 6, Math.Max(0, ActualHeight - 8)), 3, 3);
            var centre = CapLength / 2 + (1 - amount) * Travel;
            var cap = new Rect(x - 26, centre - 15, 52, 30);
            dc.DrawRoundedRectangle(Faceplate.Paper, null, cap, 4, 4);
            cap.Inflate(-2, -2);
            dc.DrawRoundedRectangle(Faceplate.Ink, null, cap, 2.5, 2.5);
            dc.DrawRectangle(line, null, new Rect(x - 17, centre - 1.5, 34, 3));
        }
        else
        {
            var y = ActualHeight / 2;
            dc.DrawRoundedRectangle(Faceplate.Ink, null, new Rect(2, y - 1.5, Math.Max(0, ActualWidth - 4), 3), 1.5, 1.5);
            var centre = CapLength / 2 + amount * Travel;
            var cap = new Rect(centre - 6, y - 9, 12, 18);
            dc.DrawRoundedRectangle(Faceplate.Paper, null, cap, 3, 3);
            cap.Inflate(-1.5, -1.5);
            dc.DrawRoundedRectangle(Faceplate.Ink, null, cap, 2, 2);
            dc.DrawRectangle(line, null, new Rect(centre - 1, y - 5, 2, 10));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var at = Along(e.GetPosition(this));
        var cap = CapCentre();
        _grabOffset = Math.Abs(at - cap) <= CapLength / 2 ? at - cap : 0;
        CaptureMouse();
        Move(at);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Move(Along(e.GetPosition(this)));
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

    private double Along(Point position) => Vertical ? position.Y : position.X;

    private double CapCentre()
    {
        var amount = Value / 100.0;
        return CapLength / 2 + (Vertical ? 1 - amount : amount) * Travel;
    }

    private void Move(double at)
    {
        var share = Math.Clamp((at - _grabOffset - CapLength / 2) / Travel, 0, 1);
        Value = Math.Round((Vertical ? 1 - share : share) * 100);
    }
}
