using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;

namespace Asuka.App;

public partial class App : Application, IAsyncDisposable
{
    private readonly CancellationTokenSource _startupCancellation = new();
    private Window? _mainWindow;
    private SplashWindow? _splash;
    private AppEnvironment? _environment;
    private Task _startupReady = Task.CompletedTask;
    private Task _launchTask = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _launchStarted;
    private bool _shutdownRequested;
    private bool _restartInProgress;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public AppEnvironment Environment => _environment
        ?? throw new InvalidOperationException("The application environment has not been created yet.");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_shutdownRequested) return;
        if (_mainWindow is { } window)
        {
            window.Activate();
            return;
        }
        if (_launchStarted) return;

        _launchStarted = true;
        _launchTask = LaunchWorkspaceAsync(args);
    }

    private async Task LaunchWorkspaceAsync(LaunchActivatedEventArgs args)
    {
        var cancellationToken = _startupCancellation.Token;
        try
        {
            var flags = System.Environment.GetCommandLineArgs().Skip(1)
                .Concat(args.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            DemoLaunchOptions? savedDemo = null;
            var ignoredDemoSetting = false;
            try
            {
                savedDemo = await new DemoModeSettingsStore(AppStoragePaths.GetStartupSettingsPath()).LoadAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                ignoredDemoSetting = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var demo = DemoLaunchOptions.Parse(flags, System.Environment.GetEnvironmentVariable("ASUKA_DEMO"), savedDemo);
            _environment = new AppEnvironment(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), demo, savedDemo);
            _environment.AddLog("Lifecycle", "Launching", "Starting the Asuka workspace.");
            if (ignoredDemoSetting)
                _environment.AddLog("Settings", "Demo setting unavailable", "The saved demo mode could not be read. Launch arguments and defaults were used.");
            cancellationToken.ThrowIfCancellationRequested();

            var main = new MainWindow(_environment) { StartupCancellationToken = cancellationToken };
            _mainWindow = main;
            _startupReady = main.StartupReady;
            main.Closed += OnMainWindowClosed;
            if (main.Content is not Grid host)
                throw new InvalidOperationException("The main workspace has no startup host.");

            // The native title bar occupies row 0. Keep it interactive, including
            // its close button, while the workspace loads below it.
            var workspace = host.Children.OfType<FrameworkElement>().Where(element => Grid.GetRow(element) > 0)
                .Select(element => new WorkspaceElementState(element, element.Opacity,
                    element.IsHitTestVisible, element is Control control ? control.IsEnabled : null)).ToArray();
            foreach (var item in workspace)
            {
                item.Element.Opacity = 0;
                item.Element.IsHitTestVisible = false;
                if (item.Element is Control control) control.IsEnabled = false;
            }
            var splash = new SplashWindow();
            _splash = splash;
            splash.CloseRequested += (_, _) => main.Close();
            splash.MotionFailed += OnStartupMotionFailed;
            Grid.SetRow(splash, 1);
            Grid.SetRowSpan(splash, Math.Max(1, host.RowDefinitions.Count - 1));
            host.Children.Add(splash);
            main.Activate();

            // MainWindow owns initialization and first-page refresh. It completes
            // this task from its Loaded finally block, including recoverable errors.
            await _startupReady;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await StartupMotion.NextFrameAsync(cancellationToken);
                await splash.RevealWorkspaceAsync(
                    motion => RevealWorkspace(workspace, motion), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _environment?.ReportError("Startup transition failed; showing the workspace", exception);
            }
            finally
            {
                if (!_shutdownRequested && ReferenceEquals(_mainWindow, main))
                {
                    RevealWorkspace(workspace, null);
                    foreach (var item in workspace)
                    {
                        item.Element.IsHitTestVisible = item.IsHitTestVisible;
                        if (item.Element is Control control && item.IsEnabled is { } enabled)
                            control.IsEnabled = enabled;
                    }
                    host.Children.Remove(splash);
                }
                splash.MotionFailed -= OnStartupMotionFailed;
                splash.Dispose();
                if (ReferenceEquals(_splash, splash)) _splash = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _shutdownRequested)
        {
            // Closing the startup window is final: never create or activate another one.
        }
        catch (Exception exception)
        {
            if (_shutdownRequested) return;
            _environment?.ReportError("Startup failed", exception);
            ShowStartupFailure(exception);
        }
    }

    private static void RevealWorkspace(IEnumerable<WorkspaceElementState> elements, StartupMotion? motion)
    {
        foreach (var item in elements)
        {
            if (motion is null) item.Element.Opacity = item.Opacity;
            else motion.Fade(item.Element, 0, (float)item.Opacity, TimeSpan.FromMilliseconds(220));
        }
    }

    private void ShowStartupFailure(Exception exception)
    {
        if (_shutdownRequested) return;
        var window = _mainWindow;
        if (window is null)
        {
            window = new Window { Title = "Asuka" };
            WindowChrome.Configure(window, 540, 460);
            window.Closed += OnMainWindowClosed;
            _mainWindow = window;
        }

        var splash = _splash;
        if (splash is null)
        {
            splash = new SplashWindow();
            _splash = splash;
            splash.CloseRequested += (_, _) => window.Close();
            window.Content = splash;
        }
        splash.ShowFailure(exception.Message);
        window.Activate();
    }

    private void OnStartupMotionFailed(object? sender, Exception exception) =>
        _environment?.ReportError("Startup animation unavailable; using a still presentation", exception);

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        _mainWindow = null;
        try { await DisposeAsync(); }
        catch (Exception exception)
        {
            // A closed window cannot display recovery UI; don't raise another
            // unhandled UI exception during resource cleanup.
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) =>
        _environment?.ReportError("Unhandled UI error", args.Exception);

    internal async Task RestartForDemoModeAsync(DemoLaunchOptions options)
    {
        if (_shutdownRequested || _restartInProgress || _mainWindow is null)
            throw new InvalidOperationException("The app cannot restart while it is closing or already restarting.");
        _restartInProgress = true;
        try
        {
            var arguments = string.Join(' ', options.ToRestartArguments());
            // Settings and SQLite mutations are committed before this call.
            // Finish protocol shutdown, but keep the UI/environment usable if
            // Windows rejects the restart (for example, when not foreground).
            if (_environment is { } environment) await environment.DisconnectAsync();
            if (_shutdownRequested || _mainWindow is null) return;
            var failure = AppInstance.Restart(arguments);
            // A successful restart terminates this process and never returns.
            throw new InvalidOperationException($"Windows could not restart Asuka ({failure}). The demo mode setting is saved; close and reopen Asuka to apply it.");
        }
        finally { _restartInProgress = false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposeTask is null)
        {
            _shutdownRequested = true;
            _startupCancellation.Cancel();
            _disposeTask = DisposeCoreAsync();
        }
        await _disposeTask;
        GC.SuppressFinalize(this);
    }

    private async Task DisposeCoreAsync()
    {
        // Do not WaitAsync with the canceled token here. Initialization must finish
        // releasing its gates before AppEnvironment disposes those gates/storage.
        try { await _startupReady; }
        catch (OperationCanceledException) { }
        try { await _launchTask; }
        catch (OperationCanceledException) { }
        _splash?.Dispose();
        _splash = null;
        var environment = _environment;
        _environment = null;
        try
        {
            if (environment is not null) await environment.DisposeAsync();
        }
        finally
        {
            _startupCancellation.Dispose();
        }
    }

    private sealed record WorkspaceElementState(UIElement Element, double Opacity,
        bool IsHitTestVisible, bool? IsEnabled);
}
