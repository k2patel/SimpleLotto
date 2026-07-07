using Microsoft.UI.Xaml;
using SimpleLotto.App.Services;
using System;

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
        _rdisplay = new RdisplayService(_store);
        _rdisplayApiHost = new RdisplayApiHost(_rdisplay, _store);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
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
            System.Diagnostics.Debug.WriteLine($"Rdisplay API failed to start: {ex.Message}");
        }
    }
}
