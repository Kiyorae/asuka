using System.Runtime.InteropServices;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Asuka.App;

public sealed partial class MainWindow
{
    private StackPanel CreateAudioPreview(Asset asset) => new InlineAudioPreview(this, asset).View;

    /// <summary>Owns a lazy audio player without a video surface or video-only controls.</summary>
    private sealed class InlineAudioPreview : IDisposable
    {
        private readonly MainWindow _owner;
        private readonly Asset _asset;
        private readonly Button _play = new() { Content = "▶ Play audio" };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly InlineMediaPlaybackControls _controls;
        private CancellationTokenSource? _lifetime;
        private MediaPlayer? _player;
        private MediaSource? _source;
        private IRandomAccessStream? _mediaStream;
        private PlayableAudioSource? _playbackSource;
        private bool _active;
        private bool _busy;
        private bool _disposed;
        private bool _inVisualTree;
        private bool _watchingPageVisibility;
        private bool _watchingWindowClosed;
        private long _pageVisibilityToken;
        private int _loadVersion;

        internal InlineAudioPreview(MainWindow owner, Asset asset)
        {
            _owner = owner;
            _asset = asset;
            View = new StackPanel { Spacing = 6, Width = 360, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
            _controls = new InlineMediaPlaybackControls($"audio {asset.Name}", ControlsFailed);
            View.Children.Add(_play);
            View.Children.Add(_controls.View);
            View.Children.Add(_status);
            _status.Text = asset.Name;
            AutomationProperties.SetName(_play, $"Play audio {asset.Name}");
            AutomationProperties.SetName(_status, $"Audio status for {asset.Name}");
            View.Loaded += Loaded;
            View.Unloaded += Unloaded;
            _play.Click += PlayClicked;
            UpdateButtons();
        }

        internal StackPanel View { get; }

        private void Loaded(object sender, RoutedEventArgs args)
        {
            if (_disposed || _owner._closed) return;
            _inVisualTree = true;
            if (!_watchingPageVisibility)
            {
                _pageVisibilityToken = _owner.MessagesPage.RegisterPropertyChangedCallback(
                    UIElement.VisibilityProperty, PageVisibilityChanged);
                _watchingPageVisibility = true;
            }
            if (!_watchingWindowClosed) { _owner.Closed += WindowClosed; _watchingWindowClosed = true; }
            Activate();
        }

        private void Unloaded(object sender, RoutedEventArgs args)
        {
            _inVisualTree = false;
            StopWatchingPageVisibility();
            StopWatchingWindowClosed();
            Deactivate();
        }

        private void PageVisibilityChanged(DependencyObject sender, DependencyProperty property)
        {
            if (!_inVisualTree || _disposed) return;
            if (_owner.MessagesPage.Visibility == Visibility.Visible) Activate();
            else Deactivate();
        }

        private void WindowClosed(object sender, WindowEventArgs args) => Dispose();

        private void Activate()
        {
            if (_active || !_inVisualTree || _disposed || _owner._closed
                || _owner.MessagesPage.Visibility != Visibility.Visible) return;
            _active = true;
            _lifetime = new CancellationTokenSource();
            ++_loadVersion;
            UpdateButtons();
        }

        private bool IsCurrent(int version) => _active && _inVisualTree && !_disposed && !_owner._closed
            && _owner.MessagesPage.Visibility == Visibility.Visible && version == _loadVersion;

        private void EnsureCurrent(int version, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(version)) throw new OperationCanceledException("The audio preview is no longer visible.");
        }

        private async void PlayClicked(object sender, RoutedEventArgs args) => await PlayAsync();

