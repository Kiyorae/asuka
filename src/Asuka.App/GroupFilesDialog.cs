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

/// <summary>Milky's group file and folder actions, using the same platform operations as its API.</summary>
public sealed class GroupFilesDialog : ContentDialog, IDisposable
{
    private readonly AppEnvironment _environment;
    private readonly Group _group;
    private readonly User _operator;
    private readonly Window _owner;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _cancellationToken;
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, Height = 270 };
    private readonly TextBlock _path = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _empty = new() { Text = "This folder is empty.", Margin = new Thickness(8), Visibility = Visibility.Collapsed };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _rootButton = new() { Content = "All files" };
    private readonly Button _upload = new() { Content = "Upload file" };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private readonly Button _open = new() { Content = "Open" };
    private readonly Button _save = new() { Content = "Save as…" };
    private readonly Button _persist = new() { Content = "Keep permanently" };
    private readonly Button _delete = new() { Content = "Delete" };
    private readonly TextBox _folderName = new() { PlaceholderText = "New folder name", Width = 230 };
    private readonly Button _createFolder = new() { Content = "Create folder" };
    private readonly StackPanel _folderControls = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBox _name = new() { PlaceholderText = "Selected file or folder name", Width = 320 };
    private readonly Button _rename = new() { Content = "Rename" };
    private readonly ComboBox _destination = new() { PlaceholderText = "Move to folder", Width = 230, DisplayMemberPath = nameof(FolderChoice.Name) };
    private readonly Button _move = new() { Content = "Move" };
    private readonly StackPanel _confirmation = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _confirmationText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _confirmDelete = new() { Content = "Delete" };
    private GroupMember? _member;
    private string _parentId = "/";
    private string _parentName = "All files";
    private FileEntry? _pendingDelete;
    private bool _opened;
    private bool _busy;
    private bool _refreshing;
    private bool _refreshNeeded;
    private bool _restoringSelection;
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _opened = false;
        _environment.Store.Changed -= StoreChanged;
        _environment.PreferencesChanged -= PreferencesChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
    private bool _wholeMuted;

    public GroupFilesDialog(AppEnvironment environment, Group group, User @operator, Window parent)
    {
        _environment = environment;
        _group = group;
        _wholeMuted = group.WholeMuted;
        _operator = @operator;
        _owner = parent;
        _cancellationToken = _lifetime.Token;
        Title = $"Files · {group.Name}";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        MinWidth = 720;
        Resources["ContentDialogMaxWidth"] = 760d;
        Content = BuildContent();
        Opened += async (_, _) =>
        {
            _opened = true;
            _environment.Store.Changed += StoreChanged;
            _environment.PreferencesChanged += PreferencesChanged;
            await RefreshSafelyAsync();
        };
        Closed += (_, _) => Dispose();
        _list.SelectionChanged += (_, _) => SelectionChanged();
        _list.DoubleTapped += async (_, _) =>
        {
            if (Selected is { Folder: { } folder })
                await NavigateAsync(folder.Id, folder.Name);
        };
        _name.TextChanged += (_, _) => UpdateActions();
        _folderName.TextChanged += (_, _) => UpdateActions();
        _destination.SelectionChanged += (_, _) => UpdateActions();
        _rootButton.Click += async (_, _) => await NavigateAsync("/", "All files");
        _refresh.Click += async (_, _) => await RefreshSafelyAsync();
        _upload.Click += async (_, _) => await RunAsync(UploadAsync);
        _createFolder.Click += async (_, _) => await RunAsync(async () =>
        {
            await _environment.Platform.CreateGroupFolderAsync(_group.Id, _operator.Id, _folderName.Text, _cancellationToken);
            _folderName.Text = string.Empty;
        });
        _open.Click += async (_, _) =>
        {
            if (Selected is { Folder: { } folder }) await NavigateAsync(folder.Id, folder.Name);
            else if (Selected is { File: { } file }) await RunAsync(() => OpenAsync(file));
        };
        _save.Click += async (_, _) =>
        {
            if (Selected is { File: { } file }) await RunAsync(() => SaveAsync(file));
        };
        _rename.Click += async (_, _) =>
        {
            var selected = Selected;
            var name = _name.Text;
            await RunAsync(async () =>
            {
                if (selected?.File is { } file)
                    await _environment.Platform.RenameGroupFileAsync(_group.Id, file.Id, _operator.Id, name, file.ParentFolderId, _cancellationToken);
                else if (selected?.Folder is { } folder)
                    await _environment.Platform.RenameGroupFolderAsync(_group.Id, folder.Id, _operator.Id, name, _cancellationToken);
            });
        };
        _move.Click += async (_, _) =>
        {
            if (Selected is { File: { } file } && _destination.SelectedItem is FolderChoice target)
                await RunAsync(() => _environment.Platform.MoveGroupFileAsync(_group.Id, file.Id, _operator.Id,
                    file.ParentFolderId, target.Id, _cancellationToken));
        };
        _persist.Click += async (_, _) =>
        {
            if (Selected is { File: { } file })
                await RunAsync(() => _environment.Platform.PersistGroupFileAsync(_group.Id, file.Id, _operator.Id, _cancellationToken));
        };
        _delete.Click += (_, _) =>
        {
            _pendingDelete = Selected;
            _confirmationText.Text = _pendingDelete?.Folder is { } folder
                ? $"Delete “{folder.Name}” and every file it contains?"
                : $"Delete “{_pendingDelete?.Name}”?";
            _confirmation.Visibility = _pendingDelete is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateActions();
        };
        _confirmDelete.Click += async (_, _) =>
        {
            var selected = _pendingDelete;
            await RunAsync(async () =>
            {
                if (selected?.File is { } file)
                    await _environment.Platform.DeleteGroupFileAsync(_group.Id, file.Id, _operator.Id, _cancellationToken);
                else if (selected?.Folder is { } folder)
                    await _environment.Platform.DeleteGroupFolderAsync(_group.Id, folder.Id, _operator.Id, _cancellationToken);
                CancelDelete();
            });
        };
        UpdateActions();
    }

    private FileEntry? Selected => (_list.SelectedItem as ListViewItem)?.Tag as FileEntry;
    private bool IsActiveContext => _opened && _environment.Preferences.Protocol == ProtocolKind.Milky
        && _environment.CurrentPersona?.Id == _operator.Id;
    private bool CanManage(FileEntry? entry) => _member is not null && entry is not null
        && (_member.Role > GroupRole.Member || entry.File?.UploaderId == _operator.Id);

    private ScrollViewer BuildContent()
    {
        var root = new StackPanel { Width = 680, Spacing = 12 };
        root.Children.Add(_status);
        root.Children.Add(new TextBlock { Text = $"Acting as {_operator.DisplayName}", Opacity = 0.75 });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(_upload);
        toolbar.Children.Add(_refresh);
        root.Children.Add(toolbar);
        _folderControls.Children.Add(_folderName);
        _folderControls.Children.Add(_createFolder);
        root.Children.Add(_folderControls);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        navigation.Children.Add(_rootButton);
        navigation.Children.Add(_path);
        root.Children.Add(navigation);
        root.Children.Add(_list);
        root.Children.Add(_empty);
        root.Children.Add(_details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in new[] { _open, _save, _persist, _delete }) actions.Children.Add(button);
        root.Children.Add(actions);
        var rename = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rename.Children.Add(_name);
        rename.Children.Add(_rename);
        root.Children.Add(rename);
        var move = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        move.Children.Add(_destination);
        move.Children.Add(_move);
        root.Children.Add(move);
        _confirmation.Children.Add(_confirmationText);
        var confirmationActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        confirmationActions.Children.Add(_confirmDelete);
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => CancelDelete();
        confirmationActions.Children.Add(cancel);
        _confirmation.Children.Add(confirmationActions);
        root.Children.Add(_confirmation);
        AutomationProperties.SetName(_list, "Group files and folders");
        AutomationProperties.SetName(_name, "Rename selected file or folder");
        AutomationProperties.SetName(_folderName, "New folder name");
        AutomationProperties.SetName(_destination, "Destination folder");
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async Task NavigateAsync(string folderId, string name)
    {
        if (_busy || _refreshing) return;
        _parentId = folderId;
        _parentName = name;
        CancelDelete();
        await RefreshSafelyAsync();
    }

    private void StoreChanged(object? sender, StoreChangedEventArgs args)
    {
        if ((args.Changes & (StoreChangeKind.Files | StoreChangeKind.Members | StoreChangeKind.Groups)) != 0)
            QueueRefresh();
    }

    private void PreferencesChanged(object? sender, EventArgs args) => QueueRefresh();

    private void QueueRefresh() => DispatcherQueue.TryEnqueue(async () =>
    {
        _refreshNeeded = true;
        await RefreshSafelyAsync();
    });

    private async Task RefreshSafelyAsync()
    {
        if (!_opened || _busy || _refreshing) return;
        _refreshing = true;
        UpdateActions();
        try
        {
            do
            {
                _refreshNeeded = false;
                RequireActiveContext();
                _member = await _environment.Store.GetMemberAsync(_group.Id, _operator.Id, _cancellationToken);
                var group = await _environment.Store.GetGroupAsync(_group.Id, _cancellationToken);
                _wholeMuted = group?.WholeMuted ?? true;
                Title = $"Files · {group?.Name ?? _group.Name}";
                if (_parentId != "/")
                {
                    var parent = await _environment.Store.GetGroupFolderAsync(_group.Id, _parentId, _cancellationToken);
                    _parentId = parent?.Id ?? "/";
                    _parentName = parent?.Name ?? "All files";
                }

                var listing = await _environment.Platform.GetGroupFileListingAsync(_group.Id, _operator.Id, _parentId, _cancellationToken);
                var folders = await _environment.Store.GetGroupFoldersAsync(_group.Id, cancellationToken: _cancellationToken);
                RequireActiveContext();
                var selectedId = Selected?.Id;
                var editedName = _name.Text;
                var nameWasEdited = editedName != Selected?.Name;
                var destinationId = (_destination.SelectedItem as FolderChoice)?.Id ?? _parentId;
                _restoringSelection = true;
                try
                {
                    _list.Items.Clear();
                    foreach (var entry in listing.Folders.Select(folder => new FileEntry(null, folder))
                        .Concat(listing.Files.Select(file => new FileEntry(file, null))))
                    {
                        var item = BuildItem(entry);
                        _list.Items.Add(item);
                        if (entry.Id == selectedId) _list.SelectedItem = item;
                    }

                    var choices = new[] { new FolderChoice("/", "All files") }
                        .Concat(folders.Select(folder => new FolderChoice(folder.Id, folder.Name))).ToArray();
                    _destination.ItemsSource = choices;
                    _destination.SelectedItem = choices.FirstOrDefault(choice => choice.Id == destinationId) ?? choices[0];
                    _name.Text = Selected?.Id == selectedId && nameWasEdited ? editedName : Selected?.Name ?? string.Empty;
                }
                finally { _restoringSelection = false; }
                _path.Text = _parentId == "/" ? string.Empty : $"/ {_parentName}";
                _empty.Visibility = _list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (_pendingDelete?.Id != Selected?.Id) CancelDelete();
                UpdateDetails();
            } while (_refreshNeeded && _opened);
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _member = null;
            _list.Items.Clear();
            ShowError(exception);
        }
        finally
        {
            _refreshing = false;
            UpdateActions();
        }
    }

    private static ListViewItem BuildItem(FileEntry entry)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(4) };
        content.Children.Add(new SymbolIcon(entry.Folder is null ? Symbol.Document : Symbol.Folder));
        var labels = new StackPanel { Spacing = 3 };
        labels.Children.Add(new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 530 });
        labels.Children.Add(new TextBlock
        {
            Text = entry.File is { } file
                ? $"{file.Asset.ByteCount:N0} bytes · {file.DownloadedTimes:N0} downloads · {(file.ExpiresAt is null ? "Permanent" : "Temporary")}"
                : $"{entry.Folder!.FileCount:N0} files",
            Opacity = 0.7,
        });
        content.Children.Add(labels);
        var item = new ListViewItem { Tag = entry, Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, entry.Name);
        return item;
    }

    private void SelectionChanged()
    {
        if (_restoringSelection) return;
        CancelDelete();
        _name.Text = Selected?.Name ?? string.Empty;
        UpdateDetails();
        UpdateActions();
    }

    private void UpdateDetails()
    {
        _details.Text = Selected switch
        {
            { File: { } file } => $"Uploaded {file.UploadedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} by {file.UploaderId}. "
                + (file.ExpiresAt is { } expiry ? $"Expires {expiry.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}." : "Kept permanently."),
            { Folder: { } folder } => $"Created {folder.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · "
                + $"Modified {folder.LastModifiedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}",
            _ => "Select a file to open, save, or manage it. Select a folder to browse its files.",
        };
    }

    private void UpdateActions()
    {
        var active = IsActiveContext && !_busy && !_refreshing && _member is not null;
        var selected = Selected;
        var manage = active && CanManage(selected);
        _list.IsEnabled = active;
        _rootButton.IsEnabled = active && _parentId != "/";
        _refresh.IsEnabled = IsActiveContext && !_busy && !_refreshing;
        _upload.IsEnabled = active && !_member!.IsMuted && (!_wholeMuted || _member.Role > GroupRole.Member);
        _folderControls.Visibility = _member?.Role > GroupRole.Member && _parentId == "/" ? Visibility.Visible : Visibility.Collapsed;
        _folderName.IsEnabled = active;
        _createFolder.IsEnabled = active && _parentId == "/" && _member?.Role > GroupRole.Member && !string.IsNullOrWhiteSpace(_folderName.Text);
        _open.IsEnabled = active && selected is not null;
        _open.Content = selected?.Folder is null ? "Open" : "Open folder";
        _save.IsEnabled = active && selected?.File is not null;
        _persist.Visibility = selected?.File?.ExpiresAt is null ? Visibility.Collapsed : Visibility.Visible;
        _persist.IsEnabled = manage;
        _delete.IsEnabled = manage;
        _name.IsEnabled = manage;
        _rename.IsEnabled = manage && !string.IsNullOrWhiteSpace(_name.Text) && _name.Text != selected?.Name;
        _destination.IsEnabled = manage && selected?.File is not null;
        _move.IsEnabled = _destination.IsEnabled && _destination.SelectedItem is FolderChoice target && target.Id != selected!.File!.ParentFolderId;
        _confirmDelete.IsEnabled = manage && selected?.Id == _pendingDelete?.Id;
        IsPrimaryButtonEnabled = !_busy;
    }

    private async Task UploadAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));
        var source = await picker.PickSingleFileAsync();
        if (source is null) return;
        RequireActiveContext();
        var attachment = await _environment.CreateAttachmentAsync(source, _cancellationToken);
        RequireActiveContext();
        await _environment.Platform.ShareGroupFileAsync(_group.Id, _operator.Id, attachment.Asset, _parentId, _cancellationToken);
    }

    private async Task<SharedFile> ResolveFileAsync(SharedFile file)
    {
        var current = await _environment.Platform.GetGroupFileForDownloadAsync(_group.Id, file.Id, _operator.Id, _cancellationToken);
        var reference = await _environment.Media.GetReferenceAsync(current.Asset, true, _cancellationToken);
        return current with { Asset = reference.ResolvedAsset ?? current.Asset };
    }

    private async Task OpenAsync(SharedFile file)
    {
        var current = await ResolveFileAsync(file);
        var directory = Path.Combine(Path.GetTempPath(), "Asuka", "file-preview", current.Id);
        Directory.CreateDirectory(directory);
        var preview = Path.Combine(directory, SafeLocalName(current.Name));
        await CopyFileAsync(_environment.Assets.LocationOf(current.Asset.Id), preview);
        RequireActiveContext();
        var source = await StorageFile.GetFileFromPathAsync(preview);
        if (!await Launcher.LaunchFileAsync(source))
            throw new InvalidOperationException("Windows could not find an app that opens this file. Use Save as to save a copy.");
        await _environment.Platform.RecordGroupFileDownloadAsync(_group.Id, current.Id, current.Asset.Id, _cancellationToken);
    }

    private async Task SaveAsync(SharedFile file)
    {
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
        if (destination is null) return;
        RequireActiveContext();
        var current = await ResolveFileAsync(file);
        var source = await StorageFile.GetFileFromPathAsync(_environment.Assets.LocationOf(current.Asset.Id));
        await source.CopyAndReplaceAsync(destination);
        await _environment.Platform.RecordGroupFileDownloadAsync(_group.Id, current.Id, current.Asset.Id, _cancellationToken);
    }

    private async Task CopyFileAsync(string source, string destination)
    {
        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, _cancellationToken);
    }

    private static string SafeLocalName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(safe)) return "file.bin";
        var stem = safe.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        var isDeviceName = stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3]));
        return isDeviceName ? $"_{safe}" : safe;
    }

    private void CancelDelete()
    {
        _pendingDelete = null;
        _confirmation.Visibility = Visibility.Collapsed;
        UpdateActions();
    }

    private void RequireActiveContext()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!IsActiveContext) throw new InvalidOperationException("The active persona or protocol changed. Reopen group files to continue.");
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _refreshing || !_opened) return;
        _busy = true;
        _status.IsOpen = false;
        UpdateActions();
        try
        {
            RequireActiveContext();
            await action();
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { ShowError(exception); }
        finally
        {
            _busy = false;
            await RefreshSafelyAsync();
            UpdateActions();
        }
    }

    private void ShowError(Exception exception)
    {
        if (!_opened) return;
        _status.Title = "File action failed";
        _status.Message = exception.Message;
        _status.Severity = InfoBarSeverity.Error;
        _status.IsOpen = true;
        _environment.ReportError("Group files", exception);
    }

    private sealed record FileEntry(SharedFile? File, SharedFolder? Folder)
    {
        public string Id => File?.Id ?? Folder!.Id;
        public string Name => File?.Name ?? Folder!.Name;
    }

    private sealed record FolderChoice(string Id, string Name);
}
