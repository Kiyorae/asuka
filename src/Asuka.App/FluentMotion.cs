using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Asuka.App;

/// <summary>
/// Small, compositor-backed transitions for surfaces that are shown and hidden by the shell.
/// The helper only changes visual properties, so it never participates in layout.
/// </summary>
internal static class FluentMotion
{
    private static readonly ConditionalWeakTable<UIElement, PulseState> Pulses = new();

    public static async Task EnterAsync(
        UIElement element,
        Vector3 offset,
        TimeSpan duration,
        TimeSpan? delay = null,
        float startScale = 1f,
        bool resumeFromCurrentState = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!AreAnimationsEnabled)
        {
            ResetTransition(element);
            return;
        }

        if (!resumeFromCurrentState)
        {
            ClearTransitions(element);
            element.Opacity = 0;
            element.Translation = offset;
            element.Scale = new Vector3(startScale, startScale, 1f);
            await YieldToDispatcherAsync(element);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (delay.GetValueOrDefault() > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(delay.GetValueOrDefault(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        ConfigureTransitions(element, duration);
        element.Opacity = 1;
        element.Translation = Vector3.Zero;
        element.Scale = Vector3.One;
        await WaitForTransitionAsync(duration, cancellationToken);
    }

    public static async Task ExitAsync(
        UIElement element,
        Vector3 offset,
        TimeSpan duration,
        float endScale = 1f,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!AreAnimationsEnabled)
        {
            ClearTransitions(element);
            element.Opacity = 0;
            element.Translation = offset;
            element.Scale = new Vector3(endScale, endScale, 1f);
            return;
        }

        ConfigureTransitions(element, duration);
        element.Opacity = 0;
        element.Translation = offset;
        element.Scale = new Vector3(endScale, endScale, 1f);
        await WaitForTransitionAsync(duration, cancellationToken);
    }

    public static void Reset(UIElement element)
    {
        ResetTransition(element);
    }

    /// <summary>
    /// Restores the properties owned by the XAML transition APIs.
    /// Composition pulse state deliberately remains separate: accessing an element visual here
    /// would conflict with ElementCompositionPreview ownership while a pulse is active.
    /// </summary>
    public static void ResetTransition(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        ClearTransitions(element);
        element.Opacity = 1;
        element.Translation = Vector3.Zero;
        element.Scale = Vector3.One;
        element.Rotation = 0;
    }

    public static void Pulse(FrameworkElement element, float peakScale = 1.12f, float rotationDegrees = 0f)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (!AreAnimationsEnabled)
        {
            ResetPulseVisual(element);
            return;
        }

        peakScale = Math.Max(0.01f, peakScale);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3(
            (float)Math.Max(0, element.ActualWidth / 2),
            (float)Math.Max(0, element.ActualHeight / 2),
            0);
        var state = Pulses.GetValue(element, static _ => new PulseState());
        state.Cancellation?.Cancel();
        state.Cancellation?.Dispose();
        state.Cancellation = new CancellationTokenSource();

        visual.StopAnimation(nameof(Visual.Scale));
        visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
        visual.Scale = Vector3.One;
        visual.RotationAngleInDegrees = 0f;

        var compositor = visual.Compositor;
        var easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
        var easeIn = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = TimeSpan.FromMilliseconds(260);
        scale.InsertKeyFrame(0f, Vector3.One);
        scale.InsertKeyFrame(0.42f, new Vector3(peakScale, peakScale, 1f), easeOut);
        scale.InsertKeyFrame(1f, Vector3.One, easeIn);
        visual.StartAnimation(nameof(Visual.Scale), scale);

        if (rotationDegrees != 0f)
        {
            var rotation = compositor.CreateScalarKeyFrameAnimation();
            rotation.Duration = scale.Duration;
            rotation.InsertKeyFrame(0f, 0f);
            rotation.InsertKeyFrame(0.42f, rotationDegrees, easeOut);
            rotation.InsertKeyFrame(1f, 0f, easeIn);
            visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), rotation);
        }

        _ = FinishPulseAsync(element, scale.Duration, state.Cancellation.Token);
    }

    public static bool AreAnimationsEnabled
    {
        get
        {
            try
            {
                return new UISettings().AnimationsEnabled;
            }
            catch
            {
                // Prefer motion when the system setting cannot be queried (for example, in tests).
                return true;
            }
        }
    }

    private static void ConfigureTransitions(UIElement element, TimeSpan duration)
    {
        // Reuse implicit transitions so an interrupted show/hide retargets the current visual
        // instead of snapping to the old animation's destination before reversing.
        element.OpacityTransition ??= new ScalarTransition();
        element.TranslationTransition ??= new Vector3Transition();
        element.ScaleTransition ??= new Vector3Transition();
        element.OpacityTransition.Duration = duration;
        element.TranslationTransition.Duration = duration;
        element.ScaleTransition.Duration = duration;
    }

    private static void ClearTransitions(UIElement element)
    {
        element.OpacityTransition = null;
        element.TranslationTransition = null;
        element.ScaleTransition = null;
        element.RotationTransition = null;
    }

    private static Task<bool> YieldToDispatcherAsync(UIElement element)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!element.DispatcherQueue.TryEnqueue(() => completion.TrySetResult(true)))
        {
            completion.TrySetResult(true);
        }

        return completion.Task;
    }

    private static async Task WaitForTransitionAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller that cancelled this transition owns the next visual state.
        }
    }

    private static async Task FinishPulseAsync(
        UIElement element,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                ResetPulseVisual(element);
                if (Pulses.TryGetValue(element, out var state))
                {
                    state.Cancellation?.Dispose();
                    state.Cancellation = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A later pulse owns the visual and performs the final reset instead.
        }
    }

    private static void ResetPulseVisual(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation(nameof(Visual.Scale));
        visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
        visual.Scale = Vector3.One;
        visual.RotationAngleInDegrees = 0f;
    }

    private sealed class PulseState
    {
        public CancellationTokenSource? Cancellation { get; set; }
    }
}