        private async Task PlayAsync()
        {
            if (!_active || _busy || _disposed || _lifetime is null) return;
            var version = _loadVersion;
            var token = _lifetime.Token;
            _busy = true;
            ReleasePlayer();
            _status.Text = "Preparing audio…";
            UpdateButtons();
            IRandomAccessStream? openingStream = null;
            PlayableAudioSource? openingSource = null;
            try
            {
                // Hold the opened file through playback so cache cleanup cannot remove it.
                openingSource = await _owner._environment.Media.OpenPlayableAudioAsync(_asset, token);
                EnsureCurrent(version, token);
                openingStream = openingSource.Stream.AsRandomAccessStream();
                var contentType = Path.GetExtension(openingSource.Path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                    ? "audio/wav" : _asset.MimeType;
                if (string.IsNullOrWhiteSpace(contentType)) contentType = AssetStore.MimeTypeForFileName(_asset.Name);
                if (string.IsNullOrWhiteSpace(contentType)) contentType = "application/octet-stream";
                // Original cache files have hash-only names, so pass their MIME explicitly.
                _source = MediaSource.CreateFromStream(openingStream, contentType);
                _mediaStream = openingStream;
                _playbackSource = openingSource;
                openingStream = null;
                openingSource = null;
                var player = new MediaPlayer { AutoPlay = false };
                _player = player;
                player.MediaOpened += MediaOpened;
                player.MediaFailed += MediaFailed;
                _controls.Attach(player);
                _play.Visibility = Visibility.Collapsed;
                _status.Text = "Opening audio…";
                player.Source = _source;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || !IsCurrent(version)) { }
            catch (Exception error)
            {
                if (IsCurrent(version)) Fail(error.Message, error);
            }
            finally
            {
                openingStream?.Dispose();
                openingSource?.Dispose();
                if (IsCurrent(version)) { _busy = false; UpdateButtons(); }
            }
        }

        private void MediaOpened(MediaPlayer sender, object args) => View.DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrent(_loadVersion) || !ReferenceEquals(_player, sender)) return;
            try { _status.Text = _asset.Name; sender.Play(); }
            catch (Exception error) { if (ReferenceEquals(_player, sender)) Fail(error.Message, error); }
        });

        private void MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            var message = string.IsNullOrWhiteSpace(args.ErrorMessage) ? args.Error.ToString() : args.ErrorMessage;
            _ = View.DispatcherQueue.TryEnqueue(() =>
            {
                if (IsCurrent(_loadVersion) && ReferenceEquals(_player, sender)) Fail(message);
            });
        }

        private void ControlsFailed(Exception error)
        {
            if (IsCurrent(_loadVersion)) Fail(error.Message, error);
        }

        private void Fail(string message, Exception? error = null)
        {
            ReleasePlayer();
            _status.Text = $"Audio could not be played: {message}";
            _play.Content = "↻ Retry audio";
            _play.Visibility = Visibility.Visible;
            UpdateButtons();
            if (error is not null) _owner._environment.ReportError("Audio playback failed", error);
        }

        private void UpdateButtons() => _play.IsEnabled = IsCurrent(_loadVersion) && !_busy && _player is null;

        private void ReleasePlayer()
        {
            _controls.Detach();
            var player = _player;
            var source = _source;
            var stream = _mediaStream;
            var playbackSource = _playbackSource;
            _player = null;
            _source = null;
            _mediaStream = null;
            _playbackSource = null;
            if (player is not null)
            {
                try { player.MediaOpened -= MediaOpened; player.MediaFailed -= MediaFailed; }
                catch (COMException) { }
                catch (ObjectDisposedException) { }
            }
            try { player?.Dispose(); }
            catch (COMException) { }
            catch (ObjectDisposedException) { }
            try { source?.Dispose(); }
            catch (COMException) { }
            catch (ObjectDisposedException) { }
            try { stream?.Dispose(); }
            catch (COMException) { }
            catch (ObjectDisposedException) { }
            playbackSource?.Dispose();
        }

        private void Deactivate()
        {
            _active = false;
            ++_loadVersion;
            var lifetime = _lifetime;
            _lifetime = null;
            lifetime?.Cancel();
            lifetime?.Dispose();
            ReleasePlayer();
            _busy = false;
            _play.Content = "▶ Play audio";
            _play.Visibility = Visibility.Visible;
            _status.Text = _asset.Name;
            UpdateButtons();
        }

        private void StopWatchingPageVisibility()
        {
            if (!_watchingPageVisibility) return;
            _owner.MessagesPage.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _pageVisibilityToken);
            _watchingPageVisibility = false;
        }

        private void StopWatchingWindowClosed()
        {
            if (!_watchingWindowClosed) return;
            _owner.Closed -= WindowClosed;
            _watchingWindowClosed = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inVisualTree = false;
            StopWatchingPageVisibility();
            StopWatchingWindowClosed();
            Deactivate();
            _controls.Dispose();
            View.Loaded -= Loaded;
            View.Unloaded -= Unloaded;
            _play.Click -= PlayClicked;
            GC.SuppressFinalize(this);
        }
    }
}
