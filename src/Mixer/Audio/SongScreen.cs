using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mixer.Services;

namespace Mixer.Audio;

/// <summary>
/// Draws the playing song as 128x64 one-bit frames for the Nova Pro screen:
/// title, artist, a progress bar, and elapsed and total time.
/// A title or artist wider than the screen scrolls. Each line is drawn once with WPF text
/// and then moved in plain code, so a scroll step costs no text layout. Use from one STA thread.
/// </summary>
internal sealed class SongScreen
{
    /// <summary>Everything that decides how a frame looks. Two equal frames draw the same pixels.</summary>
    public readonly record struct Frame(string Title, string Artist, int TitleShift, int ArtistShift, int Elapsed, int Total, int Fill);

    private const int Margin = 1;
    private const int Room = OledScreen.Width - Margin * 2;
    private const int ScrollGap = 36;
    private const int ScrollPixelsPerSecond = 30;
    private const int ScrollPauseMs = 2000;
    private const int BarHeight = 6;
    private const int BarInner = OledScreen.Width - 2;
    private const int MaxStripWidth = 4096;

    private static readonly Typeface TitleFace =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface ArtistFace =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private string? _title;
    private string? _artist;
    private Strip _titleStrip;
    private Strip _artistStrip;
    private long _scrollStart;

    /// <summary>Starts the scroll from the beginning, for when the song screen comes back.</summary>
    public void Restart(long nowMs) => _scrollStart = nowMs;

    public Frame Next(SpotifySong.Track track, long nowMs, DateTimeOffset clock)
    {
        if (track.Title != _title || track.Artist != _artist)
        {
            _titleStrip = Draw(track.Title, TitleFace, 15);
            _artistStrip = Draw(track.Artist, ArtistFace, 12);
            _title = track.Title;
            _artist = track.Artist;
            _scrollStart = nowMs;
        }

        var elapsed = -1;
        var total = -1;
        var fill = 0;
        if (track.HasTimeline)
        {
            var position = track.PositionAt(clock);
            elapsed = (int)position.TotalSeconds;
            total = (int)track.Duration.TotalSeconds;
            fill = (int)(position.Ticks * BarInner / track.Duration.Ticks);
        }

        var running = nowMs - _scrollStart;
        return new Frame(track.Title, track.Artist, Shift(_titleStrip.Width, running), Shift(_artistStrip.Width, running), elapsed, total, fill);
    }

    /// <summary>Pixels for a frame made by the latest <see cref="Next"/>.</summary>
    public bool[] Pixels(Frame frame)
    {
        var pixels = new bool[OledScreen.Width * OledScreen.Height];
        var timed = frame.Total > 0;
        var textBlock = _titleStrip.Height + 1 + _artistStrip.Height;
        // With no timeline the two lines sit in the middle of the screen.
        var top = timed ? 0 : Math.Max(0, (OledScreen.Height - textBlock) / 2);

        Blit(pixels, _titleStrip, frame.TitleShift, top);
        Blit(pixels, _artistStrip, frame.ArtistShift, top + _titleStrip.Height + 1);
        if (!timed) return pixels;

        var canvas = new OledScreen.Canvas(pixels);
        const int barTop = 46;
        for (var x = 0; x < OledScreen.Width; x++)
        {
            canvas.Plot(x, barTop);
            canvas.Plot(x, barTop + BarHeight - 1);
        }
        for (var y = 0; y < BarHeight; y++)
        {
            canvas.Plot(0, barTop + y);
            canvas.Plot(OledScreen.Width - 1, barTop + y);
        }
        for (var x = 0; x < Math.Clamp(frame.Fill, 0, BarInner); x++)
            for (var y = 1; y < BarHeight - 1; y++)
                canvas.Plot(1 + x, barTop + y);

        const int timeTop = 56;
        var total = Clock(frame.Total);
        canvas.Text(0, timeTop, Clock(Math.Min(frame.Elapsed, frame.Total)));
        canvas.Text(OledScreen.Width - (total.Length * 4 - 1), timeTop, total);
        return pixels;
    }

    private static string Clock(int seconds)
    {
        seconds = Math.Max(0, seconds);
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>How far a line has scrolled. It rests at the start, runs once around, then rests again.</summary>
    private static int Shift(int width, long runningMs)
    {
        if (width <= Room) return 0;
        var loop = width + ScrollGap;
        var cycleMs = ScrollPauseMs + loop * 1000L / ScrollPixelsPerSecond;
        var at = runningMs % cycleMs;
        if (at < ScrollPauseMs) return 0;
        return (int)Math.Min(loop - 1, (at - ScrollPauseMs) * ScrollPixelsPerSecond / 1000);
    }

    private static void Blit(bool[] pixels, Strip strip, int shift, int top)
    {
        if (strip.Pixels == null) return;
        var scrolls = strip.Width > Room;
        Copy(pixels, strip, Margin - shift, top);
        if (scrolls) Copy(pixels, strip, Margin - shift + strip.Width + ScrollGap, top);
    }

    private static void Copy(bool[] pixels, Strip strip, int left, int top)
    {
        var fromX = Math.Max(0, -left);
        var toX = Math.Min(strip.Width, OledScreen.Width - left);
        for (var y = 0; y < strip.Height; y++)
        {
            var row = top + y;
            if ((uint)row >= OledScreen.Height) continue;
            for (var x = fromX; x < toX; x++)
                if (strip.Pixels![y * strip.Width + x])
                    pixels[row * OledScreen.Width + left + x] = true;
        }
    }

    private static Strip Draw(string text, Typeface face, double size)
    {
        var line = new FormattedText(string.IsNullOrWhiteSpace(text) ? " " : text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, face, size, Brushes.White, null, TextFormattingMode.Display, 1.0)
        {
            MaxLineCount = 1
        };
        var width = Math.Clamp((int)Math.Ceiling(line.WidthIncludingTrailingWhitespace) + 1, 1, MaxStripWidth);
        var height = Math.Clamp((int)Math.Ceiling(line.Height), 1, OledScreen.Height);

        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Aliased);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            context.DrawText(line, new Point(0, 0));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var raw = new byte[width * height * 4];
        bitmap.CopyPixels(raw, width * 4, 0);

        var pixels = new bool[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = raw[i * 4 + 1] >= 128;
        return new Strip(pixels, width, height);
    }

    private readonly record struct Strip(bool[]? Pixels, int Width, int Height);
}
