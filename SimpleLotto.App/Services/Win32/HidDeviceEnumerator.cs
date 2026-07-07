using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using SimpleLotto.App.Models;
using static SimpleLotto.App.Services.Win32.RawInputInterop;

namespace SimpleLotto.App.Services.Win32;

internal static class HidDeviceEnumerator
{
    public static IReadOnlyList<EnumeratedDevice> Enumerate()
    {
        var devices = new List<EnumeratedDevice>();
        uint numDevices = 0;
        var structSize = (uint)Marshal.SizeOf<RawInputDeviceList>();

        var probe = GetRawInputDeviceList(null, ref numDevices, structSize);
        if (probe == unchecked((uint)-1) || numDevices == 0)
            return devices;

        var list = new RawInputDeviceList[numDevices];
        var count = GetRawInputDeviceList(list, ref numDevices, structSize);
        if (count == unchecked((uint)-1))
            return devices;

        for (var i = 0; i < count; i++)
        {
            if (list[i].dwType != RIM_TYPEKEYBOARD)
                continue;

            var info = ResolveHandle(list[i].hDevice);
            if (info is not null)
                devices.Add(new EnumeratedDevice(list[i].hDevice, info));
        }

        return devices;
    }

    public static HidDeviceInfo? ResolveHandle(IntPtr hDevice)
    {
        var path = GetDeviceName(hDevice);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var (vid, pid) = ParseVidPidFromPath(path);
        if (string.IsNullOrWhiteSpace(vid) || string.IsNullOrWhiteSpace(pid))
            return null;

        var (product, manufacturer, serial) = ReadHidStrings(path);
        return new HidDeviceInfo
        {
            Vid = vid,
            Pid = pid,
            Serial = serial,
            Product = product,
            Manufacturer = manufacturer,
            DevicePath = path
        };
    }

    public sealed record EnumeratedDevice(IntPtr Handle, HidDeviceInfo Info);

    private static string GetDeviceName(IntPtr hDevice)
    {
        uint size = 0;
        var probe = GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (probe == unchecked((uint)-1) || size == 0)
            return string.Empty;

        var buffer = Marshal.AllocHGlobal((int)(size * sizeof(char)));
        try
        {
            var got = GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buffer, ref size);
            if (got == unchecked((uint)-1) || got == 0)
                return string.Empty;
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (string Vid, string Pid) ParseVidPidFromPath(string path)
    {
        var vid = ExtractToken(path, "VID_");
        var pid = ExtractToken(path, "PID_");
        return (vid.ToUpperInvariant(), pid.ToUpperInvariant());
    }

    private static string ExtractToken(string path, string marker)
    {
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return string.Empty;

        index += marker.Length;
        return index + 4 > path.Length
            ? string.Empty
            : path.Substring(index, 4);
    }

    private static (string Product, string Manufacturer, string Serial) ReadHidStrings(string path)
    {
        var handle = CreateFileW(
            path,
            dwDesiredAccess: 0,
            dwShareMode: FILE_SHARE_READ | FILE_SHARE_WRITE,
            lpSecurityAttributes: IntPtr.Zero,
            dwCreationDisposition: OPEN_EXISTING,
            dwFlagsAndAttributes: 0,
            hTemplateFile: IntPtr.Zero);

        if (handle == InvalidHandleValue)
            return (string.Empty, string.Empty, string.Empty);

        try
        {
            return (
                ReadHidString(handle, HidD_GetProductString),
                ReadHidString(handle, HidD_GetManufacturerString),
                ReadHidString(handle, HidD_GetSerialNumberString));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private delegate bool HidStringReader(IntPtr handle, StringBuilder buffer, uint bufferLength);

    private static string ReadHidString(IntPtr handle, HidStringReader reader)
    {
        var buffer = new StringBuilder(256);
        try
        {
            if (!reader(handle, buffer, (uint)(buffer.Capacity * sizeof(char))))
                return string.Empty;
            return buffer.ToString().Trim('\0').Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
