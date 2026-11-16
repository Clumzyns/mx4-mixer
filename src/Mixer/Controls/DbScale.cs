using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Mixer.Controls;

/// <summary>
/// The dB marks beside a fader or meter: 0 at the top down to 48, with the bottom stop marked as silence.
/// The marks sit where a fader cap's centre line lands for that gain.
/// </summary>
public sealed class DbScale : FrameworkElement
{
    private static readonly int[] Marks = [0, 6, 12, 24, 36, 48];

    public DbScale()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    protected override Size MeasureOverride(Size available) =>
        new(28, double.IsInfinity(available.Height) ? 200 : available.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var travel = ActualHeight - Faceplate.FaderInset * 2;
        if (travel <= 0) return;
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var pen = Faceplate.Pen(Faceplate.Ink, 1);
        foreach (var mark in Marks)
            Draw(dc, mark.ToString(CultureInfo.InvariantCulture), Faceplate.FaderInset + mark / 60.0 * travel, pen, dip);
        Draw(dc, "∞", Faceplate.FaderInset + travel, pen, dip);
    }

    private void Draw(DrawingContext dc, string label, double y, Pen pen, double dip)
    {
        y = Math.Round(y) + 0.5;
        dc.DrawLine(pen, new Point(ActualWidth - 6, y), new Point(ActualWidth, y));
        var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Faceplate.Label, 10, Faceplate.Muted, dip);
        dc.DrawText(text, new Point(ActualWidth - 10 - text.Width, y - text.Height / 2));
    }
}
