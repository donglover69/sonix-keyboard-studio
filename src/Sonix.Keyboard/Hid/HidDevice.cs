using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sonix.Keyboard.Hid;

/// <summary>One HID top-level collection (one Windows device path).</summary>
public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    int Interface,
    ushort UsagePage,
    ushort Usage,
    int InputLength,
    int OutputLength,
    int FeatureLength,
    string Product);

/// <summary>Thin wrapper around a Windows HID handle: feature reports + overlapped interrupt I/O.</summary>
public sealed class HidDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream? _stream;

    public HidDeviceInfo Info { get; }

    private HidDevice(HidDeviceInfo info, SafeFileHandle handle)
    {
        Info = info;
        _handle = handle;
        if (info.InputLength > 0 || info.OutputLength > 0)
            _stream = new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true);
    }

    public static HidDevice Open(HidDeviceInfo info)
    {
        var h = Native.CreateFile(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
            Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open HID device {info.Path}");
        return new HidDevice(info, h);
    }

    /// <summary>Sends a feature report. <paramref name="report"/>[0] is the report ID; it is padded to the feature length.</summary>
    public void SetFeature(ReadOnlySpan<byte> report)
    {
        var buf = new byte[Info.FeatureLength];
        report[..Math.Min(report.Length, buf.Length)].CopyTo(buf);
        if (!Native.HidD_SetFeature(_handle, buf, buf.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_SetFeature failed");
    }

    /// <summary>Reads a feature report for <paramref name="reportId"/>; the result includes the report ID byte.</summary>
    public byte[] GetFeature(byte reportId = 0)
    {
        var buf = new byte[Info.FeatureLength];
        buf[0] = reportId;
        if (!Native.HidD_GetFeature(_handle, buf, buf.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_GetFeature failed");
        return buf;
    }

    /// <summary>Writes an output report. <paramref name="report"/>[0] is the report ID; it is padded to the output length.</summary>
    public void Write(ReadOnlySpan<byte> report, int timeoutMs = 2000)
    {
        if (_stream is null) throw new InvalidOperationException("Device has no output reports");
        var buf = new byte[Info.OutputLength];
        report[..Math.Min(report.Length, buf.Length)].CopyTo(buf);
        using var cts = new CancellationTokenSource(timeoutMs);
        _stream.WriteAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Reads one input report, or returns null on timeout.</summary>
    public byte[]? Read(int timeoutMs = 1000)
    {
        if (_stream is null) throw new InvalidOperationException("Device has no input reports");
        var buf = new byte[Info.InputLength];
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            int n = _stream.ReadAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult();
            return n > 0 ? buf : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _handle.Dispose();
    }

    public static List<HidDeviceInfo> Enumerate(ushort vendorId)
    {
        var result = new List<HidDeviceInfo>();
        Native.HidD_GetHidGuid(out var guid);
        var set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var ifData = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
            {
                var path = GetDevicePath(set, ref ifData);
                if (path is null || !path.Contains($"vid_{vendorId:x4}", StringComparison.OrdinalIgnoreCase))
                    continue;
                var info = Describe(path);
                if (info is not null) result.Add(info);
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    private static string? GetDevicePath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA ifData)
    {
        Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out int size, IntPtr.Zero);
        if (size <= 0) return null;
        var mem = Marshal.AllocHGlobal(size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on x64, 6 on x86 (DWORD + WCHAR, packed).
            Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
            if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, mem, size, out _, IntPtr.Zero))
                return null;
            return Marshal.PtrToStringUni(mem + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }

    private static HidDeviceInfo? Describe(string path)
    {
        // Zero-access open is enough to query attributes/caps, even for keyboards Windows holds exclusively.
        using var h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var attrs = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
        if (!Native.HidD_GetAttributes(h, ref attrs)) return null;
        if (!Native.HidD_GetPreparsedData(h, out var pp)) return null;
        Native.HIDP_CAPS caps;
        try { Native.HidP_GetCaps(pp, out caps); }
        finally { Native.HidD_FreePreparsedData(pp); }

        var name = new char[128];
        string product = Native.HidD_GetProductString(h, name, name.Length * 2) ? new string(name).TrimEnd('\0') : "";
        int mi = -1;
        int idx = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) int.TryParse(path.AsSpan(idx + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out mi);

        return new HidDeviceInfo(path, attrs.VendorID, attrs.ProductID, mi, caps.UsagePage, caps.Usage,
            caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength, product);
    }
}
