using Microsoft.UI.Xaml;

namespace Matcha.App;

public partial class App : Application, IAsyncDisposable
{
    private const int MinimumSplashDurationMilliseconds = 1_000;
    private Window? _mainWindow;
    private SplashWindow? _splashWindow;
    private AppEnvironment? _environment;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public AppEnvironment Environment => _environment
        ?? throw new InvalidOperationException("The application environment has not been created yet.");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_mainWindow is not null)
        {
            _mainWindow.Activate();
            return;
        }

        _environment ??= new AppEnvironment(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        _environment.AddLog("Lifecycle", "Launching", "Starting the Matcha workspace.");
        var splash = new SplashWindow();
        _splashWindow = splash;
        splash.Closed += (_, _) =>
        {
            if (ReferenceEquals(_splashWindow, splash))
            {
                _splashWindow = null;
            }
        };
        splash.Activate();
        LaunchMainWindowAsync(_environment, splash);
    }

    private async void LaunchMainWindowAsync(AppEnvironment environment, SplashWindow splash)
    {
        try
        {
            var initialization = environment.InitializeAsync();
            await Task.WhenAll(initialization, Task.Delay(MinimumSplashDurationMilliseconds));
        }
        catch (Exception exception)
        {
            // Initialization is recoverable: the main window shows its existing retry/error UI.
            environment.ReportError("Startup initialization failed", exception);
        }

        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(environment);
            _mainWindow.Closed += OnMainWindowClosed;
            _mainWindow.Activate();
        }

        if (ReferenceEquals(_splashWindow, splash))
        {
            await splash.CloseWithAnimationAsync();
        }
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        await DisposeAsync();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _environment?.ReportError("Unhandled UI error", e.Exception);
    }

    public async ValueTask DisposeAsync()
    {
        if (_environment is not { } environment)
        {
            GC.SuppressFinalize(this);
            return;
        }

        _environment = null;
        await environment.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
