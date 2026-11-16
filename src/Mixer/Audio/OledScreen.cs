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

    public static byte[][] Pack(Picture picture)
    {
        var pixels = new bool[Width * Height];
        var canvas = new Canvas(pixels);
        canvas.Row(0, "MST", picture.Master, false);
        canvas.Mix(11, picture.GameMix, picture.ChatMix);
        canvas.Row(22, "GAM", picture.Game, picture.GameMuted);
        canvas.Row(33, "CHT", picture.Chat, picture.ChatMuted);
        canvas.Row(44, "MED", picture.Media, picture.MediaMuted);
        canvas.Row(55, "AUX", picture.Aux, picture.AuxMuted);
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

        public void Row(int y, string label, int percent, bool muted)
        {
            percent = Math.Clamp(percent, 0, 100);
            Text(0, y, label);
            Bar(16, y, 78, 5, muted ? 0 : percent);
            Text(96, y, percent.ToString("0"));
            if (muted) Cross(120, y);
        }

        public void Mix(int y, int game, int chat)
        {
            Text(0, y, "MIX");
            Text(16, y, "G");
            Bar(20, y, 36, 5, game);
            Text(60, y, "C");
            Bar(64, y, 36, 5, chat);
        }

        private void Bar(int x, int y, int width, int height, int percent)
        {
            for (var i = 0; i < width; i++)
            {
                Plot(x + i, y);
                Plot(x + i, y + height - 1);
            }
            for (var j = 0; j < height; j++)
            {
                Plot(x, y + j);
                Plot(x + width - 1, y + j);
            }
            var fill = (width - 2) * Math.Clamp(percent, 0, 100) / 100;
            for (var i = 0; i < fill; i++)
                for (var j = 1; j < height - 1; j++)
                    Plot(x + 1 + i, y + j);
        }

        private void Cross(int x, int y)
        {
            for (var i = 0; i < 5; i++)
            {
                Plot(x + i, y + i);
                Plot(x + 4 - i, y + i);
            }
        }

        public void Text(int x, int y, string text)
        {
            foreach (var ch in text)
            {
                Glyph(x, y, ch);
                x += 4;
            }
        }

        private void Glyph(int x, int y, char ch)
        {
            var rows = Rows(ch);
            for (var row = 0; row < rows.Length; row++)
            {
                var bits = rows[row];
                for (var col = 0; col < 3; col++)
                    if ((bits & (1 << (2 - col))) != 0)
                        Plot(x + col, y + row);
            }
        }

        public void Plot(int x, int y)
        {
            if ((uint)x >= Width || (uint)y >= Height) return;
            _pixels[y * Width + x] = true;
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
