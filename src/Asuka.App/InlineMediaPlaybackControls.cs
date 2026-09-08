using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Playback;

namespace Asuka.App;

/// <summary>Always-visible native controls owned by one inline audio/video preview.</summary>
internal sealed class InlineMediaPlaybackControls : IDisposable
{
    private readonly string _name;
    private readonly Action<Exception> _failed;
    private readonly Button _playPause = new() { Width = 34, Height = 34, Padding = new Thickness(4), Visibility = Visibility.Collapsed };
    private readonly Button _mute = new() { Width = 34, Height = 34, Padding = new Thickness(4), Visibility = Visibility.Collapsed };
    private readonly SymbolIcon _playPauseIcon = new(Symbol.Play);
    private readonly SymbolIcon _muteIcon = new(Symbol.Volume);
    private readonly Slider _position = new()
    {
        Minimum = 0,
        Maximum = 1,
        Value = 0,
        StepFrequency = 0.1,
        MinWidth = 96,
        Height = 34,
        IsEnabled = false,
        IsThumbToolTipEnabled = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _time = new()
    {
        Text = "0:00 / --:--",
        FontSize = 12,
        MinWidth = 74,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly PointerEventHandler _pressedHandler;
    private readonly PointerEventHandler _finishedHandler;
    private readonly PointerEventHandler _cancelledHandler;
    private MediaPlayer? _player;
    private MediaPlaybackSession? _session;
    private bool _loaded;
    private bool _opened;
    private bool _ended;
    private bool _updating;
    private bool _scrubbing;
    private bool _disposed;
    private TimeSpan? _pendingSeek;
    private long _pendingSeekUntil;
    private int _generation;

    internal InlineMediaPlaybackControls(string name, Action<Exception> failed)
    {
        _name = name;
        _failed = failed;
        View = new Grid { ColumnSpacing = 6, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_position, 1);
        Grid.SetColumn(_time, 2);
        Grid.SetColumn(_mute, 3);
        View.Children.Add(_playPause);
        View.Children.Add(_position);
        View.Children.Add(_time);
        View.Children.Add(_mute);
        AutomationProperties.SetName(_position, $"Playback position for {name}");
        AutomationProperties.SetHelpText(_position, "Seeking becomes available when the media has opened and its duration is known.");
        AutomationProperties.SetName(_time, $"Elapsed and total time for {name}");
        _playPause.Content = _playPauseIcon;
        _mute.Content = _muteIcon;
        _playPause.Click += PlayPauseClicked;
        _mute.Click += MuteClicked;
        _position.ValueChanged += PositionChanged;
        // Slider/Thumb handle their own pointer events. Observe handled events too
        // so the polling timer never pulls the thumb away during a drag.
        _pressedHandler = PointerPressed;
        _finishedHandler = PointerFinished;
        _cancelledHandler = PointerCancelled;
        _position.AddHandler(UIElement.PointerPressedEvent, _pressedHandler, true);
        _position.AddHandler(UIElement.PointerReleasedEvent, _finishedHandler, true);
        _position.AddHandler(UIElement.PointerCaptureLostEvent, _finishedHandler, true);
        _position.AddHandler(UIElement.PointerCanceledEvent, _cancelledHandler, true);
        View.Loaded += Loaded;
        View.Unloaded += Unloaded;
        _timer.Tick += TimerTick;
    }

    internal Grid View { get; }
    internal bool HasEnded => _ended;

    internal void Attach(MediaPlayer player, bool playbackEnded = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Detach();
        _player = player;
        _session = player.PlaybackSession;
        _opened = _session.PlaybackState is not (MediaPlaybackState.None or MediaPlaybackState.Opening);
        _ended = playbackEnded;
        player.MediaOpened += MediaOpened;
        player.MediaEnded += MediaEnded;
        _session.PlaybackStateChanged += SessionChanged;
        _session.NaturalDurationChanged += SessionChanged;
        _session.SeekableRangesChanged += SessionChanged;
        _session.SeekCompleted += SeekCompleted;
        _playPause.Visibility = _mute.Visibility = Visibility.Visible;
        Refresh();
        if (_loaded && !_disposed && ReferenceEquals(_player, player)) _timer.Start();
    }

    internal void Detach()
    {
        _timer.Stop();
        ++_generation;
        var player = _player;
        var session = _session;
        _player = null;
        _session = null;
        _opened = _ended = _scrubbing = false;
        _pendingSeek = null;
        try
        {
            if (player is not null) { player.MediaOpened -= MediaOpened; player.MediaEnded -= MediaEnded; }
            if (session is not null)
            {
                session.PlaybackStateChanged -= SessionChanged;
                session.NaturalDurationChanged -= SessionChanged;
                session.SeekableRangesChanged -= SessionChanged;
                session.SeekCompleted -= SeekCompleted;
            }
        }
        catch (COMException) { }
        catch (ObjectDisposedException) { }
        _playPause.Visibility = _mute.Visibility = Visibility.Collapsed;
        _playPause.IsEnabled = _mute.IsEnabled = false;
        ResetPosition();
    }

    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (_disposed) return;
        _loaded = true;
        if (_player is not null)
        {
            Refresh();
            if (_player is not null && !_disposed) _timer.Start();
        }
    }

    private void Unloaded(object sender, RoutedEventArgs args)
    {
        _loaded = false;
        Detach();
    }

    private void TimerTick(object? sender, object args) => Refresh();

    private void MediaOpened(MediaPlayer sender, object args) => QueueRefresh(sender, opened: true);
    private void MediaEnded(MediaPlayer sender, object args) => QueueRefresh(sender, ended: true);
    private void SessionChanged(MediaPlaybackSession sender, object args)
    {
        if (ReferenceEquals(sender, _session) && _player is { } player) QueueRefresh(player);
    }

    private void SeekCompleted(MediaPlaybackSession sender, object args)
    {
        if (ReferenceEquals(sender, _session) && _player is { } player) QueueRefresh(player, seekCompleted: true);
    }

    private void QueueRefresh(MediaPlayer expected, bool opened = false, bool ended = false, bool seekCompleted = false)
    {
        var generation = _generation;
        _ = View.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || generation != _generation || !ReferenceEquals(expected, _player)) return;
            if (opened) _opened = true;
            if (seekCompleted) _ended = false;
            if (ended) _ended = true;
            if (ended || seekCompleted) _pendingSeek = null;
            Refresh();
        });
    }

    private bool CanSeek(out TimeSpan duration)
    {
        duration = _session is not null && _opened ? _session.NaturalDuration : TimeSpan.Zero;
        return _session is not null && _opened && _session.CanSeek && duration > TimeSpan.Zero
            && duration != TimeSpan.MaxValue && _session.PlaybackState is not (MediaPlaybackState.None or MediaPlaybackState.Opening);
    }

    private void Refresh()
    {
        if (_disposed || _player is null || _session is null) return;
        try
        {
            var state = _session.PlaybackState;
            var ready = _opened && state is not (MediaPlaybackState.None or MediaPlaybackState.Opening);
            var seekable = CanSeek(out var duration);
            var knownDuration = duration > TimeSpan.Zero && duration != TimeSpan.MaxValue;
            var playing = state is MediaPlaybackState.Playing or MediaPlaybackState.Buffering;
            var nativePosition = _session.Position;
            if (_ended && state == MediaPlaybackState.Playing && knownDuration
                && (duration - nativePosition).TotalMilliseconds > Math.Min(100, duration.TotalMilliseconds / 10)) _ended = false;
            _playPause.IsEnabled = _mute.IsEnabled = ready;
            _playPauseIcon.Symbol = playing && !_ended ? Symbol.Pause : Symbol.Play;
            var playbackAction = playing && !_ended ? "Pause" : _ended ? "Replay" : "Play";
            AutomationProperties.SetName(_playPause, $"{playbackAction} {_name}");
            ToolTipService.SetToolTip(_playPause, playbackAction);
            _muteIcon.Symbol = _player.IsMuted ? Symbol.Mute : Symbol.Volume;
            AutomationProperties.SetName(_mute, $"{(_player.IsMuted ? "Unmute" : "Mute")} {_name}");
            ToolTipService.SetToolTip(_mute, _player.IsMuted ? "Unmute" : "Mute");
            _position.IsEnabled = seekable;
            _updating = true;
            try
            {
                _position.Maximum = knownDuration ? duration.TotalSeconds : 1;
                if (knownDuration)
                {
                    _position.StepFrequency = Math.Max(0.001, Math.Min(0.1, duration.TotalSeconds / 1000));
                    _position.SmallChange = Math.Max(0.1, Math.Min(5, duration.TotalSeconds / 100));
                    _position.LargeChange = Math.Max(_position.SmallChange, Math.Min(10, duration.TotalSeconds / 10));
                }
                if (!_scrubbing)
                {
                    if (_pendingSeek is { } target && (Environment.TickCount64 >= _pendingSeekUntil
                        || Math.Abs((nativePosition - target).TotalMilliseconds) < 50)) _pendingSeek = null;
                    // Native seeking completes asynchronously. Keep the requested
                    // position visible until completion instead of snapping backward.
                    // The bounded fallback also handles a no-op seek with no event.
                    var position = _ended && knownDuration ? duration : _pendingSeek ?? nativePosition;
                    _position.Value = knownDuration ? Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds) : 0;
                }
            }
            finally { _updating = false; }
            UpdateTime(knownDuration ? duration : null, knownDuration ? null : nativePosition);
        }
        catch (Exception error) when (error is COMException or ObjectDisposedException or InvalidOperationException)
        {
            _timer.Stop();
            _failed(error);
        }
    }

    private void UpdateTime(TimeSpan? duration, TimeSpan? elapsed = null)
    {
        var precise = duration is { TotalSeconds: < 1 };
        var position = elapsed ?? TimeSpan.FromSeconds(Math.Max(0, _position.Value));
        _time.Text = $"{FormatTime(position < TimeSpan.Zero ? TimeSpan.Zero : position, precise)} / {(duration is { } total ? FormatTime(total, precise) : "--:--")}";
    }

    private static string FormatTime(TimeSpan time, bool precise)
    {
        if (precise) return $"0:{time.Seconds:00}.{time.Milliseconds / 100}";
        return time.TotalHours >= 1
            ? $"{((long)time.TotalHours).ToString(CultureInfo.InvariantCulture)}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{((long)time.TotalMinutes).ToString(CultureInfo.InvariantCulture)}:{time.Seconds:00}";
    }

    private void PlayPauseClicked(object sender, RoutedEventArgs args)
    {
        if (_player is null || _session is null || !_opened) return;
        try
        {
            if ((_session.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Buffering) && !_ended) _player.Pause();
            else
            {
                if (_ended && CanSeek(out _))
                {
                    _pendingSeek = TimeSpan.Zero;
                    _pendingSeekUntil = Environment.TickCount64 + 2000;
                    _session.Position = TimeSpan.Zero;
                }
                _ended = false;
                _player.Play();
            }
            Refresh();
        }
        catch (Exception error) when (error is COMException or ObjectDisposedException or InvalidOperationException) { _failed(error); }
    }

    private void MuteClicked(object sender, RoutedEventArgs args)
    {
        if (_player is null) return;
        try { _player.IsMuted = !_player.IsMuted; Refresh(); }
        catch (Exception error) when (error is COMException or ObjectDisposedException) { _failed(error); }
    }

    private void PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_position.IsEnabled) _scrubbing = true;
    }

    private void PointerFinished(object sender, PointerRoutedEventArgs args)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        SeekTo(_position.Value);
    }

    private void PointerCancelled(object sender, PointerRoutedEventArgs args)
    {
        _scrubbing = false;
        Refresh();
    }

    private void PositionChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_updating || !_position.IsEnabled) return;
        if (_scrubbing)
        {
            try { UpdateTime(CanSeek(out var duration) ? duration : null); }
            catch (Exception error) when (error is COMException or ObjectDisposedException) { _failed(error); }
        }
        else SeekTo(args.NewValue); // Track clicks and keyboard arrows/Home/End.
    }

    private void SeekTo(double seconds)
    {
        try
        {
            if (!CanSeek(out var duration)) return;
            _ended = false;
            _pendingSeek = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, duration.TotalSeconds));
            _pendingSeekUntil = Environment.TickCount64 + 2000;
            _session!.Position = _pendingSeek.Value;
            Refresh();
        }
        catch (Exception error) when (error is COMException or ObjectDisposedException or InvalidOperationException or ArgumentException) { _failed(error); }
    }

    private void ResetPosition()
    {
        _updating = true;
        try { _position.IsEnabled = false; _position.Maximum = 1; _position.Value = 0; _time.Text = "0:00 / --:--"; }
        finally { _updating = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        _timer.Tick -= TimerTick;
        View.Loaded -= Loaded;
        View.Unloaded -= Unloaded;
        _playPause.Click -= PlayPauseClicked;
        _mute.Click -= MuteClicked;
        _position.ValueChanged -= PositionChanged;
        _position.RemoveHandler(UIElement.PointerPressedEvent, _pressedHandler);
        _position.RemoveHandler(UIElement.PointerReleasedEvent, _finishedHandler);
        _position.RemoveHandler(UIElement.PointerCaptureLostEvent, _finishedHandler);
        _position.RemoveHandler(UIElement.PointerCanceledEvent, _cancelledHandler);
        GC.SuppressFinalize(this);
    }
}
