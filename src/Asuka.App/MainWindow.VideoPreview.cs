using System.Runtime.InteropServices;
using Asuka.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace Asuka.App;

public sealed partial class MainWindow
{
    internal UIElement CreateVideoPreview(VideoSegment video) => new InlineVideoPreview(this, video).View;

    /// <summary>Owns one loaded message row's optional player; unloaded rows never retain a decoder.</summary>
    private sealed class InlineVideoPreview : IDisposable
    {
        private readonly MainWindow _owner;
        private readonly VideoSegment _video;
        private readonly Grid _viewport = new() { Height = 202.5, MinWidth = 260 };
        private readonly Image _poster = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        private readonly SymbolIcon _placeholder = new(Symbol.Video) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _play = new() { Content = "▶ Play video" };
        private readonly Button _open = new() { Content = "Open externally" };
        private readonly Button _save = new() { Content = "Save as" };
        private readonly ToggleButton _fitFill = new() { Content = "Fit", Visibility = Visibility.Collapsed };
        private readonly Button _expand = new()
        {
            Content = new SymbolIcon(Symbol.FullScreen),
            Width = 34,
            Height = 34,
            Padding = new Thickness(4),
            Visibility = Visibility.Collapsed,
        };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly InlineMediaPlaybackControls _controls;
        private readonly KeyEventHandler _fullWindowKeyHandler;
        private readonly PointerEventHandler _fullWindowWheelHandler;
        private CancellationTokenSource? _lifetime;
        private MediaPlayer? _player;
        private MediaSource? _source;
        private IRandomAccessStream? _mediaStream;
        private MediaPlayerElement? _element;
        private Grid? _expandedView;
        private Grid? _expandedViewport;
        private Button? _exitExpanded;
        private InlineMediaPlaybackControls? _expandedControls;
        private bool _active;
        private bool _busy;
        private bool _disposed;
        private bool _inVisualTree;
        private bool _watchingPageVisibility;
        private bool _watchingWindowClosed;
        private long _pageVisibilityToken;
        private int _loadVersion;
        private int _operationVersion;

        internal InlineVideoPreview(MainWindow owner, VideoSegment video)
        {
            _owner = owner;
            _video = video;
            View = new StackPanel { Spacing = 6, Width = 360, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
            _controls = new InlineMediaPlaybackControls($"video {video.Asset.Name}", ControlsFailed);
            _fullWindowKeyHandler = FullWindowKeyDown;
            _fullWindowWheelHandler = FullWindowPointerWheelChanged;
            _viewport.Children.Add(_placeholder);
            _viewport.Children.Add(_poster);
            View.Children.Add(new Border
            {
                Child = _viewport,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 26, 30)),
            });
            View.Children.Add(_controls.View);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            actions.Children.Add(_play);
            actions.Children.Add(_open);
            actions.Children.Add(_save);
            actions.Children.Add(_fitFill);
            actions.Children.Add(_expand);
            View.Children.Add(actions);
            View.Children.Add(_status);
            _status.Text = video.Asset.Name;
            AutomationProperties.SetName(_poster, $"Video thumbnail for {video.Asset.Name}");
            AutomationProperties.SetName(_play, $"Play video {video.Asset.Name}");
            AutomationProperties.SetName(_status, $"Video status for {video.Asset.Name}");
            AutomationProperties.SetName(_expand, $"Play video {video.Asset.Name} in full window");
            ToolTipService.SetToolTip(_expand, "Full window");
            View.Loaded += Loaded;
            View.Unloaded += Unloaded;
            _viewport.SizeChanged += ViewportSizeChanged;
            _fitFill.Click += FitFillClicked;
            _expand.Click += ExpandClicked;
            _play.Click += async (_, _) => await PlayAsync();
            _open.Click += async (_, _) => await FileActionAsync(save: false);
            _save.Click += async (_, _) => await FileActionAsync(save: true);
            UpdateButtons();
        }

        internal StackPanel View { get; }

