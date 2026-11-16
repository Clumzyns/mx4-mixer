using HidSharp;

namespace Mixer.Audio;

/// <summary>
/// Reads the Arctis Nova Pro base-station dial over USB HID.
/// The protocol is the one Sonar uses: report id 0x06 to enable, report id 0x07 / command 0x45 for the two levels.
/// Audio is not played through this device. The numbers are applied to the Game and Chat faders.
/// </summary>
public sealed class ChatMixMonitor : IDisposable
{
    public const ushort VendorId = 0x1038;

    // Nova Pro wired GameDAC and Nova Pro Wireless base station, from the nova-chatmix and rust-arctis-chatmix device tables.
    private static readonly ushort[] ProductIds = [0x12CB, 0x12CD, 0x12E0, 0x12E5, 0x225D];

    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private bool _enabled = true;
    private bool _disposed;
    private int _lastGame = -1;
    private int _lastChat = -1;

    public ChatMixMonitor()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Mixer.ChatMix"
        };
    }

    public event EventHandler<ChatMixReading>? Reading;

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set
        {
            lock (_gate) _enabled = value;
            _wake.Set();
        }
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        try { _thread.Join(1500); } catch { /* background read */ }
        _wake.Dispose();
    }

    private void Loop()
    {
        while (!_disposed)
        {
            _wake.Reset();
            if (!Enabled)
            {
                Publish(false, false, "ChatMix is off. Game and Chat faders are manual.");
                _wake.Wait(1000);
                continue;
            }

            HidDevice? device = null;
            try
            {
                device = FindBaseStation();
            }
            catch (Exception ex)
            {
                Services.Log.Error("ChatMix device scan failed", ex);
            }

            if (device == null)
            {
                Publish(false, false, "Nova Pro base station not found. Plug it in over USB. Quit SteelSeries GG if the dial does nothing.");
                _wake.Wait(1000);
                continue;
            }

            try
            {
                Pump(device);
            }
            catch (Exception ex)
            {
                Services.Log.Error("ChatMix read failed", ex);
                Publish(false, false, "Couldn't open the base station. Quit SteelSeries GG, then toggle ChatMix.");
                _wake.Wait(1000);
            }
        }
    }

    private void Pump(HidDevice device)
    {
        if (!device.TryOpen(out var stream))
        {
            Publish(false, false, "The base station is in use. Quit SteelSeries GG, then toggle ChatMix.");
            return;
        }

        using (stream)
        {
            stream.ReadTimeout = 1000;
            try
            {
                Send(stream, device, 0x06, 0x8D, 0x01);
                Send(stream, device, 0x06, 0x49, 0x01);
            }
            catch (Exception ex)
            {
                Services.Log.Error("ChatMix enable command failed", ex);
                Publish(false, false, "The base station rejected ChatMix mode. Quit SteelSeries GG and toggle ChatMix.");
                return;
            }

            var name = string.IsNullOrWhiteSpace(device.GetProductName()) ? "Nova Pro" : device.GetProductName();
            Publish(true, false, name + " connected. Turn the dial. Quit SteelSeries GG if the numbers do not move.");
            var buffer = new byte[Math.Max(device.GetMaxInputReportLength(), 64)];
            while (!_disposed && Enabled)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (IOException)
                {
                    Publish(false, false, "Base station unplugged. It will reconnect when it is back.");
                    return;
                }

                if (!TryParse(buffer, read, out var game, out var chat)) continue;
                if (game == _lastGame && chat == _lastChat) continue;
                _lastGame = game;
                _lastChat = chat;
                Publish(true, true, $"Dial {game} game / {chat} chat. Quit SteelSeries GG if this stops.");
            }

            try
            {
                Send(stream, device, 0x06, 0x49, 0x00);
                Send(stream, device, 0x06, 0x8D, 0x00);
            }
            catch
            {
                // The station may already be gone.
            }
        }
    }

    private static void Send(HidStream stream, HidDevice device, byte reportId, byte command, byte value)
    {
        var length = device.GetMaxOutputReportLength();
        if (length <= 0) length = 64;
        var buffer = new byte[length];
        buffer[0] = reportId;
        if (length > 1) buffer[1] = command;
        if (length > 2) buffer[2] = value;
        stream.Write(buffer);
    }

    private static bool TryParse(byte[] buffer, int length, out int game, out int chat)
    {
        game = 0;
        chat = 0;
        if (length < 4) return false;
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] != 0x07 || buffer[i + 1] != 0x45) continue;
            var gameValue = buffer[i + 2];
            var chatValue = buffer[i + 3];
            if (gameValue > 100 || chatValue > 100) continue;
            game = gameValue;
            chat = chatValue;
            return true;
        }
        return false;
    }

    private static HidDevice? FindBaseStation()
    {
        HidDevice? best = null;
        var bestScore = -1;
        foreach (var device in DeviceList.Local.GetHidDevices(VendorId))
        {
            if (!ProductIds.Contains((ushort)device.ProductID)) continue;
            var score = device.GetMaxInputReportLength() + device.GetMaxOutputReportLength();
            if (score > bestScore)
            {
                best = device;
                bestScore = score;
            }
        }
        return best;
    }

    private void Publish(bool connected, bool hasLevels, string detail)
    {
        Reading?.Invoke(this, new ChatMixReading(connected, hasLevels, _lastGame, _lastChat, detail));
    }
}

public readonly record struct ChatMixReading(bool Connected, bool HasLevels, int Game, int Chat, string Detail);
