using Ajazz.Keyboard.Hid;
using Ajazz.Keyboard.Lcd;

namespace Ajazz.Keyboard;

public enum ConnectionKind
{
    Usb,
    Dongle24G,
}

/// <summary>
/// AJAZZ AK980 (V2) protocol, reverse engineered from the stock DeviceDriver.exe (Sonix 0C45 firmware).
///
/// Wired transport:
///   MI_03 (usage page 0xFF13): 65-byte feature reports = report ID 0x00 + 64-byte command.
///   MI_02 (usage page 0xFF68): 4097-byte output reports = report ID 0x00 + 4096-byte LCD page.
/// Every transaction is: 04 18 (begin) -> 04 CMD ... COUNT -> COUNT data packets -> 04 02 (commit).
/// </summary>
public sealed class Ak980Keyboard : IDisposable
{
    public const ushort VendorId = 0x0C45;
    public const ushort UsbProductId = 0x8009;
    public const ushort DongleProductId = 0xFEFE;

    private const byte CmdPrefix = 0x04;
    private const byte OpBegin = 0x18;
    private const byte OpCommit = 0x02;
    private const byte OpLightConfig = 0x13;
    private const byte OpApply = 0xF0;
    private const byte OpCustomColors = 0x23;
    private const byte OpOptions = 0x17;
    private const byte CustomLightMode = 0x80;
    private const int CustomTablePackets = 9; // 576 bytes = [index, R, G, B] x 144, last two bytes AA 55
    private const byte OpKeyMapTop = 0x11;
    private const byte OpKeyMapFn = 0x27;
    private const byte OpMacroBegin = 0x19;
    private const byte OpMacroData = 0x15;
    private const byte OpLcdConfig = 0x28;
    private const byte OpLcdUpload = 0x72;

    private readonly HidDevice _command;
    private readonly HidDevice? _lcd;
    private readonly object _io = new();

    /// <summary>Pause before each command, from layouts/rgb-keyboard.xml &lt;cmd_delaytime value="35"/&gt;.</summary>
    public int CommandDelayMs { get; set; } = 35;

    public ConnectionKind Connection { get; }
    public string Product => _command.Info.Product;
    public LcdGeometry Lcd { get; } = LcdGeometry.AK980;

    private Ak980Keyboard(ConnectionKind kind, HidDevice command, HidDevice? lcd)
    {
        Connection = kind;
        _command = command;
        _lcd = lcd;
    }

    /// <summary>Finds and opens the keyboard. Prefers the USB cable over the 2.4G dongle when both are present.</summary>
    public static Ak980Keyboard? Open()
    {
        var all = HidDevice.Enumerate(VendorId);
        var usbCmd = all.FirstOrDefault(d => d.ProductId == UsbProductId && d.UsagePage == 0xFF13 && d.FeatureLength >= 65);
        if (usbCmd is not null)
        {
            var usbLcd = all.FirstOrDefault(d => d.ProductId == UsbProductId && d.UsagePage == 0xFF68 && d.OutputLength >= 4097);
            var cmd = HidDevice.Open(usbCmd);
            try
            {
                return new Ak980Keyboard(ConnectionKind.Usb, cmd, usbLcd is null ? null : HidDevice.Open(usbLcd));
            }
            catch
            {
                cmd.Dispose();
                throw;
            }
        }
        return null;
    }

    public static bool IsPresent() =>
        HidDevice.Enumerate(VendorId).Any(d => d.ProductId is UsbProductId or DongleProductId);

    /// <summary>
    /// What is plugged in. The keyboard only enumerates over USB in wired mode; in 2.4G mode the cable just charges
    /// and the dongle is the only way to reach it.
    /// </summary>
    public static (bool Usb, bool Dongle) Detect()
    {
        var all = HidDevice.Enumerate(VendorId);
        return (all.Any(d => d.ProductId == UsbProductId), all.Any(d => d.ProductId == DongleProductId));
    }

    public static List<HidDeviceInfo> ListInterfaces() => HidDevice.Enumerate(VendorId);

    // ---------------------------------------------------------------- transport

