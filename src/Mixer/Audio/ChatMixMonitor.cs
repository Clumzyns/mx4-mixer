using System.Text;
using HidSharp;
using Mixer.Services;

namespace Mixer.Audio;

/// <summary>
/// Reads the Arctis Nova Pro base-station dial over USB HID.
/// Wired GameDAC (PID 0x12CB/0x12CD) init and the 0x07 0x45 report match
/// rust-arctis-chatmix / nova-chatmix: interface 4, commands 0x06 0x49 0x01 and 0x06 0x8D 0x01.
/// Both MI_04 collections are opened. Reports are taken from input reads.
/// </summary>
public sealed class ChatMixMonitor : IDisposable
{
    public const ushort VendorId = 0x1038;

    private static readonly ushort[] ProductIds = [0x12CB, 0x12CD, 0x12E0, 0x12E5, 0x225D];

    private static readonly byte[][] WiredInit =
    [
        [0x06, 0x10],
        [0x06, 0x20],
        [0x06, 0x80],
        [0x06, 0x18],
        [0x06, 0x49, 0x01],
        [0x06, 0x8D, 0x01]
    ];

    private static readonly byte[][] WirelessInit =
    [
        [0x06, 0x8D, 0x01],
        [0x06, 0x49, 0x01]
    ];

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
        try { _thread.Join(2000); } catch { /* background read */ }
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

            List<HidDevice> devices;
            try
            {
                devices = FindInterface4();
            }
            catch (Exception ex)
            {
                Log.Error("ChatMix device scan failed", ex);
                Publish(false, false, "Couldn't scan USB devices.");
                _wake.Wait(1000);
                continue;
            }

            if (devices.Count == 0)
            {
                Publish(false, false, "Nova Pro base station not found. Plug it in over USB.");
                _wake.Wait(1000);
                continue;
            }

