using System.ComponentModel;
using System.Globalization;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using User = Asuka.Core.User;

namespace Asuka.App;

/// <summary>Local history of files shared by the two participants of a private conversation.</summary>
public sealed class PrivateFilesDialog : ContentDialog, IDisposable
{
    private const string ContextChangedMessage = "The active conversation, persona, or protocol changed. Reopen shared files to continue.";
    private readonly AppEnvironment _environment;
    private readonly Chat _chat;
    private readonly Chat? _selectedChatAtOpen;
    private readonly User _actor;
    private readonly string _peerId;
    private readonly Window _owner;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly DispatcherTimer _expiryTimer = new();
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly ListView _files = new() { SelectionMode = ListViewSelectionMode.Single, Height = 300 };
    private readonly TextBlock _participants = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock _empty = new() { Text = "No shared files in this conversation yet.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private readonly Button _open = new() { Content = "Open" };
    private readonly Button _save = new() { Content = "Save as" };
    private IReadOnlyList<SharedFile> _listing = [];
    private string _peerName;
    private bool _opened;
    private bool _disposed;
    private bool _busy;
    private bool _refreshing;
    private bool _refreshPending;
    private bool _restoringSelection;
    private bool _participantsExist;
    private int _selectionVersion;

    public PrivateFilesDialog(AppEnvironment environment, Chat chat, User actor, Window parent)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(parent);
        _peerId = chat.CounterpartId(actor.Id)
            ?? throw new InvalidOperationException("Choose one of this conversation's participants to browse shared files.");
        _environment = environment;
        _chat = chat;
        _selectedChatAtOpen = environment.SelectedChat;
        _actor = actor;
        _peerName = _peerId;
        _owner = parent;
        _token = _lifetime.Token;
        Resources["ContentDialogMaxWidth"] = 680d;
        Title = "Shared files";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        XamlRoot = (parent.Content as FrameworkElement)?.XamlRoot;
        RequestedTheme = WindowChrome.ToElementTheme(environment.Preferences.Theme);
        var root = new StackPanel { Spacing = 12, Width = 600 };
        root.Children.Add(_status);
        root.Children.Add(_participants);
        root.Children.Add(_refresh);
        root.Children.Add(_files);
        root.Children.Add(_empty);
        root.Children.Add(_details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_open);
        actions.Children.Add(_save);
        root.Children.Add(actions);
        Content = new ScrollViewer { Content = root, MaxHeight = 590, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(_files, "Private shared files");
        AutomationProperties.SetName(_details, "Selected shared file details");
        Opened += async (_, _) =>
        {
            _opened = true;
            environment.Store.Changed += StoreChanged;
            environment.PropertyChanged += EnvironmentPropertyChanged;
            environment.PreferencesChanged += ContextChanged;
            environment.SelectedChatChanged += ContextChanged;
            await RefreshAsync();
        };
        Closed += (_, _) => Dispose();
        CloseButtonClick += (_, _) => _lifetime.Cancel();
        _files.SelectionChanged += (_, _) =>
        {
            if (!_restoringSelection) _selectionVersion++;
            UpdateDetails();
            UpdateActions();
        };
        _files.DoubleTapped += async (_, _) => await RunSelectedAsync(OpenAsync);
        _refresh.Click += async (_, _) => await RefreshAsync();
        _open.Click += async (_, _) => await RunSelectedAsync(OpenAsync);
        _save.Click += async (_, _) => await RunSelectedAsync(SaveAsync);
        _expiryTimer.Tick += ExpiryTimerTick;
        UpdateActions();
    }

    private SharedFile? Selected => (_files.SelectedItem as ListViewItem)?.Tag as SharedFile;
    private bool ContextMatches => _environment.Preferences.Protocol == ProtocolKind.Milky
        && _environment.BotPersona?.Id == _chat.SelfId && _environment.CurrentPersona?.Id == _actor.Id
        && _environment.SelectedChat == _selectedChatAtOpen;

    private void StoreChanged(object? sender, StoreChangedEventArgs args)
    {
        if ((args.Changes & (StoreChangeKind.Files | StoreChangeKind.Users | StoreChangeKind.Assets)) != 0) QueueRefresh();
    }

    private void EnvironmentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppEnvironment.CurrentPersona) or nameof(AppEnvironment.BotPersona)
            or nameof(AppEnvironment.Preferences) or nameof(AppEnvironment.SelectedChat)) ContextChanged(sender, EventArgs.Empty);
    }

    private void ContextChanged(object? sender, EventArgs args)
    {
        if (!_disposed && !ContextMatches) _lifetime.Cancel();
        QueueRefresh();
    }

    private void QueueRefresh() => DispatcherQueue.TryEnqueue(async () =>
    {
        if (!_opened) return;
        _refreshPending = true;
        await RefreshAsync();
    });

    private async void ExpiryTimerTick(object? sender, object args)
    {
        _expiryTimer.Stop();
        _refreshPending = true;
        UpdateActions();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
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
                var listing = await _environment.Platform.GetPrivateFileListingAsync(_actor.Id, _peerId, _token);
                var peer = await _environment.Store.GetUserAsync(_peerId, _token);
                RequireContext();
                _participantsExist = true;
                _peerName = peer?.DisplayName ?? _peerId;
                _participants.Text = $"{_actor.DisplayName} ↔ {_peerName} · {_actor.Id} / {_peerId}";
                var selectedId = Selected?.Id;
                _listing = listing;
                _restoringSelection = true;
                try
                {
                    _files.Items.Clear();
                    foreach (var file in listing)
                    {
                        var item = BuildRow(file);
                        _files.Items.Add(item);
                        if (file.Id == selectedId) _files.SelectedItem = item;
                    }
                }
                finally { _restoringSelection = false; }
                if (Selected?.Id != selectedId) _selectionVersion++;
                _empty.Visibility = listing.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateDetails();
                ScheduleExpiryRefresh();
            } while (_refreshPending && _opened);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            if (_opened && !ContextMatches) ShowContextChanged();
        }
        catch (Exception error)
        {
            _participantsExist = false;
            _files.Items.Clear();
            _listing = [];
            _expiryTimer.Stop();
            ShowError(error);
        }
        finally { _refreshing = false; UpdateActions(); }
    }

    private ListViewItem BuildRow(SharedFile file)
    {
        var labels = new StackPanel { Spacing = 3, Padding = new Thickness(4) };
        labels.Children.Add(new TextBlock { Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 555 });
        var direction = file.UploaderId == _actor.Id ? $"Sent to {_peerName}" : $"Received from {_peerName}";
        labels.Children.Add(new TextBlock
        {
            Text = $"{direction} · {file.Asset.ByteCount:N0} bytes · {FormatTime(file.UploadedAt)}"
                + (file.IsExpired ? " · Expired" : string.Empty),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });
        var item = new ListViewItem { Content = labels, Tag = file, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, $"{file.Name}, {direction}{(file.IsExpired ? ", expired" : string.Empty)}");
        return item;
    }

    private void UpdateDetails()
    {
        _details.Text = Selected is not { } file ? "Select a file to open it or save a copy."
            : $"Shared {FormatTime(file.UploadedAt)} · {file.Asset.ByteCount:N0} bytes. "
                + (file.IsExpired ? "This file has expired and can no longer be downloaded."
                    : file.ExpiresAt is { } expiry ? $"Available until {FormatTime(expiry)}." : "No expiry date.");
    }

    private void UpdateActions()
    {
        if (_disposed) return;
        var context = _opened && ContextMatches && !_token.IsCancellationRequested;
        var enabled = context && _participantsExist && !_busy && !_refreshing;
        _files.IsEnabled = enabled;
        _refresh.IsEnabled = context && !_busy && !_refreshing;
        _open.Visibility = _save.Visibility = context ? Visibility.Visible : Visibility.Collapsed;
        _open.IsEnabled = _save.IsEnabled = enabled && Selected is { IsExpired: false };
    }

    private void ScheduleExpiryRefresh()
    {
        _expiryTimer.Stop();
        var now = DateTimeOffset.UtcNow;
        var future = _listing.Where(file => file.ExpiresAt > now).Select(file => file.ExpiresAt!.Value).ToArray();
        if (future.Length == 0 || !_opened || _token.IsCancellationRequested) return;
        var remaining = future.Min() - now;
        _expiryTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds + 25, 1, int.MaxValue));
        _expiryTimer.Start();
    }

    private async Task RunSelectedAsync(Func<SharedFile, int, Task> action)
    {
        if (_busy || _refreshing || !_opened || !_participantsExist || Selected is not { IsExpired: false } file) return;
        var version = _selectionVersion;
        _busy = true;
        _status.IsOpen = false;
        UpdateActions();
        try { RequireSelection(file.Id, version); await action(file, version); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            if (_opened && !ContextMatches) ShowContextChanged();
        }
        catch (Exception error) { ShowError(error); }
        finally { _busy = false; await RefreshAsync(); UpdateActions(); }
    }

    private async Task<SharedFile> AuthorizeAsync(string fileId, int version)
    {
        RequireSelection(fileId, version);
        var file = await _environment.Platform.GetSharedFileForDownloadAsync(fileId, _actor.Id, _token);
        RequireSelection(fileId, version);
        if (file.GroupId is not null || !((file.UploaderId == _actor.Id && file.RecipientId == _peerId)
            || (file.UploaderId == _peerId && file.RecipientId == _actor.Id)))
            throw new InvalidOperationException("This file is no longer shared in the selected conversation.");
        return file;
    }

    private async Task<SharedFile> ResolveFileAsync(SharedFile file, int version)
    {
        var authorized = await AuthorizeAsync(file.Id, version);
        var reference = await _environment.Media.GetReferenceAsync(authorized.Asset, true, _token);
        RequireSelection(file.Id, version);
        var latest = await AuthorizeAsync(file.Id, version);
        if (latest.Asset.Id != authorized.Asset.Id)
            throw new InvalidOperationException("The shared file changed. Refresh and try again.");
        return latest with { Asset = reference.ResolvedAsset ?? authorized.Asset };
    }

    private async Task OpenAsync(SharedFile file, int version)
    {
        var current = await ResolveFileAsync(file, version);
        RequireSelection(file.Id, version);
        var directory = Path.Combine(Path.GetTempPath(), "Asuka", "private-file-preview", current.Asset.Id);
        Directory.CreateDirectory(directory);
        var preview = Path.Combine(directory, SafeLocalName(current.Name));
        await using (var input = File.OpenRead(_environment.Assets.LocationOf(current.Asset.Id)))
        await using (var output = File.Create(preview))
            await input.CopyToAsync(output, _token);
        RequireSelection(file.Id, version);
        var source = await StorageFile.GetFileFromPathAsync(preview);
        _ = await AuthorizeAsync(file.Id, version);
        if (!await Launcher.LaunchFileAsync(source))
            throw new InvalidOperationException("Windows could not open this file. Use Save as to save a copy.");
        RequireSelection(file.Id, version);
    }

    private async Task SaveAsync(SharedFile file, int version)
    {
        RequireSelection(file.Id, version);
        var name = SafeLocalName(file.Name);
        var extension = Path.GetExtension(name);
        if (string.IsNullOrEmpty(extension)) extension = ".bin";
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = Path.GetFileNameWithoutExtension(name),
        };
        picker.FileTypeChoices.Add("File", new List<string> { extension });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));
        var destination = await picker.PickSaveFileAsync();
        RequireSelection(file.Id, version);
        if (destination is null) return;
        var current = await ResolveFileAsync(file, version);
        var source = await StorageFile.GetFileFromPathAsync(_environment.Assets.LocationOf(current.Asset.Id));
        _ = await AuthorizeAsync(file.Id, version);
        await source.CopyAndReplaceAsync(destination).AsTask(_token);
        RequireSelection(file.Id, version);
    }

    private static string SafeLocalName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(safe)) return "file.bin";
        var stem = safe.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        var device = stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3]));
        return device ? $"_{safe}" : safe;
    }

    private static string FormatTime(DateTimeOffset time) => time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private void RequireContext()
    {
        _token.ThrowIfCancellationRequested();
        if (!_opened || !ContextMatches) throw new InvalidOperationException(ContextChangedMessage);
    }

    private void RequireSelection(string fileId, int version)
    {
        RequireContext();
        if (_selectionVersion != version || Selected?.Id != fileId)
            throw new InvalidOperationException("The selected file changed. Select the file again to continue.");
    }

    private void ShowContextChanged()
    {
        _status.Title = "Shared files unavailable";
        _status.Message = ContextChangedMessage;
        _status.Severity = InfoBarSeverity.Warning;
        _status.IsOpen = true;
        UpdateActions();
    }

    private void ShowError(Exception error)
    {
        if (!_opened) return;
        _status.Title = "File action failed";
        _status.Message = error.Message;
        _status.Severity = InfoBarSeverity.Error;
        _status.IsOpen = true;
        _environment.ReportError("Private shared files", error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _opened = false;
        _environment.Store.Changed -= StoreChanged;
        _environment.PropertyChanged -= EnvironmentPropertyChanged;
        _environment.PreferencesChanged -= ContextChanged;
        _environment.SelectedChatChanged -= ContextChanged;
        _expiryTimer.Stop();
        _expiryTimer.Tick -= ExpiryTimerTick;
        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