    /// <summary>Sends one 64-byte command packet (report ID 0 is prepended) and reads the device's echo/ack.</summary>
    public byte[] SendPacket(ReadOnlySpan<byte> packet64)
    {
        var report = new byte[65];
        packet64[..Math.Min(64, packet64.Length)].CopyTo(report.AsSpan(1));
        lock (_io)
        {
            Thread.Sleep(CommandDelayMs);
            _command.SetFeature(report);
            Thread.Sleep(CommandDelayMs);
            return _command.GetFeature(0);
        }
    }

    private void SendCommand(byte op, byte arg = 0, int count = 0, params (int Index, byte Value)[] extra)
    {
        var p = new byte[64];
        p[0] = CmdPrefix;
        p[1] = op;
        p[2] = arg;
        p[8] = (byte)count;
        p[9] = (byte)(count >> 8);
        foreach (var (i, v) in extra) p[i] = v;
        SendPacket(p);
    }

    private void Begin() => SendCommand(OpBegin);

    private void Commit() => SendCommand(OpCommit);

    /// <summary>Sends packets without the GetFeature read-back (the stock app does this for data and 0xF0 apply packets).</summary>
    private void SendNoAck(ReadOnlySpan<byte> packet64)
    {
        var report = new byte[65];
        packet64[..Math.Min(64, packet64.Length)].CopyTo(report.AsSpan(1));
        lock (_io)
        {
            Thread.Sleep(CommandDelayMs);
            _command.SetFeature(report);
        }
    }

    private void Apply()
    {
        var p = new byte[64];
        p[0] = CmdPrefix;
        p[1] = OpApply;
        SendNoAck(p);
    }

    // ---------------------------------------------------------------- lighting

    /// <summary>Sets the backlight effect (stock DeviceDriver.exe 0x42b190).</summary>
    public void SetLighting(LightSettings s)
    {
        lock (_io)
        {
            Begin();
            SendCommand(OpLightConfig, count: 1);
            SendNoAck(s.ToPacket());
            Commit();
            Apply();
        }
    }

    /// <summary>Win/Alt+Tab/Alt+F4 locks, Fn swap, sleep timer and key response level.</summary>
    public void SetOptions(KeyboardOptions o)
    {
        lock (_io)
        {
            Begin();
            SendCommand(OpOptions, 1, count: 1);
            SendNoAck(o.ToPacket());
            Commit();
        }
    }

    /// <summary>
    /// Switches to per-key custom lighting and writes one color per LED index (stock DeviceDriver.exe 0x44bda0 + 0x44beb0).
    /// LEDs missing from <paramref name="colors"/> are black.
    /// </summary>
    public void SetCustomColors(IReadOnlyDictionary<int, (byte R, byte G, byte B)> colors, int brightness = LightSettings.MaxLevel)
    {
        var mode = new byte[64];
        mode[0] = CustomLightMode;
        mode[9] = (byte)Math.Clamp(brightness, 0, LightSettings.MaxLevel);
        mode[14] = 0xAA;
        mode[15] = 0x55;

        var table = new byte[CustomTablePackets * 64];
        foreach (var (index, (r, g, b)) in colors)
        {
            if (index < 0 || index * 4 + 3 >= table.Length - 2) continue;
            table[index * 4] = (byte)index;
            table[index * 4 + 1] = r;
            table[index * 4 + 2] = g;
            table[index * 4 + 3] = b;
        }
        table[^2] = 0xAA;
        table[^1] = 0x55;

        lock (_io)
        {
            Begin();
            SendCommand(OpLightConfig, count: 1);
            SendNoAck(mode);
            Commit();
            Apply();

            Begin();
            SendCommand(OpCustomColors, count: CustomTablePackets);
            for (int i = 0; i < CustomTablePackets; i++)
                SendNoAck(table.AsSpan(i * 64, 64));
            Commit();
            Apply();
        }
    }

    /// <summary>
    /// Sets the secondary light zone (the stock app calls it "side light" and hides it for the AK980 V2;
    /// on this board it drives the logo LED). Stock DeviceDriver.exe 0x434300.
    /// </summary>
    public void SetSideLight(SideLightMode mode)
    {
        var p = new byte[64];
        p[0] = (byte)mode;
        p[14] = 0xAA;
        p[15] = 0x55;
        lock (_io)
        {
            Begin();
            SendCommand(OpLightConfig, count: 1);
            SendNoAck(p);
            Commit();
            Apply();
        }
    }

