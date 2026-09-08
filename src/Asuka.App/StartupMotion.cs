using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Asuka.App;

/// <summary>
/// Owns the startup surface's explicit animations. All calls, including disposal, belong to the UI thread.
/// </summary>
internal sealed class StartupMotion : IDisposable
{
    private readonly Compositor _compositor = CompositionTarget.GetCompositorForCurrentThread();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<(UIElement Element, string Property), CompositionAnimation> _animations = [];
    private readonly CubicBezierEasingFunction _easeOut;
    private readonly CubicBezierEasingFunction _easeInOut;
    private bool _disposed;

    public StartupMotion()
    {
        _easeOut = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        _easeInOut = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));
    }

    public void Fade(UIElement element, float from, float to, TimeSpan duration, TimeSpan? delay = null)
    {
        ThrowIfUnavailable(element);
        ValidateTiming(duration, delay);
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.Target = nameof(UIElement.Opacity);
        Configure(animation, duration, delay);
        animation.InsertKeyFrame(0f, from);
        animation.InsertKeyFrame(1f, to, _easeOut);
        Start(element, animation, () => element.Opacity = to);
    }

    public void Move(UIElement element, Vector3 from, Vector3 to, TimeSpan duration, TimeSpan? delay = null)
    {
        ThrowIfUnavailable(element);
        ValidateTiming(duration, delay);
        var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.Target = nameof(UIElement.Translation);
        Configure(animation, duration, delay);
        animation.InsertKeyFrame(0f, from);
        animation.InsertKeyFrame(1f, to, _easeOut);
        Start(element, animation, () => element.Translation = to);
    }

    public void SpringScale(UIElement element, float from, float to)
    {
        ThrowIfUnavailable(element);
        var animation = _compositor.CreateSpringVector3Animation();
        animation.Target = nameof(UIElement.Scale);
        animation.InitialValue = new Vector3(from, from, 1f);
        animation.FinalValue = new Vector3(to, to, 1f);
        animation.DampingRatio = 0.75f;
        animation.Period = TimeSpan.FromMilliseconds(160);
        animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        Start(element, animation, () => element.Scale = new Vector3(to, to, 1f));
    }

    /// <summary>Start only after the finite entrance batch has completed.</summary>
    public void Breathe(UIElement element)
    {
        ThrowIfUnavailable(element);
        var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.Target = nameof(UIElement.Scale);
        Configure(animation, TimeSpan.FromSeconds(2), null);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        animation.InsertKeyFrame(0f, Vector3.One);
        animation.InsertKeyFrame(0.5f, new Vector3(1.025f, 1.025f, 1f), _easeInOut);
        animation.InsertKeyFrame(1f, Vector3.One, _easeInOut);
        Start(element, animation, () => element.Scale = Vector3.One);
    }

    public void StopAll()
    {
        foreach (var (key, animation) in _animations)
        {
            StopAndDispose(key.Element, animation);
        }

        _animations.Clear();
    }

    /// <summary>
    /// Waits for the compositor to finish the animations started synchronously by <paramref name="start"/>.
    /// Cancellation stops waiting; the caller owns the next visual state and calls <see cref="StopAll"/>.
    /// </summary>
    public async Task RunBatchAsync(Action start, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(start);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object sender, CompositionBatchCompletedEventArgs args) => completion.TrySetResult();

        batch.Completed += OnCompleted;
        try
        {
            try
            {
                start();
            }
            finally
            {
                // Even a failed start must stop collecting this thread's later animations.
                batch.End();
            }

            await completion.Task.WaitAsync(cancellation.Token);
        }
        finally
        {
            batch.Completed -= OnCompleted;
        }
    }

    public static async Task NextFrameAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendering(object? sender, object args) => completion.TrySetResult();

        CompositionTarget.Rendering += OnRendering;
        try
        {
            await completion.Task.WaitAsync(ct);
        }
        finally
        {
            // The captured UI context also handles cancellation raised by a worker thread.
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        StopAll();
        _easeOut.Dispose();
        _easeInOut.Dispose();
        _lifetime.Dispose();
    }

    private void ThrowIfUnavailable(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ValidateTiming(TimeSpan duration, TimeSpan? delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay.GetValueOrDefault(), TimeSpan.Zero, nameof(delay));
    }

    private static void Configure(KeyFrameAnimation animation, TimeSpan duration, TimeSpan? delay)
    {
        animation.Duration = duration;
        animation.DelayTime = delay.GetValueOrDefault();
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
    }

    private void Start(UIElement element, CompositionAnimation animation, Action setFinalValue)
    {
        var key = (element, animation.Target);
        try
        {
            if (_animations.Remove(key, out var previous))
            {
                StopAndDispose(element, previous);
            }

            // Keep the XAML value valid after stopping or disconnecting the explicit animation.
            setFinalValue();
            element.StartAnimation(animation);
            _animations.Add(key, animation);
        }
        catch
        {
            StopAndDispose(element, animation);
            throw;
        }
    }

    private static void StopAndDispose(UIElement element, CompositionAnimation animation)
    {
        try
        {
            // UIElement.StopAnimation takes the animation object, unlike Visual.StopAnimation.
            element.StopAnimation(animation);
        }
        catch (Exception exception) when (exception is COMException or ObjectDisposedException)
        {
            // The XAML target may already be closed during window teardown.
        }
        finally
        {
            animation.Dispose();
        }
    }
}
