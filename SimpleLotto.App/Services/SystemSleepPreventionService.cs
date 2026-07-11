using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SimpleLotto.App.Services;

public sealed class SystemSleepPreventionService : IDisposable
{
    private IntPtr _powerRequestHandle;
    private bool _systemRequired;
    private bool _displayRequired;

    public void Start()
    {
        if (_powerRequestHandle != IntPtr.Zero)
            return;

        var context = new ReasonContext
        {
            Version = 0,
            Flags = PowerRequestContextSimpleString,
            SimpleReasonString = "SimpleLotto is running"
        };

        _powerRequestHandle = PowerCreateRequest(ref context);
        if (_powerRequestHandle == IntPtr.Zero || _powerRequestHandle == InvalidHandleValue)
        {
            var error = Marshal.GetLastWin32Error();
            _powerRequestHandle = IntPtr.Zero;
            AppLog.Error("Unable to create sleep prevention power request.", new Win32Exception(error));
            return;
        }

        _systemRequired = SetPowerRequest(PowerRequestType.SystemRequired);
        _displayRequired = SetPowerRequest(PowerRequestType.DisplayRequired);

        if (_systemRequired)
            AppLog.Info("System sleep prevention enabled while SimpleLotto is running.");
    }

    public void Dispose()
    {
        if (_powerRequestHandle == IntPtr.Zero)
            return;

        if (_displayRequired)
            _ = PowerClearRequest(_powerRequestHandle, PowerRequestType.DisplayRequired);
        if (_systemRequired)
            _ = PowerClearRequest(_powerRequestHandle, PowerRequestType.SystemRequired);

        _displayRequired = false;
        _systemRequired = false;
        _ = CloseHandle(_powerRequestHandle);
        _powerRequestHandle = IntPtr.Zero;
        AppLog.Info("System sleep prevention released.");
    }

    private bool SetPowerRequest(PowerRequestType requestType)
    {
        if (PowerSetRequest(_powerRequestHandle, requestType))
            return true;

        AppLog.Error(
            $"Unable to set {requestType} power request.",
            new Win32Exception(Marshal.GetLastWin32Error()));
        return false;
    }

    private const uint PowerRequestContextSimpleString = 0x1;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private enum PowerRequestType
    {
        SystemRequired,
        DisplayRequired
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr powerRequestHandle, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr powerRequestHandle, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
