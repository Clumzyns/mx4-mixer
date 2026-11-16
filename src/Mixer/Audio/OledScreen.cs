namespace Mixer.Audio;

/// <summary>
/// 128x64 1bpp frame for the Arctis Nova Pro base station.
/// Packet layout matches ggoled and SteelClock's Nova Pro driver:
/// feature report 0x06, command 0x93, column-major pages, LSB at the top of each page.
/// Two 64-wide strips cover the panel. Command 0x95 returns the stock screen.
/// </summary>
internal static class OledScreen
{
    public const int Width = 128;
    public const int Height = 64;
    private const int ReportSize = 1024;

    public readonly record struct Picture(
        int Master,
        int GameMix,
        int ChatMix,
        int Game,
        int Chat,
        int Media,
        int Aux,
        bool GameMuted,
        bool ChatMuted,
        bool MediaMuted,
        bool AuxMuted);

    /// <summary>
    /// The mix screen, laid out like the window: the ChatMix dial on the left with Game and Chat under it,
    /// four channel faders on the right, and master along the bottom right.
    /// </summary>
    public static byte[][] Pack(Picture picture)
    {
        var pixels = new bool[Width * Height];
        var canvas = new Canvas(pixels);

        // ChatMix dial: tick ring, knob outline, pointer, and a bar from the top to the pointer.
        const int cx = 31, cy = 30;
        var balance = ChatMixMath.FromLevels(picture.GameMix, picture.ChatMix);
        var pointer = (balance - 50) / 50.0 * 135.0;
        for (var i = 0; i <= 16; i++)
        {
            var angle = -135 + i * 16.875;
            canvas.Spoke(cx, cy, angle, i % 2 == 0 ? 21 : 23, 25);
        }
        canvas.Ring(cx, cy, 17);
        canvas.Ring(cx, cy, 16);
        canvas.Spoke(cx, cy, pointer, 4, 13, thick: true);
        for (var angle = Math.Min(0, pointer); angle <= Math.Max(0, pointer); angle += 1.5)
        {
            canvas.Spoke(cx, cy, angle, 28, 29);
        }
        var game = Math.Clamp(picture.GameMix, 0, 100).ToString("0");
        var chat = Math.Clamp(picture.ChatMix, 0, 100).ToString("0");
        canvas.Text(1, 58, "G");
        canvas.Text(7, 58, game);
        canvas.Text(61 - Canvas.Measure("C " + chat), 58, "C");
        canvas.Text(61 - Canvas.Measure(chat), 58, chat);

        // Channel faders.
        canvas.Fader(74, "G", picture.Game, picture.GameMuted);
        canvas.Fader(89, "C", picture.Chat, picture.ChatMuted);
        canvas.Fader(104, "M", picture.Media, picture.MediaMuted);
        canvas.Fader(119, "A", picture.Aux, picture.AuxMuted);

        // Master: label, value at double size, and a segmented bar.
        var master = Math.Clamp(picture.Master, 0, 100);
        canvas.Text(69, 50, "MST");
        var value = master.ToString("0");
        canvas.Text(128 - Canvas.Measure(value) * 2, 45, value, scale: 2);
        const int segments = 15;
        var lit = (int)Math.Round(master / 100.0 * segments);
        for (var i = 0; i < segments; i++)
        {
            var x = 69 + i * 4;
            if (i < lit) canvas.Fill(x, 58, 3, 5);
            else canvas.Fill(x, 62, 3, 1);
        }
        return Pack(pixels);
    }

    /// <summary>Packs a row-major 128x64 frame, true meaning lit.</summary>
    public static byte[][] Pack(bool[] pixels) => [Strip(pixels, 0), Strip(pixels, 64)];

    public static byte[] ReturnToUi()
    {
        var packet = new byte[ReportSize];
        packet[0] = 0x06;
        packet[1] = 0x95;
        return packet;
    }

    private static byte[] Strip(bool[] pixels, int srcX)
    {
        var packet = new byte[ReportSize];
        packet[0] = 0x06;
        packet[1] = 0x93;
        packet[2] = (byte)srcX;
        packet[3] = 0;
        packet[4] = 64;
        packet[5] = 64;
        for (var x = 0; x < 64; x++)
        {
            for (var y = 0; y < Height; y++)
            {
                if (!pixels[y * Width + srcX + x]) continue;
                var index = x * Height + y;
                packet[6 + (index >> 3)] |= (byte)(1 << (index & 7));
            }
        }
        return packet;
    }

    internal sealed class Canvas
    {
        private readonly bool[] _pixels;
        public Canvas(bool[] pixels) => _pixels = pixels;

