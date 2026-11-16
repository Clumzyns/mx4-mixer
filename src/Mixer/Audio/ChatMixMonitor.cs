using System.Text;
using HidSharp;
using Mixer.Services;

namespace Mixer.Audio;

/// <summary>
/// Reads the Arctis Nova Pro base station over USB HID.
/// Wired GameDAC (PID 0x12CB/0x12CD) accepts init on MI_04 COL01 (usage page 0xFFC0):
/// 06 10, 06 20, 06 80, 06 18, 06 49 01, 06 8D 01. COL02 (usage page 0xFF00) is input-only.
/// ChatMix, once enabled with 06 49 01, is report 07 45 GG CC (game %, chat %).
/// The wired knob is a separate report, 07 25 NN, and is not ChatMix unless the user opts in.
/// Both MI_04 collections are read at the same time.
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
    private readonly Dictionary<string, string> _unknownHex = new(StringComparer.Ordinal);
    private bool _enabled = true;
    private bool _useKnob;
    private bool _disposed;
    private bool _statusConnected;
    private string _statusDetail = "";
    private int _lastGame = -1;
    private int _lastChat = -1;
    private int _lastKnob = -1;
    private string _lastRaw = "";

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

    public bool UseKnobAsChatMix
    {
        get => Volatile.Read(ref _useKnob);
        set => Volatile.Write(ref _useKnob, value);
    }

    public void Start() => _thread.Start();

    public void ReplayKnob()
    {
        ChatMixReading? reading = null;
        lock (_gate)
        {
            if (_lastKnob < 0 || string.IsNullOrEmpty(_lastRaw)) return;
            reading = new ChatMixReading(
                true,
                true,
                ChatMixReportKind.Knob,
                0,
                0,
                _lastKnob,
                $"Knob report 07 25 value {_lastKnob}.",
                _lastRaw);
        }

        if (reading is ChatMixReading update)
            Reading?.Invoke(this, update);
    }

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
                Publish(false, false, ChatMixReportKind.None, _lastGame, _lastChat, _lastKnob,
                    "USB dial reading is off. The slider still sets Game and Chat.", "");
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
                Publish(false, false, ChatMixReportKind.None, _lastGame, _lastChat, _lastKnob,
                    "Couldn't scan USB devices.", "");
                _wake.Wait(1000);
                continue;
            }

            if (devices.Count == 0)
            {
                Publish(false, false, ChatMixReportKind.None, _lastGame, _lastChat, _lastKnob,
                    "Nova Pro base station not found. Plug it in over USB.", "");
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
                Publish(false, false, ChatMixReportKind.None, _lastGame, _lastChat, _lastKnob,
                    "Couldn't open the base station. Quit SteelSeries GG, then toggle ChatMix.", "");
                _wake.Wait(1000);
            }
        }
    }

    private void Pump(List<HidDevice> devices)
    {
        var running = 1;
        var streams = new List<(HidDevice Device, HidStream Stream, bool Wrote)>();
        var threads = new List<Thread>();

        foreach (var device in devices)
        {
            var tag = CollectionTag(device.DevicePath);
            Log.Info($"ChatMix open {tag} path={device.DevicePath} pid=0x{device.ProductID:X4} in={device.GetMaxInputReportLength()} out={SafeOutputLength(device)} {DescribeUsage(device)}");
            if (!device.TryOpen(out var stream))
            {
                Log.Info($"ChatMix open failed for {tag} " + device.DevicePath);
                continue;
            }

            stream.ReadTimeout = 250;
            var wrote = false;
            if (ShouldWriteInit(device, out var writeReason))
            {
                var init = device.ProductID is 0x12CB or 0x12CD ? WiredInit : WirelessInit;
                foreach (var packet in init)
                {
                    try
                    {
                        Write(stream, device, packet);
                        wrote = true;
                        Log.Info($"ChatMix wrote {Hex(packet, packet.Length)} to {tag} {device.DevicePath}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"ChatMix write failed on {tag} {device.DevicePath}", ex);
                        break;
                    }
                }
            }
            else
            {
                Log.Info($"ChatMix skip init on {tag} ({writeReason}); reading input anyway. path={device.DevicePath}");
            }

            streams.Add((device, stream, wrote));
            var captured = stream;
            var capturedDevice = device;
            var thread = new Thread(() => ReadLoop(captured, capturedDevice, () => Volatile.Read(ref running) == 1))
            {
                IsBackground = true,
                Name = "Mixer.ChatMix.Read"
            };
            thread.Start();
            threads.Add(thread);
        }

        if (streams.Count == 0)
        {
            Publish(false, false, ChatMixReportKind.None, _lastGame, _lastChat, _lastKnob,
                "The base station is in use. Quit SteelSeries GG, then toggle ChatMix.", "");
            return;
        }

        Log.Info($"ChatMix reading {streams.Count} collection(s) concurrently.");
        Publish(true, false, ChatMixReportKind.None, _lastGame, _lastChat, _lastKnob,
            "Arctis Nova Pro connected. ChatMix reports are 07 45. The slider works either way.", "");
        while (!_disposed && Enabled && !_wake.Wait(200))
        {
        }

        Volatile.Write(ref running, 0);
        foreach (var thread in threads)
        {
            try { thread.Join(400); } catch { /* already leaving */ }
        }

        foreach (var (device, stream, wrote) in streams)
        {
            if (wrote)
            {
                try
                {
                    Write(stream, device, [0x06, 0x49, 0x00]);
                    Write(stream, device, [0x06, 0x8D, 0x00]);
                }
                catch { /* already gone */ }
            }
            try { stream.Dispose(); } catch { /* already gone */ }
        }
    }

    private void ReadLoop(HidStream stream, HidDevice device, Func<bool> running)
    {
        var buffer = new byte[Math.Max(device.GetMaxInputReportLength(), 64)];
        var tag = CollectionTag(device.DevicePath);
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
                Log.Info($"ChatMix input closed for {tag} " + device.DevicePath);
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

            if (read <= 0) continue;
            var snapshot = new byte[read];
            Buffer.BlockCopy(buffer, 0, snapshot, 0, read);

            ChatMixReading? update = null;
            string? log = null;
            lock (_gate)
                update = Interpret(snapshot, tag, out log);
            if (log != null)
                Log.Info(log);
            if (update is ChatMixReading reading)
                Reading?.Invoke(this, reading);
        }
    }

    private ChatMixReading? Interpret(byte[] buffer, string tag, out string? log)
    {
        log = null;
        var hex = Hex(buffer, buffer.Length);
        var raw = tag + "  " + hex;
        var kind = Classify(buffer, out var game, out var chat, out var knob);

        if (kind == ChatMixReportKind.Levels)
        {
            if (_lastRaw == raw && game == _lastGame && chat == _lastChat) return null;
            var changed = game != _lastGame || chat != _lastChat;
            _lastGame = game;
            _lastChat = chat;
            _lastRaw = raw;
            if (changed)
            {
                log = buffer[0] == 0x07
                    ? $"ChatMix report 07 45 game={game} chat={chat} ({raw})"
                    : $"ChatMix report 45 game={game} chat={chat} ({raw})";
            }
            return new ChatMixReading(true, true, ChatMixReportKind.Levels, game, chat, _lastKnob,
                $"Dial {game} game / {chat} chat.", raw);
        }

        if (kind == ChatMixReportKind.Knob)
        {
            var apply = UseKnobAsChatMix;
            if (_lastRaw == raw && knob == _lastKnob) return null;
            var changed = knob != _lastKnob;
            _lastKnob = knob;
            _lastRaw = raw;
            if (changed)
            {
                log = apply
                    ? $"ChatMix knob report 07 25 value={knob} ({raw})"
                    : $"ChatMix knob report 07 25 value={knob} ignored; knob-as-ChatMix is off ({raw})";
            }

            var detail = apply
                ? $"Knob report 07 25 value {knob}."
                : $"Knob report 07 25 value {knob}. Not ChatMix unless enabled in Settings.";
            return new ChatMixReading(true, apply, ChatMixReportKind.Knob, _lastGame, _lastChat, knob, detail, raw);
        }

        var id = buffer.Length > 0 ? buffer[0] : (byte)0;
        var sub = buffer.Length > 1 ? buffer[1] : (byte)0;
        var key = id == 0x07 ? $"07-{sub:X2}" : $"{id:X2}";
        var label = id == 0x07 ? $"0x07/0x{sub:X2}" : $"0x{id:X2}";
        if (_unknownHex.TryAdd(key, hex))
            log = $"ChatMix unrecognized report id {label}: {raw}";

        if (_lastRaw == raw) return null;
        _lastRaw = raw;
        return new ChatMixReading(true, false, ChatMixReportKind.Unknown, _lastGame, _lastChat, _lastKnob,
            $"Unrecognized report id {label}.", raw);
    }

    /// <summary>
    /// Classifies the front of one input report. 07 25 is never a ChatMix level report.
    /// </summary>
    public static ChatMixReportKind Classify(byte[] buffer, out int game, out int chat, out int knob)
    {
        game = 0;
        chat = 0;
        knob = 0;
        if (buffer.Length < 2) return ChatMixReportKind.Unknown;

        if (buffer[0] == 0x07 && buffer[1] == 0x45 && buffer.Length >= 4)
        {
            if (buffer[2] > 100 || buffer[3] > 100) return ChatMixReportKind.Unknown;
            game = buffer[2];
            chat = buffer[3];
            return ChatMixReportKind.Levels;
        }

        if (buffer[0] == 0x07 && buffer[1] == 0x25 && buffer.Length >= 3)
        {
            knob = buffer[2];
            return ChatMixReportKind.Knob;
        }

        if (buffer[0] == 0x45 && buffer.Length >= 3 && buffer[1] <= 100 && buffer[2] <= 100)
        {
            game = buffer[1];
            chat = buffer[2];
            return ChatMixReportKind.Levels;
        }

        return ChatMixReportKind.Unknown;
    }

    private static bool ShouldWriteInit(HidDevice device, out string reason)
    {
        var pages = UsagePages(device);
        var hasVendor = pages.Contains(0xFFC0);
        var hasInputOnly = pages.Contains(0xFF00);
        if (hasInputOnly && !hasVendor)
        {
            reason = "usagePage=0xFF00";
            return false;
        }

        if (hasVendor)
        {
            reason = "usagePage=0xFFC0";
            return true;
        }

        var output = SafeOutputLength(device);
        if (output == 0)
        {
            reason = "no output report";
            return false;
        }

        reason = pages.Count == 0
            ? "usagePage=?"
            : "usagePage=" + string.Join(",", pages.Distinct().Select(page => "0x" + page.ToString("X")));
        return true;
    }

    private static int SafeOutputLength(HidDevice device)
    {
        try { return device.GetMaxOutputReportLength(); }
        catch { return -1; }
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
        var pages = UsagePages(device);
        return pages.Count == 0
            ? "usagePage=?"
            : "usagePage=" + string.Join(",", pages.Distinct().Select(page => "0x" + page.ToString("X")));
    }

    private static List<int> UsagePages(HidDevice device)
    {
        var pages = new List<int>();
        try
        {
            var raw = device.GetRawReportDescriptor();
            for (var i = 0; i < raw.Length;)
            {
                if (raw[i] == 0xFE)
                {
                    if (i + 2 >= raw.Length) break;
                    i += 3 + raw[i + 1];
                    continue;
                }

                var sizeCode = raw[i] & 0x03;
                var size = sizeCode == 3 ? 4 : sizeCode;
                var type = (raw[i] >> 2) & 0x03;
                var tag = (raw[i] >> 4) & 0x0F;
                if (i + 1 + size > raw.Length) break;
                if (type == 1 && tag == 0 && size is 1 or 2)
                {
                    var page = size == 1 ? raw[i + 1] : raw[i + 1] | (raw[i + 2] << 8);
                    pages.Add(page);
                }
                i += 1 + size;
            }
        }
        catch
        {
            // Caller treats an empty list as an unknown usage page.
        }
        return pages;
    }

    private static string CollectionTag(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "HID";
        var lower = path.ToLowerInvariant();
        var col = "COL??";
        var at = lower.LastIndexOf("col", StringComparison.Ordinal);
        if (at >= 0 && at + 5 <= lower.Length && char.IsDigit(lower[at + 3]) && char.IsDigit(lower[at + 4]))
            col = lower.Substring(at, 5).ToUpperInvariant();
        var mi = lower.Contains("mi_04", StringComparison.Ordinal) ? "MI_04" : "HID";
        return mi + " " + col;
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

    private void Publish(bool connected, bool hasLevels, ChatMixReportKind kind, int game, int chat, int knob, string detail, string raw)
    {
        if (string.IsNullOrEmpty(raw) && connected == _statusConnected && detail == _statusDetail)
            return;
        _statusConnected = connected;
        _statusDetail = detail;
        Reading?.Invoke(this, new ChatMixReading(connected, hasLevels, kind, game, chat, knob, detail, raw));
    }
}

public enum ChatMixReportKind
{
    None = 0,
    Levels = 1,
    Knob = 2,
    Unknown = 3
}

public readonly record struct ChatMixReading(
    bool Connected,
    bool HasLevels,
    ChatMixReportKind Kind,
    int Game,
    int Chat,
    int Knob,
    string Detail,
    string RawReport);
