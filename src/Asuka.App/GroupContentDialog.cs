using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;
using User = Asuka.Core.User;

namespace Asuka.App;

/// <summary>Milky group announcements, essence messages and avatar, backed by the same platform as the API.</summary>
public sealed class GroupContentDialog : ContentDialog, IDisposable
{
    private const int PageSize = 20;
    private readonly AppEnvironment _environment;
    private readonly Group _group;
    private readonly User _operator;
    private readonly Window _owner;
    private readonly string? _selfId;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private readonly Button _avatar = new() { Content = "Change group avatar" };
    private readonly ListView _announcements = new() { Height = 165, SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBlock _announcementDetails = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly Image _announcementImage = new() { MaxHeight = 150, MaxWidth = 620, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly TextBlock _announcementEmpty = new() { Text = "No announcements yet.", Opacity = 0.7 };
    private readonly TextBox _content = new() { Header = "New announcement", PlaceholderText = "Share an update with the group", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 95 };
    private readonly Button _attach = new() { Content = "Attach image" };
    private readonly Button _clearImage = new() { Content = "Remove image" };
    private readonly TextBlock _imageName = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 270, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _send = new() { Content = "Post announcement" };
    private readonly Button _delete = new() { Content = "Delete announcement" };
    private readonly StackPanel _announcementEditor = new() { Spacing = 8 };
    private readonly ListView _essence = new() { Height = 270, SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBlock _essenceDetails = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _essenceEmpty = new() { Text = "No essence messages on this page. Use a message’s menu to add one.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    private readonly Button _previous = new() { Content = "Previous" };
    private readonly Button _next = new() { Content = "Next" };
    private readonly Button _removeEssence = new() { Content = "Remove from essence" };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _confirmation = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _confirmationText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _confirm = new() { Content = "Delete" };
    private GroupMember? _member;
    private GroupAnnouncement? _pendingDelete;
    private Asset? _image;
    private bool _opened;
    private bool _disposed;
    private bool _busy;
    private bool _refreshing;
    private bool _refreshNeeded;
    private bool _restoringAnnouncementSelection;
    private bool _isEnd = true;
    private int _page;

    public GroupContentDialog(AppEnvironment environment, Group group, User @operator, Window parent)
    {
        _environment = environment;
        _group = group;
        _operator = @operator;
        _owner = parent;
        _selfId = environment.BotPersona?.Id;
        _token = _lifetime.Token;
        Title = $"Group content · {group.Name}";
        MinWidth = 720;
        Resources["ContentDialogMaxWidth"] = 740d;
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        Content = BuildContent();
        Opened += async (_, _) =>
        {
            _opened = true;
            environment.Store.Changed += StoreChanged;
            environment.PreferencesChanged += PreferencesChanged;
            await RefreshAsync();
        };
        Closed += (_, _) => Dispose();
        _refresh.Click += async (_, _) => await RefreshAsync();
        _avatar.Click += async (_, _) => await RunAsync(async () =>
        {
            if (await PickImageAsync() is not { } image) return;
            await environment.Platform.SetGroupAvatarAsync(group.Id, @operator.Id,
                new Uri(environment.Assets.LocationOf(image.Id)).AbsoluteUri, _token);
        });
        _attach.Click += async (_, _) => await RunAsync(async () =>
        {
            if (await PickImageAsync() is { } image) { _image = image; _imageName.Text = image.Name; }
        });
        _clearImage.Click += (_, _) => { _image = null; _imageName.Text = string.Empty; UpdateActions(); };
        _send.Click += async (_, _) => await RunAsync(async () =>
        {
            await environment.Platform.SendGroupAnnouncementAsync(group.Id, @operator.Id, _content.Text, _image, _token);
            _content.Text = string.Empty;
            _image = null;
            _imageName.Text = string.Empty;
        });
        _content.TextChanged += (_, _) => UpdateActions();
        _announcements.SelectionChanged += (_, _) =>
        {
            if (_restoringAnnouncementSelection) return;
            CancelDelete();
            UpdateDetails();
        };
        _essence.SelectionChanged += (_, _) => UpdateDetails();
        _delete.Click += (_, _) =>
        {
            _pendingDelete = SelectedAnnouncement;
            _confirmationText.Text = "Delete the selected announcement?";
            _confirmation.Visibility = _pendingDelete is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateActions();
        };
        _confirm.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_pendingDelete is not { } announcement) return;
            await environment.Platform.DeleteGroupAnnouncementAsync(group.Id, announcement.Id, @operator.Id, _token);
            CancelDelete();
        });
        _removeEssence.Click += async (_, _) => await RunAsync(async () =>
        {
            if (SelectedEssence is not { } essence || _selfId is null) return;
            await environment.Platform.SetGroupEssenceMessageAsync(group.Id, essence.Message.Seq, _selfId, @operator.Id, false, _token);
        });
        _previous.Click += async (_, _) => { if (_page > 0 && !_busy && !_refreshing) { _page--; await RefreshAsync(); } };
        _next.Click += async (_, _) => { if (!_isEnd && !_busy && !_refreshing) { _page++; await RefreshAsync(); } };
        UpdateActions();
    }

    private GroupAnnouncement? SelectedAnnouncement => (_announcements.SelectedItem as ListViewItem)?.Tag as GroupAnnouncement;
    private GroupEssenceMessage? SelectedEssence => (_essence.SelectedItem as ListViewItem)?.Tag as GroupEssenceMessage;
    private bool IsActive => _opened && _environment.Preferences.Protocol == ProtocolKind.Milky
        && _environment.CurrentPersona?.Id == _operator.Id && _environment.BotPersona?.Id == _selfId;

    private ScrollViewer BuildContent()
    {
        var root = new StackPanel { Width = 660, Spacing = 10 };
        root.Children.Add(_status);
        root.Children.Add(new TextBlock { Text = $"Acting as {_operator.DisplayName}", Opacity = 0.7 });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(_avatar);
        toolbar.Children.Add(_refresh);
        root.Children.Add(toolbar);
        var tabs = new Pivot();
        var notices = new StackPanel { Spacing = 10 };
        notices.Children.Add(_announcements);
        notices.Children.Add(_announcementEmpty);
        notices.Children.Add(_announcementDetails);
        notices.Children.Add(_announcementImage);
        notices.Children.Add(_delete);
        _announcementEditor.Children.Add(_content);
        var attachments = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        attachments.Children.Add(_attach);
        attachments.Children.Add(_imageName);
        attachments.Children.Add(_clearImage);
        _announcementEditor.Children.Add(attachments);
        _announcementEditor.Children.Add(_send);
        notices.Children.Add(_announcementEditor);
        _confirmation.Children.Add(_confirmationText);
        var confirmActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        confirmActions.Children.Add(_confirm);
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => CancelDelete();
        confirmActions.Children.Add(cancel);
        _confirmation.Children.Add(confirmActions);
        notices.Children.Add(_confirmation);
        tabs.Items.Add(new PivotItem { Header = "Announcements", Content = notices });
        var essence = new StackPanel { Spacing = 10 };
        essence.Children.Add(_essence);
        essence.Children.Add(_essenceEmpty);
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pages.Children.Add(_previous);
        pages.Children.Add(_pageLabel);
        pages.Children.Add(_next);
        essence.Children.Add(pages);
        essence.Children.Add(_essenceDetails);
        essence.Children.Add(_removeEssence);
        tabs.Items.Add(new PivotItem { Header = "Essence messages", Content = essence });
        root.Children.Add(tabs);
        AutomationProperties.SetName(_announcements, "Group announcements");
        AutomationProperties.SetName(_essence, "Group essence messages");
        AutomationProperties.SetName(_announcementImage, "Selected announcement image");
        return new ScrollViewer { Content = root, MaxHeight = 650, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void StoreChanged(object? sender, StoreChangedEventArgs args)
    {
        if ((args.Changes & (StoreChangeKind.Groups | StoreChangeKind.Members | StoreChangeKind.Messages | StoreChangeKind.Users)) != 0)
            QueueRefresh();
    }
    private void PreferencesChanged(object? sender, EventArgs args) => QueueRefresh();
    private void QueueRefresh() => DispatcherQueue.TryEnqueue(async () => { _refreshNeeded = true; await RefreshAsync(); });

    private async Task RefreshAsync()
    {
        if (!_opened || _busy || _refreshing) return;
        _refreshing = true;
        UpdateActions();
        try
        {
            do
            {
                _refreshNeeded = false;
                RequireActive();
                _member = await _environment.Store.GetMemberAsync(_group.Id, _operator.Id, _token);
                var group = await _environment.Store.GetGroupAsync(_group.Id, _token);
                Title = $"Group content · {group?.Name ?? _group.Name}";
                var announcements = await _environment.Platform.GetGroupAnnouncementsAsync(_group.Id, _operator.Id, _token);
                var essence = _selfId is null ? new GroupEssencePage([], true)
                    : await _environment.Platform.GetGroupEssenceMessagesAsync(_group.Id, _operator.Id, _page, PageSize, _selfId, _token);
                if (essence.Messages.Count == 0 && _page > 0) { _page = 0; _refreshNeeded = true; continue; }
                RequireActive();
                var announcementId = SelectedAnnouncement?.Id;
                var essenceId = SelectedEssence?.Message.Id;
                _restoringAnnouncementSelection = true;
                try
                {
                    _announcements.Items.Clear();
                    foreach (var announcement in announcements)
                    {
                        var item = Row(announcement.Content, $"{announcement.Time.LocalDateTime:g} · {announcement.UserId}", announcement);
                        _announcements.Items.Add(item);
                        if (announcement.Id == announcementId) _announcements.SelectedItem = item;
                    }
                }
                finally { _restoringAnnouncementSelection = false; }
                if (_pendingDelete is { } pending)
                {
                    if (SelectedAnnouncement?.Id == pending.Id) _pendingDelete = SelectedAnnouncement;
                    else CancelDelete();
                }
                _essence.Items.Clear();
                foreach (var entry in essence.Messages)
                {
                    var text = entry.Message.Content.TextPreview();
                    var item = Row(string.IsNullOrWhiteSpace(text) ? "Media message" : text,
                        $"{entry.SenderName} · {entry.Message.Time.LocalDateTime:g} · #{entry.Message.Seq}", entry);
                    _essence.Items.Add(item);
                    if (entry.Message.Id == essenceId) _essence.SelectedItem = item;
                }
                _isEnd = essence.IsEnd;
                _pageLabel.Text = $"Page {_page + 1}";
                _announcementEmpty.Visibility = announcements.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                _essenceEmpty.Visibility = essence.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateDetails();
            } while (_refreshNeeded && _opened);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _member = null;
            _announcements.Items.Clear();
            _essence.Items.Clear();
            UpdateDetails();
            ShowError(error);
        }
        finally { _refreshing = false; UpdateActions(); }
    }

    private static ListViewItem Row(string text, string caption, object value)
    {
        var labels = new StackPanel { Spacing = 3, Padding = new Thickness(4) };
        labels.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 590 });
        labels.Children.Add(new TextBlock { Text = caption, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 590 });
        return new ListViewItem { Content = labels, Tag = value, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private void UpdateDetails()
    {
        _announcementDetails.Text = SelectedAnnouncement?.Content ?? string.Empty;
        _announcementImage.Source = null;
        _announcementImage.Visibility = Visibility.Collapsed;
        if (SelectedAnnouncement?.Image is { } image && _environment.Assets.Exists(image.Id))
        {
            _announcementImage.Source = new BitmapImage(new Uri(_environment.Assets.LocationOf(image.Id)));
            _announcementImage.Visibility = Visibility.Visible;
        }
        _essenceDetails.Text = SelectedEssence is { } essence
            ? $"Added by {essence.OperatorName} · {essence.OperationTime.LocalDateTime:g}\n{essence.Message.Content.TextPreview()}" : string.Empty;
        UpdateActions();
    }

    private void UpdateActions()
    {
        var enabled = IsActive && !_busy && !_refreshing && _member is not null;
        var admin = _member is { Role: > GroupRole.Member } && IsActive;
        _avatar.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        _announcementEditor.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        _delete.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        _removeEssence.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        _avatar.IsEnabled = enabled && admin;
        _refresh.IsEnabled = IsActive && !_busy && !_refreshing;
        _attach.IsEnabled = enabled && admin;
        _clearImage.IsEnabled = enabled && admin && _image is not null;
        _content.IsEnabled = enabled && admin;
        _send.IsEnabled = enabled && admin && (!string.IsNullOrWhiteSpace(_content.Text) || _image is not null);
        _delete.IsEnabled = enabled && admin && SelectedAnnouncement is not null;
        _confirm.IsEnabled = enabled && admin && _pendingDelete is not null;
        _removeEssence.IsEnabled = enabled && admin && SelectedEssence is not null;
        _previous.IsEnabled = enabled && _page > 0;
        _next.IsEnabled = enabled && !_isEnd;
    }

    private async Task<Asset?> PickImageAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }) picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));
        var source = await picker.PickSingleFileAsync();
        if (source is null) return null;
        RequireActive();
        using (var stream = await source.OpenReadAsync()) { _ = await BitmapDecoder.CreateAsync(stream); }
        var attachment = await _environment.CreateAttachmentAsync(source, _token);
        RequireActive();
        return attachment.Asset;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _refreshing || !_opened) return;
        _busy = true;
        _status.IsOpen = false;
        UpdateActions();
        try { RequireActive(); await action(); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
        finally { _busy = false; await RefreshAsync(); UpdateActions(); }
    }

    private void RequireActive()
    {
        _token.ThrowIfCancellationRequested();
        if (!IsActive) throw new InvalidOperationException("The active persona or protocol changed. Reopen group content to continue.");
    }
    private void CancelDelete() { _pendingDelete = null; _confirmation.Visibility = Visibility.Collapsed; UpdateActions(); }
    private void ShowError(Exception error)
    {
        if (!_opened) return;
        _status.Title = "Group action failed";
        _status.Message = error.Message;
        _status.Severity = InfoBarSeverity.Error;
        _status.IsOpen = true;
        _environment.ReportError("Group content", error);
    }

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
}
