using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using SimpleLotto.App.Models;
using SimpleLotto.App.Services.Win32;
using Windows.System;

namespace SimpleLotto.App.Services;

/// <summary>
/// Captures the selected HID keyboard scanner with Raw Input, independent of
/// focus or tray state. This is the same message-window model used by WindowsPOS.
/// </summary>
internal sealed class ScannerInputService : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly StringBuilder _scanBuffer = new();
    private ScannerMessageWindow? _messageWindow;
    private string _vid = string.Empty;
    private string _pid = string.Empty;
    private string _serial = string.Empty;
    private IntPtr _deviceHandle;
    private int _idleFlushVersion;
    private bool _registered;
    private bool _disposed;

    public ScannerInputService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public event Action<string>? ScanReceived;

    public event Action<bool>? CaptureAvailabilityChanged;

    public bool IsActivelyCapturing => _registered && _deviceHandle != IntPtr.Zero;

    public void Configure(string vid, string pid, string serial)
    {
        if (_disposed)
            return;

        _vid = vid?.Trim() ?? string.Empty;
        _pid = pid?.Trim() ?? string.Empty;
        _serial = serial?.Trim() ?? string.Empty;
        _scanBuffer.Clear();
        _idleFlushVersion++;

        if (string.IsNullOrWhiteSpace(_vid) || string.IsNullOrWhiteSpace(_pid))
        {
            _deviceHandle = IntPtr.Zero;
            Unregister();
            CaptureAvailabilityChanged?.Invoke(false);
            return;
        }

        EnsureMessageWindow();
        Register();
        ResolvePairedDevice();
    }

    private void EnsureMessageWindow()
    {
        if (_messageWindow is not null)
            return;

        _messageWindow = new ScannerMessageWindow();
        _messageWindow.RawInputReceived += OnRawInputReceived;
        _messageWindow.DevicesChanged += ResolvePairedDevice;
    }

    private void Register()
    {
        if (_registered || _messageWindow is null)
            return;

        var device = new RawInputInterop.RawInputDevice
        {
            UsagePage = RawInputInterop.HidUsagePageGeneric,
            Usage = RawInputInterop.HidUsageGenericKeyboard,
            Flags = RawInputInterop.RidevInputSink | RawInputInterop.RidevDevNotify,
            TargetWindow = _messageWindow.Handle
        };
        if (!RawInputInterop.RegisterRawInputDevices(
                new[] { device },
                1,
                (uint)Marshal.SizeOf<RawInputInterop.RawInputDevice>()))
        {
            AppLog.Error(
                $"RegisterRawInputDevices failed (Win32 {Marshal.GetLastWin32Error()}).",
                new InvalidOperationException("Background scanner capture is unavailable."));
            return;
        }

        _registered = true;
    }

    private void Unregister()
    {
        if (!_registered)
            return;

        var device = new RawInputInterop.RawInputDevice
        {
            UsagePage = RawInputInterop.HidUsagePageGeneric,
            Usage = RawInputInterop.HidUsageGenericKeyboard,
            Flags = RawInputInterop.RidevRemove,
            TargetWindow = IntPtr.Zero
        };
        RawInputInterop.RegisterRawInputDevices(
            new[] { device },
            1,
            (uint)Marshal.SizeOf<RawInputInterop.RawInputDevice>());
        _registered = false;
    }

    private void ResolvePairedDevice()
    {
        var wasCapturing = IsActivelyCapturing;
        var match = HidDeviceEnumerator.Enumerate().FirstOrDefault(device =>
            device.Info.MatchesIdentity(_vid, _pid, _serial));
        _deviceHandle = match?.Handle ?? IntPtr.Zero;
        if (wasCapturing != IsActivelyCapturing)
            CaptureAvailabilityChanged?.Invoke(IsActivelyCapturing);
    }

    private void OnRawInputReceived(IntPtr rawInput)
    {
        if (!IsActivelyCapturing)
            return;

        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputInterop.RawInputHeader>();
        var probe = RawInputInterop.GetRawInputData(rawInput, RawInputInterop.RidInput, IntPtr.Zero, ref size, headerSize);
        if (probe == unchecked((uint)-1) || size == 0)
            return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var received = RawInputInterop.GetRawInputData(rawInput, RawInputInterop.RidInput, buffer, ref size, headerSize);
            if (received == unchecked((uint)-1) || received == 0)
                return;

            var input = Marshal.PtrToStructure<RawInputInterop.RawInputKeyboard>(buffer);
            if (input.Header.Type != RawInputInterop.RIM_TYPEKEYBOARD ||
                input.Header.Device != _deviceHandle ||
                (input.Keyboard.Flags & RawInputInterop.RiKeyBreak) != 0)
            {
                return;
            }

            ProcessKey((VirtualKey)input.Keyboard.VirtualKey);
        }
        catch (Exception ex)
        {
            AppLog.Error("Paired scanner input could not be read.", ex);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ProcessKey(VirtualKey key)
    {
        if (key is VirtualKey.Enter or VirtualKey.Tab)
        {
            if (_scanBuffer.Length == 0)
                return;

            var raw = _scanBuffer.ToString();
            _scanBuffer.Clear();
            _idleFlushVersion++;
            DispatchScan(raw);
            return;
        }

        if (TryMapKey(key, out var character))
        {
            _scanBuffer.Append(character);
            ScheduleIdleFlush();
        }
    }

    private void ScheduleIdleFlush()
    {
        const int idleFlushMilliseconds = 400;
        const int staleDropMilliseconds = 5000;
        var version = ++_idleFlushVersion;
        _ = Task.Delay(idleFlushMilliseconds).ContinueWith(_ =>
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (version != _idleFlushVersion || _scanBuffer.Length == 0)
                    return;

                if (_scanBuffer.Length >= 4)
                {
                    var raw = _scanBuffer.ToString();
                    _scanBuffer.Clear();
                    _idleFlushVersion++;
                    DispatchScan(raw);
                    return;
                }

                _ = Task.Delay(staleDropMilliseconds - idleFlushMilliseconds).ContinueWith(_ =>
                {
                    _dispatcherQueue.TryEnqueue(() => DropStalePartialScan(version, staleDropMilliseconds));
                });
            });
        });
    }

    private void DropStalePartialScan(int version, int staleDropMilliseconds)
    {
        if (version != _idleFlushVersion || _scanBuffer.Length == 0)
            return;

        var droppedLength = _scanBuffer.Length;
        _scanBuffer.Clear();
        _idleFlushVersion++;
        AppLog.Info($"Discarded incomplete paired scanner input after {staleDropMilliseconds} ms (length {droppedLength}).");
    }

    private void DispatchScan(string raw)
    {
        _dispatcherQueue.TryEnqueue(() => ScanReceived?.Invoke(raw));
    }

    private static bool TryMapKey(VirtualKey key, out char character)
    {
        character = '\0';
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
            character = (char)('A' + (int)key - (int)VirtualKey.A);
        else if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
            character = (char)('0' + (int)key - (int)VirtualKey.Number0);
        else if (key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9)
            character = (char)('0' + (int)key - (int)VirtualKey.NumberPad0);
        else if (key == VirtualKey.Subtract || (int)key == 189)
            character = '-';
        else
            return false;

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _idleFlushVersion++;
        _scanBuffer.Clear();
        Unregister();
        if (_messageWindow is not null)
        {
            _messageWindow.RawInputReceived -= OnRawInputReceived;
            _messageWindow.DevicesChanged -= ResolvePairedDevice;
            _messageWindow.Dispose();
            _messageWindow = null;
        }
    }
}
