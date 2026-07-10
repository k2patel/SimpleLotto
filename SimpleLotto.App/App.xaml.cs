using Microsoft.UI.Xaml;
using SimpleLotto.App.Services;
using System;
using System.Threading.Tasks;

namespace SimpleLotto.App;

public partial class App : Application
{
    private readonly LocalStore _store = new();
    private readonly RdisplayService _rdisplay;
    private readonly RdisplayApiHost _rdisplayApiHost;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        _rdisplay = new RdisplayService(_store);
        _rdisplayApiHost = new RdisplayApiHost(_rdisplay, _store);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Info("SimpleLotto launched.");
        _ = StartRdisplayApiAsync();
        _window = new MainWindow(_rdisplay, _store);
        _window.Activate();
    }

    private async System.Threading.Tasks.Task StartRdisplayApiAsync()
    {
        try
        {
            await _rdisplayApiHost.StartAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Rdisplay API failed to start.", ex);
            System.Diagnostics.Debug.WriteLine($"Rdisplay API failed to start: {ex.Message}");
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e) =>
        AppLog.Error("Unhandled UI exception.", e.Exception);

    private static void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            AppLog.Error("Unhandled app domain exception.", ex);
        else
            AppLog.Info($"Unhandled app domain exception object: {e.ExceptionObject}");
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
