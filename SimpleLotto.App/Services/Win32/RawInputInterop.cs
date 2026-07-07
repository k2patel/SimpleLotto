using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleLotto.App.Services.Win32;

internal static class RawInputInterop
{
    internal const uint RIDI_DEVICENAME = 0x20000007;
    internal const uint RIM_TYPEKEYBOARD = 1;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;
    internal static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDeviceList
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputDeviceList(
        [In, Out] RawInputDeviceList[]? pRawInputDeviceList,
        ref uint puiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetRawInputDeviceInfoW(
        IntPtr hDevice,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool HidD_GetProductString(
        IntPtr HidDeviceObject,
        StringBuilder Buffer,
        uint BufferLength);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool HidD_GetManufacturerString(
        IntPtr HidDeviceObject,
        StringBuilder Buffer,
        uint BufferLength);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool HidD_GetSerialNumberString(
        IntPtr HidDeviceObject,
        StringBuilder Buffer,
        uint BufferLength);
}
