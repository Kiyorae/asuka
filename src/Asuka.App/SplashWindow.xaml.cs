using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace Asuka.App;

// A startup surface inside MainWindow avoids a second native window and a desktop flash.
internal sealed partial class SplashWindow : UserControl, IDisposable
{
    private readonly UISettings _uiSettings = new();
    private readonly CancellationTokenSource _entranceCancellation = new();
    private readonly TaskCompletionSource _loadedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StartupMotion? _motion;
    private CancellationTokenSource? _exitCancellation;
    private Task _entranceTask = Task.CompletedTask;
    private bool _loaded;
    private bool _completing;
    private bool _failed;
    private bool _disposed;

    internal SplashWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        CloseButton.Click += OnCloseClick;
        _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
    }

    internal event EventHandler? CloseRequested;
    internal event EventHandler<Exception>? MotionFailed;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded || _disposed) return;
        _loaded = true;
        _loadedCompletion.TrySetResult();
        LogoEntrance.CenterPoint = new Vector3(72, 72, 0);
        LogoBreath.CenterPoint = new Vector3(72, 72, 0);
        StartupProgress.IsIndeterminate = !_failed && _uiSettings.AnimationsEnabled;
        if (!_failed && _uiSettings.AnimationsEnabled) _entranceTask = EnterAsync();
        else ShowStaticArtwork();
    }

    private async Task EnterAsync()
    {
        try
        {
            _motion ??= new StartupMotion();
            await _motion.RunBatchAsync(() =>
            {
                _motion.Fade(LogoEntrance, 0, 1, TimeSpan.FromMilliseconds(240));
                _motion.Move(LogoEntrance, new Vector3(0, 12, 0), Vector3.Zero, TimeSpan.FromMilliseconds(300));
                _motion.SpringScale(LogoEntrance, 0.88f, 1);
                _motion.Fade(TitleGroup, 0, 1, TimeSpan.FromMilliseconds(220), TimeSpan.FromMilliseconds(55));
                _motion.Move(TitleGroup, new Vector3(0, 8, 0), Vector3.Zero,
                    TimeSpan.FromMilliseconds(280), TimeSpan.FromMilliseconds(55));
                _motion.Fade(StatusGroup, 0, 1, TimeSpan.FromMilliseconds(220), TimeSpan.FromMilliseconds(95));
            }, _entranceCancellation.Token);
            if (!_disposed && !_completing && _uiSettings.AnimationsEnabled) _motion.Breathe(LogoBreath);
        }
        catch (OperationCanceledException) when (_entranceCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_disposed) return;
            _motion?.StopAll();
            ShowStaticArtwork();
            MotionFailed?.Invoke(this, exception);
        }
    }

    internal async Task RevealWorkspaceAsync(Action<StartupMotion?> revealWorkspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revealWorkspace);
        cancellationToken.ThrowIfCancellationRequested();
        await _loadedCompletion.Task.WaitAsync(cancellationToken);
        if (_disposed) return;
        _completing = true;
        _entranceCancellation.Cancel();
        await _entranceTask;
        cancellationToken.ThrowIfCancellationRequested();
        _motion?.StopAll();
        StartupProgress.IsIndeterminate = false;
        StatusText.Text = "Opening your workspace";
        if (!_uiSettings.AnimationsEnabled)
        {
            revealWorkspace(null);
            Opacity = 0;
            return;
        }

        using var exit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _exitCancellation = exit;
        try
        {
            _motion ??= new StartupMotion();
            await _motion.RunBatchAsync(() =>
            {
                revealWorkspace(_motion);
                _motion.Fade(this, 1, 0, TimeSpan.FromMilliseconds(220));
                _motion.Move(StartupContent, Vector3.Zero, new Vector3(0, -8, 0), TimeSpan.FromMilliseconds(220));
            }, exit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Turning animations off while fading should reveal the workspace now.
        }
        finally
        {
            _exitCancellation = null;
            _motion?.StopAll();
            if (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                revealWorkspace(null);
                Opacity = 0;
            }
        }
    }

    internal void ShowFailure(string detail)
    {
        _failed = true;
        _entranceCancellation.Cancel();
        _motion?.StopAll();
        ShowStaticArtwork();
        TitleText.Text = "Asuka couldn't start";
        TitleText.FontSize = 24;
        StatusText.Text = detail;
        StartupProgress.IsIndeterminate = false;
        StartupProgress.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
    }

    private void OnAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_loaded) return;
            if (!sender.AnimationsEnabled)
            {
                _entranceCancellation.Cancel();
                _exitCancellation?.Cancel();
                _motion?.StopAll();
                ShowStaticArtwork();
            }
            else if (!_completing && !_failed)
            {
                _motion ??= new StartupMotion();
                _motion.Breathe(LogoBreath);
            }
            StartupProgress.IsIndeterminate = sender.AnimationsEnabled && !_completing && !_failed;
        });
    }

    private void ShowStaticArtwork()
    {
        LogoEntrance.Opacity = 1;
        LogoEntrance.Scale = Vector3.One;
        LogoEntrance.Translation = Vector3.Zero;
        LogoBreath.Scale = Vector3.One;
        TitleGroup.Opacity = 1;
        TitleGroup.Translation = Vector3.Zero;
        StatusGroup.Opacity = 1;
        StartupProgress.IsIndeterminate = false;
    }

    private void OnCloseClick(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        CloseButton.Click -= OnCloseClick;
        _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        _entranceCancellation.Cancel();
        _exitCancellation?.Cancel();
        _loadedCompletion.TrySetCanceled();
        _motion?.Dispose();
        _motion = null;
        _entranceCancellation.Dispose();
    }
}