        /// <summary>A line along a dial radius. Zero degrees is straight up and angles run clockwise.</summary>
        public void Spoke(int cx, int cy, double degrees, int from, int to, bool thick = false)
        {
            var radians = degrees * Math.PI / 180;
            var sin = Math.Sin(radians);
            var cos = Math.Cos(radians);
            for (var r = from * 2; r <= to * 2; r++)
            {
                var x = (int)Math.Round(cx + r / 2.0 * sin);
                var y = (int)Math.Round(cy - r / 2.0 * cos);
                Plot(x, y);
                if (!thick) continue;
                Plot(x + 1, y);
                Plot(x, y + 1);
            }
        }

        public void Ring(int cx, int cy, int radius)
        {
            for (var step = 0; step < 360; step++)
            {
                var radians = step * Math.PI / 180;
                Plot((int)Math.Round(cx + radius * Math.Sin(radians)), (int)Math.Round(cy - radius * Math.Cos(radians)));
            }
        }

        public void Fill(int x, int y, int width, int height)
        {
            for (var j = 0; j < height; j++)
                for (var i = 0; i < width; i++)
                    Plot(x + i, y + j);
        }

        /// <summary>A vertical fader: slot, cap at the level, letter underneath. A muted channel has a hollow cap and an X.</summary>
        public void Fader(int x, string letter, int percent, bool muted)
        {
            const int top = 2, bottom = 30;
            Fill(x, top, 2, bottom - top + 1);
            var centre = bottom - (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * (bottom - top));
            for (var j = -2; j <= 2; j++)
                for (var i = -4; i <= 5; i++)
                {
                    var edge = j == -2 || j == 2 || i == -4 || i == 5;
                    if (muted && !edge) Unplot(x + i, centre + j);
                    else Plot(x + i, centre + j);
                }
            Text(x - 1, 35, muted ? "X" : letter);
        }

        public static int Measure(string text) => text.Length == 0 ? 0 : text.Length * 4 - 1;

        public void Text(int x, int y, string text, int scale = 1)
        {
            foreach (var ch in text)
            {
                Glyph(x, y, ch, scale);
                x += 4 * scale;
            }
        }

        private void Glyph(int x, int y, char ch, int scale)
        {
            var rows = Rows(ch);
            for (var row = 0; row < rows.Length; row++)
            {
                var bits = rows[row];
                for (var col = 0; col < 3; col++)
                    if ((bits & (1 << (2 - col))) != 0)
                        Fill(x + col * scale, y + row * scale, scale, scale);
            }
        }

        public void Plot(int x, int y)
        {
            if ((uint)x >= Width || (uint)y >= Height) return;
            _pixels[y * Width + x] = true;
        }

        private void Unplot(int x, int y)
        {
            if ((uint)x >= Width || (uint)y >= Height) return;
            _pixels[y * Width + x] = false;
        }

        private static byte[] Rows(char ch) => char.ToUpperInvariant(ch) switch
        {
            '0' => [0b111, 0b101, 0b101, 0b101, 0b111],
            '1' => [0b010, 0b110, 0b010, 0b010, 0b111],
            '2' => [0b111, 0b001, 0b111, 0b100, 0b111],
            '3' => [0b111, 0b001, 0b111, 0b001, 0b111],
            '4' => [0b101, 0b101, 0b111, 0b001, 0b001],
            '5' => [0b111, 0b100, 0b111, 0b001, 0b111],
            '6' => [0b111, 0b100, 0b111, 0b101, 0b111],
            '7' => [0b111, 0b001, 0b001, 0b001, 0b001],
            '8' => [0b111, 0b101, 0b111, 0b101, 0b111],
            '9' => [0b111, 0b101, 0b111, 0b001, 0b111],
            'A' => [0b010, 0b101, 0b111, 0b101, 0b101],
            'C' => [0b111, 0b100, 0b100, 0b100, 0b111],
            'D' => [0b110, 0b101, 0b101, 0b101, 0b110],
            'E' => [0b111, 0b100, 0b110, 0b100, 0b111],
            'G' => [0b111, 0b100, 0b101, 0b101, 0b111],
            'H' => [0b101, 0b101, 0b111, 0b101, 0b101],
            'I' => [0b111, 0b010, 0b010, 0b010, 0b111],
            'M' => [0b101, 0b111, 0b101, 0b101, 0b101],
            'S' => [0b111, 0b100, 0b111, 0b001, 0b111],
            'T' => [0b111, 0b010, 0b010, 0b010, 0b010],
            'U' => [0b101, 0b101, 0b101, 0b101, 0b111],
            'X' => [0b101, 0b101, 0b010, 0b101, 0b101],
            ':' => [0b000, 0b010, 0b000, 0b010, 0b000],
            _ => [0, 0, 0, 0, 0]
        };
    }
}