            try
            {
                Pump(devices);
            }
            catch (Exception ex)
            {
                Log.Error("ChatMix read failed", ex);
                Publish(false, false, "Couldn't open the base station. Quit SteelSeries GG, then toggle ChatMix.");
                _wake.Wait(1000);
            }
        }
    }

    private void Pump(List<HidDevice> devices)
    {
        var verboseUntil = DateTime.UtcNow.AddSeconds(30);
        var running = 1;
        var streams = new List<(HidDevice Device, HidStream Stream)>();
        var threads = new List<Thread>();

        foreach (var device in devices)
        {
            Log.Info($"ChatMix open path={device.DevicePath} pid=0x{device.ProductID:X4} in={device.GetMaxInputReportLength()} out={device.GetMaxOutputReportLength()} {DescribeUsage(device)}");
            if (!device.TryOpen(out var stream))
            {
                Log.Info("ChatMix open failed for " + device.DevicePath);
                continue;
            }

            stream.ReadTimeout = 250;
            streams.Add((device, stream));
            var init = device.ProductID is 0x12CB or 0x12CD ? WiredInit : WirelessInit;
            foreach (var packet in init)
            {
                try
                {
                    Write(stream, device, packet);
                    Log.Info("ChatMix wrote " + Hex(packet, packet.Length) + " to " + device.DevicePath);
                }
                catch (Exception ex)
                {
                    Log.Error("ChatMix write failed on " + device.DevicePath, ex);
                }
            }

            var captured = stream;
            var capturedDevice = device;
            var thread = new Thread(() => ReadLoop(captured, capturedDevice, verboseUntil, () => Volatile.Read(ref running) == 1))
            {
                IsBackground = true,
                Name = "Mixer.ChatMix.Read"
            };
            thread.Start();
            threads.Add(thread);
        }

        if (streams.Count == 0)
        {
            Publish(false, false, "The base station is in use. Quit SteelSeries GG, then toggle ChatMix.");
            return;
        }

        Publish(true, false, "Arctis Nova Pro connected. Turn the dial. Quit SteelSeries GG if the numbers do not move.");
        while (!_disposed && Enabled && !_wake.Wait(200))
        {
        }

        Volatile.Write(ref running, 0);
        foreach (var thread in threads)
        {
            try { thread.Join(400); } catch { /* already leaving */ }
        }

        foreach (var (device, stream) in streams)
        {
            try
            {
                Write(stream, device, [0x06, 0x49, 0x00]);
                Write(stream, device, [0x06, 0x8D, 0x00]);
            }
            catch { /* already gone */ }
            try { stream.Dispose(); } catch { /* already gone */ }
        }
    }

    private void ReadLoop(HidStream stream, HidDevice device, DateTime verboseUntil, Func<bool> running)
    {
        var buffer = new byte[Math.Max(device.GetMaxInputReportLength(), 64)];
        while (running())
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
                Log.Info("ChatMix input closed for " + device.DevicePath);
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("ChatMix read error", ex);
                return;
            }

            if (DateTime.UtcNow <= verboseUntil)
                Log.Info($"ChatMix input {device.DevicePath} {Hex(buffer, read)}");

            if (!TryParse(buffer, read, out var game, out var chat)) continue;
            if (game == _lastGame && chat == _lastChat) continue;
            _lastGame = game;
            _lastChat = chat;
            Log.Info($"ChatMix dial game={game} chat={chat}");
            Publish(true, true, $"Dial {game} game / {chat} chat.");
        }
    }

    private static void Write(HidStream stream, HidDevice device, byte[] packet)
    {
        var length = device.GetMaxOutputReportLength();
        if (length < packet.Length) length = packet.Length;
        if (length <= 0) length = 64;
        var buffer = new byte[length];
        Buffer.BlockCopy(packet, 0, buffer, 0, packet.Length);
        stream.Write(buffer);
    }

    private static bool TryParse(byte[] buffer, int length, out int game, out int chat)
    {
        game = 0;
        chat = 0;
        if (length < 3) return false;
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] != 0x07 || buffer[i + 1] != 0x45) continue;
            if (buffer[i + 2] > 100 || buffer[i + 3] > 100) continue;
            game = buffer[i + 2];
            chat = buffer[i + 3];
            return true;
        }

        if (buffer[0] == 0x45 && length >= 3 && buffer[1] <= 100 && buffer[2] <= 100)
        {
            game = buffer[1];
            chat = buffer[2];
            return true;
        }

        return false;
    }

    private static List<HidDevice> FindInterface4()
    {
        var chosen = new List<HidDevice>();
        foreach (var device in DeviceList.Local.GetHidDevices(VendorId))
        {
            if (!ProductIds.Contains((ushort)device.ProductID)) continue;
            var path = device.DevicePath ?? "";
            Log.Info($"HID seen pid=0x{device.ProductID:X4} path={path} {DescribeUsage(device)}");
            if (path.Contains("mi_04", StringComparison.OrdinalIgnoreCase))
                chosen.Add(device);
        }

        if (chosen.Count == 0)
            Log.Info("No MI_04 vendor collections for the Nova Pro. Consumer-control MI_03 is left closed.");
        return chosen;
    }

    private static string DescribeUsage(HidDevice device)
    {
        try
        {
            var raw = device.GetRawReportDescriptor();
            var pages = new List<string>();
            for (var i = 0; i < raw.Length; i++)
            {
                var size = raw[i] & 0x03;
                var type = (raw[i] >> 2) & 0x03;
                var tag = (raw[i] >> 4) & 0x0F;
                if (type != 1 || tag != 0 || size is < 1 or > 2 || i + size >= raw.Length) continue;
                var page = size == 1 ? raw[i + 1] : raw[i + 1] | (raw[i + 2] << 8);
                pages.Add("0x" + page.ToString("X"));
                i += size;
            }
            return pages.Count == 0
                ? "usagePage=? descriptorBytes=" + raw.Length
                : "usagePage=" + string.Join(",", pages.Distinct());
        }
        catch (Exception ex)
        {
            return "usage unavailable (" + ex.GetType().Name + ")";
        }
    }

    private static string Hex(byte[] buffer, int length)
    {
        var count = Math.Min(length, 64);
        var text = new StringBuilder(count * 3);
        for (var i = 0; i < count; i++)
        {
            if (i > 0) text.Append(' ');
            text.Append(buffer[i].ToString("X2"));
        }
        return text.ToString();
    }

    private void Publish(bool connected, bool hasLevels, string detail)
    {
        Reading?.Invoke(this, new ChatMixReading(connected, hasLevels, _lastGame, _lastChat, detail));
    }
}

public readonly record struct ChatMixReading(bool Connected, bool HasLevels, int Game, int Chat, string Detail);
