using System.Windows;
using System.Windows.Media;

namespace Mixer.Controls;

/// <summary>
/// Colours and type shared by the drawn controls. The same values live in MixerTheme.xaml for markup.
/// </summary>
internal static class Faceplate
{
    public static readonly SolidColorBrush Ink = Brush("#1B1A17");
    public static readonly SolidColorBrush Paper = Brush("#E8E3D6");
    public static readonly SolidColorBrush Muted = Brush("#5A564D");
    public static readonly SolidColorBrush Accent = Brush("#F0501E");
    public static readonly SolidColorBrush KnobBody = Brush("#24221E");
    public static readonly SolidColorBrush Knurl = Brush("#4A463E");
    public static readonly SolidColorBrush OnInk = Brush("#EFEBDD");
    public static readonly SolidColorBrush OnInkSoft = Brush("#B9B4A6");
    public static readonly SolidColorBrush Dim = Brush("#2E1B1A17");
    public static readonly SolidColorBrush CapOff = Brush("#6B675E");

    public static readonly Typeface Display =
        new(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Condensed);
    public static readonly Typeface Label =
        new(new FontFamily("Cascadia Mono, Consolas, Courier New"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Half the height of a vertical fader cap. Scales and meters inset by this to line up with the cap centre.</summary>
    public const double FaderInset = 15;

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static Pen Pen(Brush brush, double thickness, bool round = false)
    {
        var pen = new Pen(brush, thickness);
        if (round)
        {
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
        }
        pen.Freeze();
        return pen;
    }
}
