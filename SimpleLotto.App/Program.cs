using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SimpleLotto.App.Services;

namespace SimpleLotto.App;

internal static class Program
{
    private const string PrimaryInstanceKey = "SimpleLotto.PrimaryInstance";
    private const uint CoWaitDefault = 0;
    private const uint Infinite = 0xFFFFFFFF;
    private static readonly object ActivationGate = new();
    private static App? _app;
    private static bool _activationPending;
    private static AppInstance? _primaryInstance;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        try
        {
            if (RedirectToPrimaryInstance())
                return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error(
                "Single-instance registration failed. SimpleLotto will exit before initializing application services.",
                ex);
            return 1;
        }

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            RegisterApp(new App());
        });

        GC.KeepAlive(_primaryInstance);
        return 0;
    }

    private static bool RedirectToPrimaryInstance()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var primary = AppInstance.FindOrRegisterForKey(PrimaryInstanceKey);
        if (primary.IsCurrent)
        {
            _primaryInstance = primary;
            primary.Activated += PrimaryInstance_Activated;
            AppLog.Info($"Registered primary SimpleLotto instance in process {Environment.ProcessId}.");
            return false;
        }

        AppLog.Info(
            $"SimpleLotto process {Environment.ProcessId} is redirecting launch activation to primary process {primary.ProcessId}.");
        _ = AllowSetForegroundWindow(primary.ProcessId);
        RedirectActivationToPrimary(activation, primary);
        return true;
    }

    private static void RedirectActivationToPrimary(
        AppActivationArguments activation,
        AppInstance primary)
    {
        var completed = CreateEvent(IntPtr.Zero, true, false, null);
        if (completed == IntPtr.Zero)
        {
            AppLog.Error(
                "Could not create the activation-redirection synchronization event.",
                new InvalidOperationException("CreateEvent returned a null handle."));
            return;
        }

        Exception? redirectError = null;
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await primary.RedirectActivationToAsync(activation);
                }
                catch (Exception ex)
                {
                    redirectError = ex;
                }
                finally
                {
                    SetEvent(completed);
                }
            });

            _ = CoWaitForMultipleObjects(
                CoWaitDefault,
                Infinite,
                1,
                new[] { completed },
                out _);
        }
        finally
        {
            CloseHandle(completed);
        }

        if (redirectError is null)
        {
            AppLog.Info($"Launch activation redirected to SimpleLotto process {primary.ProcessId}.");
        }
        else
        {
            AppLog.Error(
                $"Launch activation could not be redirected to SimpleLotto process {primary.ProcessId}. The secondary process will exit without initializing services.",
                redirectError);
        }
    }

    private static void PrimaryInstance_Activated(
        object? sender,
        AppActivationArguments args)
    {
        App? app;
        lock (ActivationGate)
        {
            app = _app;
            if (app is null)
            {
                _activationPending = true;
                return;
            }
        }

        app.HandleRedirectedActivation(args);
    }

    private static void RegisterApp(App app)
    {
        var activate = false;
        lock (ActivationGate)
        {
            _app = app;
            activate = _activationPending;
            _activationPending = false;
        }

        if (activate)
            app.HandleRedirectedActivation(null);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(
        IntPtr eventAttributes,
        bool manualReset,
        bool initialState,
        string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr eventHandle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint flags,
        uint timeoutMilliseconds,
        ulong handleCount,
        IntPtr[] handles,
        out uint index);
}
