using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Asuka.App;

public sealed partial class SettingsPage
{
    private static readonly TimeSpan AboutLogoCelebrationDuration = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan AboutLogoSpringPeriod = TimeSpan.FromMilliseconds(240);
    private const int AboutLogoClickTarget = 5;

    private CancellationTokenSource? _aboutLogoAnimationCancellation;
    private long _aboutVisibilityCallbackToken;
    private int _aboutLogoClickCount;
    private bool _aboutInitialized;
    private bool _aboutLogoPlaying;

    /// <summary>Installs the About card's interactions and immutable license list.</summary>
    public void InitializeAbout()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_aboutInitialized)
        {
            return;
        }

        ComponentLicensesList.ItemsSource = ComponentLicenses.All;
        AboutLogoButton.Click += AboutLogoButton_Click;
        _aboutVisibilityCallbackToken = RegisterPropertyChangedCallback(
            UIElement.VisibilityProperty,
            AboutVisibilityChanged);
        _aboutInitialized = true;
    }

    private void AboutLogoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_aboutInitialized || Visibility != Visibility.Visible || _aboutLogoPlaying)
        {
            return;
        }

        if (!FluentMotion.AreAnimationsEnabled)
        {
            _aboutLogoClickCount = 0;
            ResetAboutLogoVisual();
            return;
        }

        if (++_aboutLogoClickCount < AboutLogoClickTarget)
        {
            return;
        }

        _aboutLogoClickCount = 0;
        _ = PlayAboutLogoCelebrationAsync();
    }

    private void AboutVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (Visibility != Visibility.Visible)
        {
            ResetAboutInteraction();
        }
    }

    private async Task PlayAboutLogoCelebrationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _aboutLogoAnimationCancellation = cancellation;
        _aboutLogoPlaying = true;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(AboutLogoMotionHost);
            visual.StopAnimation(nameof(Visual.Scale));
            visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
            visual.CenterPoint = new Vector3(
                (float)Math.Max(0, AboutLogoMotionHost.ActualWidth > 0 ? AboutLogoMotionHost.ActualWidth / 2 : 36),
                (float)Math.Max(0, AboutLogoMotionHost.ActualHeight > 0 ? AboutLogoMotionHost.ActualHeight / 2 : 36),
                0);

            var compositor = visual.Compositor;
            using var spring = compositor.CreateSpringVector3Animation();
            spring.FinalValue = Vector3.One;
            spring.DampingRatio = 0.62f;
            spring.Period = AboutLogoSpringPeriod;
            visual.Scale = new Vector3(1.14f, 1.14f, 1f);
            visual.StartAnimation(nameof(Visual.Scale), spring);

            using var rotation = compositor.CreateScalarKeyFrameAnimation();
            using var linear = compositor.CreateLinearEasingFunction();
            rotation.Duration = AboutLogoCelebrationDuration;
            rotation.IterationBehavior = AnimationIterationBehavior.Count;
            rotation.IterationCount = 1;
            rotation.InsertKeyFrame(0f, 0f);
            rotation.InsertKeyFrame(1f, 360f, linear);
            visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), rotation);

            await Task.Delay(AboutLogoCelebrationDuration, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _environment?.ReportError("About logo animation failed", exception);
        }
        finally
        {
            if (ReferenceEquals(_aboutLogoAnimationCancellation, cancellation))
            {
                _aboutLogoAnimationCancellation = null;
                _aboutLogoPlaying = false;
                ResetAboutLogoVisual();
            }
        }
    }

    private void ResetAboutInteraction()
    {
        _aboutLogoClickCount = 0;
        _aboutLogoPlaying = false;
        var cancellation = _aboutLogoAnimationCancellation;
        _aboutLogoAnimationCancellation = null;
        cancellation?.Cancel();
        ResetAboutLogoVisual();
    }

    private void ResetAboutLogoVisual()
    {
        if (!_aboutInitialized)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(AboutLogoMotionHost);
        visual.StopAnimation(nameof(Visual.Scale));
        visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
        visual.Scale = Vector3.One;
        visual.RotationAngleInDegrees = 0f;
    }

    private void DisposeAbout()
    {
        if (!_aboutInitialized)
        {
            return;
        }

        ResetAboutInteraction();
        AboutLogoButton.Click -= AboutLogoButton_Click;
        UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _aboutVisibilityCallbackToken);
        _aboutInitialized = false;
    }
}