        private async void Loaded(object sender, RoutedEventArgs args)
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
            await ActivateAsync();
        }

        private async void PageVisibilityChanged(DependencyObject sender, DependencyProperty property)
        {
            if (!_inVisualTree || _disposed) return;
            if (_owner.MessagesPage.Visibility == Visibility.Visible) await ActivateAsync();
            else Deactivate();
        }

        private async Task ActivateAsync()
        {
            if (_active || !_inVisualTree || _disposed || _owner._closed
                || _owner.MessagesPage.Visibility != Visibility.Visible) return;
            _active = true;
            _lifetime = new CancellationTokenSource();
            var version = ++_loadVersion;
            var token = _lifetime.Token;
            UpdateButtons();
            if (_video.Thumbnail is not { } thumbnail) return;
            try
            {
                var reference = await _owner._environment.Media.GetReferenceAsync(thumbnail, true, token);
                EnsureCurrent(version, token);
                var image = new BitmapImage();
                image.ImageFailed += (_, _) =>
                {
                    if (!IsCurrent(version) || !ReferenceEquals(_poster.Source, image)) return;
                    _poster.Source = null;
                    _poster.Visibility = Visibility.Collapsed;
                    _placeholder.Visibility = _player is null ? Visibility.Visible : Visibility.Collapsed;
                };
                _poster.Source = image;
                image.UriSource = new Uri(reference.Identifier);
                _poster.Visibility = _player is null ? Visibility.Visible : Visibility.Collapsed;
                _placeholder.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                // An optional poster failing must not prevent the user from trying the actual video.
                if (IsCurrent(version)) { _poster.Visibility = Visibility.Collapsed; _placeholder.Visibility = _player is null ? Visibility.Visible : Visibility.Collapsed; }
            }
        }

        private void Unloaded(object sender, RoutedEventArgs args)
        {
            _inVisualTree = false;
            StopWatchingPageVisibility();
            StopWatchingWindowClosed();
            Deactivate();
        }
        private void WindowClosed(object sender, WindowEventArgs args) => Dispose();
        private bool IsCurrent(int version) => _active && _inVisualTree && !_disposed && !_owner._closed
            && _owner.MessagesPage.Visibility == Visibility.Visible && _loadVersion == version;

        private void EnsureCurrent(int version, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(version)) throw new OperationCanceledException("The video preview is no longer visible.");
        }

        private async Task PlayAsync()
        {
            if (!_active || _busy || _disposed || _lifetime is null) return;
            var version = _loadVersion;
            var operation = ++_operationVersion;
            var token = _lifetime.Token;
            _busy = true;
            ReleasePlayer();
            _status.Text = "Loading video…";
            UpdateButtons();
            IRandomAccessStream? openingStream = null;
            try
            {
                var reference = await _owner._environment.Media.GetReferenceAsync(_video.Asset, true, token);
                EnsureCurrent(version, token);
                var file = await StorageFile.GetFileFromPathAsync(reference.Identifier);
                EnsureCurrent(version, token);
                openingStream = await file.OpenAsync(FileAccessMode.Read);
                EnsureCurrent(version, token);
                if (operation != _operationVersion) return;
                var contentType = reference.ResolvedAsset?.MimeType ?? _video.Asset.MimeType;
                if (string.IsNullOrWhiteSpace(contentType)) contentType = file.ContentType;
                if (string.IsNullOrWhiteSpace(contentType)) contentType = "application/octet-stream";
                // Cache files are named by hash. Passing the declared MIME preserves their original format.
                _source = MediaSource.CreateFromStream(openingStream, contentType);
                _mediaStream = openingStream;
                openingStream = null;
                var player = new MediaPlayer { AutoPlay = false };
                _player = player;
                player.MediaOpened += MediaOpened;
                player.MediaFailed += MediaFailed;
                player.PlaybackSession.NaturalVideoSizeChanged += NaturalVideoSizeChanged;
                _element = new MediaPlayerElement
                {
                    AreTransportControlsEnabled = false,
                    Stretch = Stretch.Uniform,
                };
                AutomationProperties.SetName(_element, $"Video player for {_video.Asset.Name}");
                _element.SetMediaPlayer(player);
                _viewport.Children.Add(_element);
                _controls.Attach(player);
                _poster.Visibility = _placeholder.Visibility = Visibility.Collapsed;
                _play.Visibility = Visibility.Collapsed;
                _status.Text = "Opening video…";
                // Setting the source opens it without playing. MediaOpened verifies a video track first.
                player.Source = _source;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || !IsCurrent(version)) { }
            catch (Exception error)
            {
                if (IsCurrent(version) && operation == _operationVersion) Fail(error.Message, error);
            }
            finally
            {
                openingStream?.Dispose();
                if (IsCurrent(version) && operation == _operationVersion) { _busy = false; UpdateButtons(); }
            }
        }

        private void MediaOpened(MediaPlayer sender, object args) => View.DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrent(_loadVersion) || !ReferenceEquals(_player, sender)) return;
            try
            {
                if (sender.PlaybackSession.NaturalVideoWidth == 0 || sender.PlaybackSession.NaturalVideoHeight == 0)
                {
                    Fail("This attachment does not contain a playable video track.");
                    return;
                }
                _status.Text = _video.Asset.Name;
                _expand.Visibility = Visibility.Visible;
                UpdateButtons();
                UpdateVideoSizing();
                sender.Play();
            }
            catch (Exception error) { if (ReferenceEquals(_player, sender)) Fail(error.Message, error); }
        });

        private void ControlsFailed(Exception error)
        {
            if (IsCurrent(_loadVersion)) Fail(error.Message, error);
        }

        private void ViewportSizeChanged(object sender, SizeChangedEventArgs args) => UpdateVideoSizing();
        private void FitFillClicked(object sender, RoutedEventArgs args) => UpdateVideoSizing();

        private void ExpandClicked(object sender, RoutedEventArgs args)
        {
            if (!IsCurrent(_loadVersion) || _element is null || _player is null || _expandedView is not null) return;
            try
            {
                // WinUI's current default MPE template does not bind IsFullWindow
                // to its presenter. Expand within this window with an explicit exit
                // and native seek controls, keeping the existing decoder/source.
                var expanded = new Grid
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 18, 22)),
                    RequestedTheme = ElementTheme.Dark,
                    TabFocusNavigation = KeyboardNavigationMode.Cycle,
                    Padding = new Thickness(20),
                    RowSpacing = 12,
                };
                expanded.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                expanded.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                expanded.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var header = new Grid { ColumnSpacing = 12 };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock
                {
                    Text = _video.Asset.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var exit = new Button { Content = "Exit full window · Esc" };
                AutomationProperties.SetName(exit, "Exit full-window video");
                exit.Click += ExitExpandedClicked;
                Grid.SetColumn(exit, 1);
                header.Children.Add(title);
                header.Children.Add(exit);
                var viewport = new Grid();
                Grid.SetRow(viewport, 1);
                var controls = new InlineMediaPlaybackControls($"full-window video {_video.Asset.Name}", ControlsFailed);
                Grid.SetRow(controls.View, 2);
                expanded.Children.Add(header);
                expanded.Children.Add(viewport);
                expanded.Children.Add(controls.View);
                expanded.AddHandler(UIElement.KeyDownEvent, _fullWindowKeyHandler, true);
                expanded.AddHandler(UIElement.PointerWheelChangedEvent, _fullWindowWheelHandler, true);
                Grid.SetRow(expanded, 1); // Preserve the app's title bar and window controls.
                Grid.SetColumnSpan(expanded, Math.Max(1, _owner.RootGrid.ColumnDefinitions.Count));
                Canvas.SetZIndex(expanded, 100);
                _expandedView = expanded;
                _expandedViewport = viewport;
                _expandedControls = controls;
                _exitExpanded = exit;
                _element.SetMediaPlayer(null);
                _viewport.Children.Remove(_element);
                viewport.Children.Add(_element);
                _element.Stretch = Stretch.Uniform;
                _owner.RootGrid.Children.Add(expanded);
                _element.SetMediaPlayer(_player);
                controls.Attach(_player, _controls.HasEnded);
                exit.Focus(FocusState.Programmatic);
            }
            catch (Exception error)
            {
                CloseExpanded(restoreInline: true);
                _owner._environment.ReportError("Full-window video failed", error);
            }
        }

        private void ExitExpandedClicked(object sender, RoutedEventArgs args) => CloseExpanded(restoreInline: true);

        private void FullWindowKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (args.Key != VirtualKey.Escape || _expandedView is null) return;
            CloseExpanded(restoreInline: true);
            args.Handled = true;
        }

        private void FullWindowPointerWheelChanged(object sender, PointerRoutedEventArgs args)
        {
            // Full-window input still bubbles through the message list. Prevent
            // scrolling it out from underneath the active native player surface.
            if (_expandedView is not null) args.Handled = true;
        }

        private void CloseExpanded(bool restoreInline)
        {
            var expanded = _expandedView;
            if (expanded is null) return;
            var viewport = _expandedViewport;
            var controls = _expandedControls;
            var exit = _exitExpanded;
            _expandedView = null;
            _expandedViewport = null;
            _expandedControls = null;
            _exitExpanded = null;
            controls?.Dispose();
            if (exit is not null) exit.Click -= ExitExpandedClicked;
            expanded.RemoveHandler(UIElement.KeyDownEvent, _fullWindowKeyHandler);
            expanded.RemoveHandler(UIElement.PointerWheelChangedEvent, _fullWindowWheelHandler);
            Exception? failure = null;
            try
            {
                if (_element is { } element)
                {
                    element.SetMediaPlayer(null);
                    viewport?.Children.Remove(element);
                    if (restoreInline)
                    {
                        if (!_viewport.Children.Contains(element)) _viewport.Children.Add(element);
                        element.SetMediaPlayer(_player);
                    }
                }
            }
            catch (Exception error) when (error is COMException or ObjectDisposedException or InvalidOperationException) { failure = error; }
            finally { _owner.RootGrid.Children.Remove(expanded); }
            if (!restoreInline) return;
            if (failure is not null) { Fail(failure.Message, failure); return; }
            UpdateVideoSizing();
            if (IsCurrent(_loadVersion)) _expand.Focus(FocusState.Programmatic);
        }

        private void NaturalVideoSizeChanged(MediaPlaybackSession sender, object args) => View.DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrent(_loadVersion) || _player is null) return;
            try { if (ReferenceEquals(_player.PlaybackSession, sender)) UpdateVideoSizing(); }
            catch (COMException) { }
            catch (ObjectDisposedException) { }
        });

        private void UpdateVideoSizing()
        {
            if (_player is null || _element is null) { _fitFill.Visibility = Visibility.Collapsed; return; }
            try
            {
                var session = _player.PlaybackSession;
                var width = session.NaturalVideoWidth;
                var height = session.NaturalVideoHeight;
                var viewportRatio = (_viewport.ActualWidth > 0 ? _viewport.ActualWidth : View.Width) / _viewport.Height;
                var meaningful = width > 0 && height > 0 && Math.Abs((double)width / height - viewportRatio) > 0.02;
                _fitFill.Visibility = meaningful ? Visibility.Visible : Visibility.Collapsed;
                _fitFill.IsEnabled = meaningful && IsCurrent(_loadVersion);
                var fill = meaningful && _fitFill.IsChecked == true;
                // UniformToFill crops to the fixed preview rectangle without
                // distorting the source; Uniform displays the full original ratio.
                _element.Stretch = fill && _expandedView is null ? Stretch.UniformToFill : Stretch.Uniform;
                _fitFill.Content = fill ? "Fill" : "Fit";
                AutomationProperties.SetName(_fitFill, fill ? "Video sizing: Fill, toggle to Fit" : "Video sizing: Fit, toggle to Fill");
                ToolTipService.SetToolTip(_fitFill, fill
                    ? "Fill preview · edges may be cropped. Click to fit the whole video."
                    : "Fit whole video · click to fill the preview.");
            }
            catch (COMException) { _fitFill.Visibility = Visibility.Collapsed; }
            catch (ObjectDisposedException) { _fitFill.Visibility = Visibility.Collapsed; }
        }

        private void MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            var message = string.IsNullOrWhiteSpace(args.ErrorMessage) ? args.Error.ToString() : args.ErrorMessage;
            _ = View.DispatcherQueue.TryEnqueue(() =>
            {
                if (IsCurrent(_loadVersion) && ReferenceEquals(_player, sender)) Fail(message);
            });
        }

        private void Fail(string message, Exception? error = null)
        {
            ReleasePlayer();
            _status.Text = $"Video could not be played: {message} Try again, open it externally, or save the original file.";
            _play.Content = "↻ Retry video";
            _play.Visibility = Visibility.Visible;
            UpdateButtons();
            if (error is not null) _owner._environment.ReportError("Video playback failed", error);
        }

        private async Task FileActionAsync(bool save)
        {
            if (!_active || _busy || _disposed || _lifetime is null) return;
            var version = _loadVersion;
            var operation = ++_operationVersion;
            var token = _lifetime.Token;
            _busy = true;
            ReleasePlayer();
            _play.Visibility = Visibility.Visible;
            UpdateButtons();
            try
            {
                if (save)
                {
                    // App SDK's All Files picker preserves unknown or missing extensions instead of inventing a codec.
                    var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(_owner.AppWindow.Id)
                    {
                        SuggestedFileName = SafeVideoName(_video.Asset.Name),
                        SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Downloads,
                    };
                    var destination = await picker.PickSaveFileAsync();
                    EnsureCurrent(version, token);
                    if (destination is null) return;
                    var reference = await _owner._environment.Media.GetReferenceAsync(_video.Asset, true, token);
                    EnsureCurrent(version, token);
                    await using (var input = File.OpenRead(reference.Identifier))
                    await using (var output = File.Create(destination.Path))
                        await input.CopyToAsync(output, token);
                    EnsureCurrent(version, token);
                    _status.Text = "Saved the original video file.";
                }
                else
                {
                    var reference = await _owner._environment.Media.GetReferenceAsync(_video.Asset, true, token);
                    EnsureCurrent(version, token);
                    await _owner.OpenAssetAsync(reference.ResolvedAsset ?? _video.Asset);
                    EnsureCurrent(version, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || !IsCurrent(version)) { }
            catch (Exception error)
            {
                if (IsCurrent(version))
                {
                    _status.Text = $"Video file action failed: {error.Message}";
                    _owner._environment.ReportError("Video file action failed", error);
                }
            }
            finally
            {
                if (IsCurrent(version) && operation == _operationVersion) { _busy = false; UpdateButtons(); }
            }
        }

        private static string SafeVideoName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).TrimEnd(' ', '.');
            if (safe.Length == 0) return "video";
            var stem = safe.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            var device = stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && "123456789¹²³".Contains(stem[3]));
            return device ? $"_{safe}" : safe;
        }

        private void UpdateButtons()
        {
            var enabled = IsCurrent(_loadVersion) && !_busy;
            _play.IsEnabled = enabled && _player is null;
            _open.IsEnabled = _save.IsEnabled = enabled;
            _expand.IsEnabled = enabled && _player is not null;
        }

        private void ReleasePlayer()
        {
            CloseExpanded(restoreInline: false);
            _controls.Detach();
            _fitFill.IsChecked = false;
            _fitFill.Visibility = Visibility.Collapsed;
            _expand.Visibility = Visibility.Collapsed;
            _controls.View.Visibility = Visibility.Visible;
            var player = _player;
            var source = _source;
            var stream = _mediaStream;
            var element = _element;
            _player = null;
            _source = null;
            _mediaStream = null;
            _element = null;
            if (player is not null)
            {
                try
                {
                    player.MediaOpened -= MediaOpened;
                    player.MediaFailed -= MediaFailed;
                    player.PlaybackSession.NaturalVideoSizeChanged -= NaturalVideoSizeChanged;
                }
                catch (COMException) { }
                catch (ObjectDisposedException) { }
            }
            try
            {
                if (element is not null)
                {
                    element.AreTransportControlsEnabled = false;
                    element.SetMediaPlayer(null);
                }
            }
            catch (COMException) { }
            catch (ObjectDisposedException) { }
            if (element is not null) _viewport.Children.Remove(element);
            try { player?.Dispose(); }
            catch (COMException) { }
            catch (ObjectDisposedException) { }
            try { source?.Dispose(); }
            catch (COMException) { }
            catch (ObjectDisposedException) { }
            try { stream?.Dispose(); }
            catch (COMException) { }
            catch (ObjectDisposedException) { }
            _poster.Visibility = _poster.Source is null ? Visibility.Collapsed : Visibility.Visible;
            _placeholder.Visibility = _poster.Source is null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Deactivate()
        {
            _active = false;
            _loadVersion++;
            _operationVersion++;
            var lifetime = _lifetime;
            _lifetime = null;
            lifetime?.Cancel();
            lifetime?.Dispose();
            ReleasePlayer();
            _poster.Source = null;
            _poster.Visibility = Visibility.Collapsed;
            _placeholder.Visibility = Visibility.Visible;
            _busy = false;
            _play.Content = "▶ Play video";
            _play.Visibility = Visibility.Visible;
            _status.Text = _video.Asset.Name;
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
            _viewport.SizeChanged -= ViewportSizeChanged;
            _fitFill.Click -= FitFillClicked;
            _expand.Click -= ExpandClicked;
            View.Loaded -= Loaded;
            View.Unloaded -= Unloaded;
            GC.SuppressFinalize(this);
        }
    }
}