    // ---------------------------------------------------------------- key remap and macros

    /// <summary>
    /// Writes one layer's key table (stock DeviceDriver.exe 0x4183e0): 04 18, then 04 11 (top layer) or 04 27 (Fn layer)
    /// with COUNT = 9, nine 64-byte packets of <see cref="KeyMap.EncodeLayer"/>, 04 02 and 04 F0 (both acknowledged).
    /// Keys missing from <paramref name="actions"/> keep their firmware default.
    /// </summary>
    public void SetKeyMap(KeyMapLayer layer, IReadOnlyDictionary<int, KeyAction> actions)
    {
        var table = KeyMap.EncodeLayer(actions);
        lock (_io)
        {
            Begin();
            SendCommand(layer == KeyMapLayer.Fn ? OpKeyMapFn : OpKeyMapTop, count: KeyMap.Packets);
            for (int i = 0; i < KeyMap.Packets; i++)
                SendNoAck(table.AsSpan(i * 64, 64));
            Commit();
            var apply = new byte[64];
            apply[0] = CmdPrefix;
            apply[1] = OpApply;
            SendPacket(apply);
        }
    }

    /// <summary>
    /// Stores the macro list (stock DeviceDriver.exe 0x42d820): 04 19, then 04 15 with COUNT = pages, 30 ms, the pages,
    /// 30 ms, 04 02. Slot numbers are the list positions; keys reference them with <see cref="KeyActionKind.Macro"/>.
    /// An empty list clears every macro.
    /// </summary>
    public void SetMacros(IReadOnlyList<Macro> macros)
    {
        var data = MacroCodec.Encode(macros, out int pages);
        lock (_io)
        {
            SendCommand(OpMacroBegin);
            SendCommand(OpMacroData, count: pages);
            Thread.Sleep(30);
            for (int i = 0; i < pages; i++)
                SendNoAck(data.AsSpan(i * 64, 64));
            Thread.Sleep(30);
            Commit();
        }
    }

    // ---------------------------------------------------------------- LCD

    /// <summary>
    /// Sets the LCD clock. <paramref name="slot"/> is the LCD image slot the stock app sends with the clock
    /// (it always sends "currently selected list item + 1", which is what made the screen jump on launch).
    /// </summary>
    public void SyncClock(DateTime now, byte slot = 1)
    {
        lock (_io)
        {
            Begin();
            SendCommand(OpLcdConfig, extra: (8, 1));
            var p = new byte[64];
            p[0] = 0x00;
            p[1] = slot;
            p[2] = 0x5A;
            p[3] = (byte)(now.Year % 2000);
            p[4] = (byte)now.Month;
            p[5] = (byte)now.Day;
            p[6] = (byte)now.Hour;
            p[7] = (byte)now.Minute;
            p[8] = (byte)now.Second;
            p[10] = (byte)now.DayOfWeek; // SYSTEMTIME.wDayOfWeek: Sunday = 0
            p[62] = 0xAA;
            p[63] = 0x55;
            SendPacket(p);
            Commit();
        }
    }

    /// <summary>Uploads an animation to the LCD. <paramref name="progress"/> receives 0..1.</summary>
    public void UploadLcd(LcdAnimation animation, byte slot = 1, IProgress<double>? progress = null)
    {
        if (_lcd is null) throw new InvalidOperationException("LCD interface not found (USB cable required for uploads)");
        var blob = animation.Encode();
        int pages = blob.Length / LcdGeometry.PageSize;
        lock (_io)
        {
            Begin();
            SendCommand(OpLcdUpload, slot, pages);
            var report = new byte[LcdGeometry.PageSize + 1];
            for (int i = 0; i < pages; i++)
            {
                Buffer.BlockCopy(blob, i * LcdGeometry.PageSize, report, 1, LcdGeometry.PageSize);
                _lcd.Write(report, timeoutMs: 3000);
                _lcd.Read(300); // device acks each page; the stock app waits up to 300 ms
                progress?.Report((i + 1) / (double)pages);
            }
            Commit();
        }
    }

    public void Dispose()
    {
        _command.Dispose();
        _lcd?.Dispose();
    }
}
