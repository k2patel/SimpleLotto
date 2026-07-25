using System;
using System.Runtime.InteropServices;
using SimpleLotto.App.Services.Win32;

namespace SimpleLotto.App.Services;

internal sealed class ScannerMessageWindow : IDisposable
{
    private const string WindowClassName = "SimpleLottoScannerMessageWindow";
    private readonly RawInputInterop.WindowProcedure _windowProcedure;
    private IntPtr _window;
    private bool _disposed;

    public ScannerMessageWindow()
    {
        _windowProcedure = OnWindowMessage;
        var instance = RawInputInterop.GetModuleHandleW(null);
        var windowClass = new RawInputInterop.WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<RawInputInterop.WindowClassEx>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            Instance = instance,
            ClassName = WindowClassName
        };

        var atom = RawInputInterop.RegisterClassExW(ref windowClass);
        if (atom == 0 && Marshal.GetLastWin32Error() != 1410)
            throw new InvalidOperationException($"Unable to register scanner message window (Win32 {Marshal.GetLastWin32Error()}).");

        _window = RawInputInterop.CreateWindowExW(
            0,
            WindowClassName,
            WindowClassName,
            0,
            0,
            0,
            0,
            0,
            new IntPtr(RawInputInterop.MessageOnlyWindow),
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (_window == IntPtr.Zero)
            throw new InvalidOperationException($"Unable to create scanner message window (Win32 {Marshal.GetLastWin32Error()}).");
    }

    public IntPtr Handle => _window;

    public event Action<IntPtr>? RawInputReceived;

    public event Action? DevicesChanged;

    private IntPtr OnWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == RawInputInterop.WmInput)
            {
                RawInputReceived?.Invoke(lParam);
                return RawInputInterop.DefWindowProcW(window, message, wParam, lParam);
            }

            if (message == RawInputInterop.WmInputDeviceChange)
            {
                DevicesChanged?.Invoke();
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Scanner message processing failed for 0x{message:X}.", ex);
        }

        return RawInputInterop.DefWindowProcW(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_window != IntPtr.Zero)
        {
            RawInputInterop.DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
    }
}
