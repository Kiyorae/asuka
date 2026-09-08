using System.ComponentModel;
using System.Globalization;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Asuka.App;

/// <summary>Manages one bot account's local collection and chooses a normal image attachment.</summary>
public sealed class CustomFacesDialog : ContentDialog, IDisposable
{
    private const string ContextChangedMessage = "The bot account or protocol changed. Reopen custom images to continue.";
    private readonly AppEnvironment _environment;
    private readonly Window _owner;
    private readonly string? _selfId;
    private readonly bool _allowSelection;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly ListView _faces = new() { Height = 250, SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = nameof(CustomFace.Name) };
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly TextBlock _empty = new() { Text = "Import an image to start this account's collection.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly Image _preview = new() { Width = 256, Height = 160, Stretch = Stretch.Uniform };
    private readonly Button _import = new() { Content = "Import image" };
    private readonly Button _remove = new() { Content = "Remove from collection" };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private CancellationTokenSource? _previewCancellation;
    private bool _opened;
    private bool _disposed;
    private bool _busy;
    private bool _refreshing;
    private bool _refreshPending;
    private bool _restoringSelection;
    private bool _previewReady;
    private int _selectionVersion;

    public CustomFacesDialog(AppEnvironment environment, Window owner, bool allowSelection = true)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(owner);
        _environment = environment;
        _owner = owner;
        _selfId = environment.BotPersona?.Id;
        _allowSelection = allowSelection;
        _token = _lifetime.Token;
        Title = "Custom images";
        PrimaryButtonText = allowSelection ? "Add to message" : string.Empty;
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        XamlRoot = (owner.Content as FrameworkElement)?.XamlRoot;
        RequestedTheme = WindowChrome.ToElementTheme(environment.Preferences.Theme);
        Resources["ContentDialogMaxWidth"] = 680d;
        var root = new StackPanel { Width = 600, Spacing = 10 };
        root.Children.Add(_status);
        root.Children.Add(new TextBlock
        {
            Text = $"{environment.BotPersona?.DisplayName ?? "No bot selected"} · {_selfId ?? "—"}",
            TextWrapping = TextWrapping.Wrap,
        });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(_import);
        toolbar.Children.Add(_remove);
        toolbar.Children.Add(_refresh);
        root.Children.Add(toolbar);
        root.Children.Add(_faces);
        root.Children.Add(_empty);
        root.Children.Add(_preview);
        root.Children.Add(_details);
        Content = new ScrollViewer { Content = root, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(_faces, "Custom image collection");
        AutomationProperties.SetName(_preview, "Selected custom image preview");
        _import.Click += async (_, _) => await ImportAsync();
        _remove.Click += async (_, _) => await RemoveAsync();
        _refresh.Click += async (_, _) => await RefreshAsync();
        _faces.SelectionChanged += async (_, _) =>
        {
            if (_restoringSelection) return;
            _selectionVersion++;
            await LoadPreviewAsync();
        };
        PrimaryButtonClick += ChooseImage;
        CloseButtonClick += (_, _) => _lifetime.Cancel();
        Opened += async (_, _) =>
        {
            _opened = true;
            environment.Store.Changed += StoreChanged;
            environment.PropertyChanged += EnvironmentPropertyChanged;
            environment.PreferencesChanged += ContextChanged;
            owner.Closed += OwnerClosed;
            await RefreshAsync();
        };
        Closed += (_, _) => Dispose();
        UpdateActions();
    }

    public ImageSegment? SelectedImage { get; private set; }
    private CustomFace? Selected => _faces.SelectedItem as CustomFace;
    private bool ContextMatches => _environment.Preferences.Protocol == ProtocolKind.Milky
        && _selfId is not null && _environment.BotPersona?.Id == _selfId;

    private void StoreChanged(object? sender, StoreChangedEventArgs args)
    {
        if ((args.Changes & (StoreChangeKind.CustomFaces | StoreChangeKind.Users | StoreChangeKind.Assets)) != 0) QueueRefresh();
    }

    private void EnvironmentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppEnvironment.BotPersona) or nameof(AppEnvironment.Preferences)) ContextChanged(sender, EventArgs.Empty);
    }

    private void ContextChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        if (!ContextMatches) _lifetime.Cancel();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_opened) return;
            if (!ContextMatches)
            {
                ClearPreview();
                _status.Title = "Custom images unavailable";
                _status.Message = ContextChangedMessage;
                _status.Severity = InfoBarSeverity.Warning;
                _status.IsOpen = true;
            }
            UpdateActions();
        });
    }

    private void OwnerClosed(object sender, WindowEventArgs args) => Dispose();

    private void QueueRefresh() => DispatcherQueue.TryEnqueue(async () =>
    {
        if (!_opened) return;
        _refreshPending = true;
        await RefreshAsync();
    });

    private async Task RefreshAsync(string? selectAssetId = null)
    {
        if (!_opened || _busy || _refreshing) return;
        _refreshing = true;
        UpdateActions();
        try
        {
            do
            {
                _refreshPending = false;
                RequireContext();
                var faces = await _environment.Platform.GetCustomFacesAsync(_selfId!, _token);
                RequireContext();
                var previousId = Selected?.Asset.Id;
                var wantedId = selectAssetId ?? previousId;
                _restoringSelection = true;
                try
                {
                    _faces.ItemsSource = faces;
                    _faces.SelectedItem = faces.FirstOrDefault(face => face.Asset.Id == wantedId);
                }
                finally { _restoringSelection = false; }
                _empty.Visibility = faces.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (Selected?.Asset.Id != previousId || !_previewReady)
                {
                    _selectionVersion++;
                    await LoadPreviewAsync();
                }
            } while (_refreshPending && _opened);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error) { ClearPreview(); ShowError(error); }
        finally { _refreshing = false; UpdateActions(); }
    }

    private async Task ImportAsync()
    {
        if (_busy || _refreshing || !_opened) return;
        _busy = true;
        _status.IsOpen = false;
        UpdateActions();
        string? addedId = null;
        try
        {
            RequireContext();
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var extension in (string[])[".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"]) picker.FileTypeFilter.Add(extension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));
            var source = await picker.PickSingleFileAsync();
            RequireContext();
            if (source is null) return;
            var attachment = await _environment.CreateAttachmentAsync(source, _token);
            RequireContext();
            // Decode the immutable cached copy, so a changing source cannot bypass validation.
            var cached = await StorageFile.GetFileFromPathAsync(_environment.Assets.LocationOf(attachment.Asset.Id)).AsTask(_token);
            using (var stream = await cached.OpenReadAsync().AsTask(_token))
            {
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(_token);
                if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || decoder.PixelWidth > 8192 || decoder.PixelHeight > 8192
                    || (ulong)decoder.PixelWidth * decoder.PixelHeight > 16_777_216)
                    throw new InvalidOperationException("Custom images must be at most 8192 pixels per side and 16 megapixels.");
                var scale = Math.Min(1d, 256d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
                _ = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    new BitmapTransform
                    {
                        ScaledWidth = Math.Max(1u, (uint)(decoder.PixelWidth * scale)),
                        ScaledHeight = Math.Max(1u, (uint)(decoder.PixelHeight * scale)),
                    }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(_token);
            }
            RequireContext();
            var face = await _environment.Platform.AddCustomFaceAsync(_selfId!, attachment.Asset, _token);
            RequireContext();
            addedId = face.Asset.Id;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
        finally { _busy = false; await RefreshAsync(addedId); UpdateActions(); }
    }

    private async Task RemoveAsync()
    {
        if (_busy || _refreshing || Selected is not { } selected) return;
        _busy = true;
        UpdateActions();
        try
        {
            RequireContext();
            await _environment.Platform.RemoveCustomFaceAsync(_selfId!, selected.Asset.Id, _token);
            RequireContext();
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
        finally { _busy = false; await RefreshAsync(); UpdateActions(); }
    }

    private async void ChooseImage(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (!_allowSelection || _busy || _refreshing || !_previewReady || Selected is not { } selected) return;
        var deferral = args.GetDeferral();
        var version = _selectionVersion;
        _busy = true;
        UpdateActions();
        try
        {
            RequireContext();
            var faces = await _environment.Platform.GetCustomFacesAsync(_selfId!, _token);
            RequireContext();
            if (version != _selectionVersion || Selected?.Asset.Id != selected.Asset.Id) return;
            var current = faces.FirstOrDefault(face => face.Asset.Id == selected.Asset.Id)
                ?? throw new InvalidOperationException("The selected image was removed from this collection.");
            if (!_environment.Assets.Exists(current.Asset.Id)) throw new FileNotFoundException("The cached image is no longer available.");
            SelectedImage = new ImageSegment(current.Asset);
            args.Cancel = false;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
        finally { _busy = false; UpdateActions(); deferral.Complete(); }
    }

    private async Task LoadPreviewAsync()
    {
        ClearPreview();
        if (Selected is not { } selected || !_opened || !ContextMatches || _token.IsCancellationRequested) { UpdateActions(); return; }
        var version = _selectionVersion;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_token);
        _previewCancellation = cancellation;
        var token = cancellation.Token;
        _details.Text = $"{selected.Asset.Name} · {selected.Asset.ByteCount:N0} bytes · {selected.AddedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";
        UpdateActions();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_environment.Assets.LocationOf(selected.Asset.Id)).AsTask(token);
            using var stream = await file.OpenReadAsync().AsTask(token);
            var bitmap = new BitmapImage { DecodePixelWidth = 256, DecodePixelHeight = 256, AutoPlay = false };
            await bitmap.SetSourceAsync(stream).AsTask(token);
            RequireContext();
            if (token.IsCancellationRequested || version != _selectionVersion || Selected?.Asset.Id != selected.Asset.Id) return;
            _preview.Source = bitmap;
            _previewReady = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (version == _selectionVersion && !token.IsCancellationRequested) ShowError(error); }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
            UpdateActions();
        }
    }

    private void ClearPreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation = null;
        _preview.Source = null;
        _previewReady = false;
        _details.Text = string.Empty;
    }

    private void RequireContext()
    {
        _token.ThrowIfCancellationRequested();
        if (!_opened || !ContextMatches) throw new InvalidOperationException(ContextChangedMessage);
    }

    private void UpdateActions()
    {
        var enabled = _opened && !_token.IsCancellationRequested && ContextMatches && !_busy && !_refreshing;
        _faces.IsEnabled = enabled;
        _import.IsEnabled = enabled;
        _refresh.IsEnabled = enabled;
        _remove.IsEnabled = enabled && Selected is not null;
        IsPrimaryButtonEnabled = _allowSelection && enabled && _previewReady && Selected is not null;
    }

    private void ShowError(Exception error)
    {
        if (!_opened) return;
        _status.Title = "Custom image action failed";
        _status.Message = error.Message;
        _status.Severity = InfoBarSeverity.Error;
        _status.IsOpen = true;
        _environment.ReportError("Custom images", error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _opened = false;
        _environment.Store.Changed -= StoreChanged;
        _environment.PropertyChanged -= EnvironmentPropertyChanged;
        _environment.PreferencesChanged -= ContextChanged;
        _owner.Closed -= OwnerClosed;
        _lifetime.Cancel();
        ClearPreview();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
