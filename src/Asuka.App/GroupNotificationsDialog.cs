using System.ComponentModel;
using System.Globalization;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Asuka.App;

/// <summary>A bounded, account-scoped view of Milky group notification history.</summary>
public sealed class GroupNotificationsDialog : ContentDialog, IDisposable
{
    private const int PageSize = 20;
    private const string ContextChangedMessage = "The bot account or protocol changed. Reopen group notifications to continue.";
    private readonly AppEnvironment _environment;
    private readonly Window _owner;
    private readonly string? _selfId;
    private readonly ProtocolKind _protocol;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly Stack<long?> _previousCursors = new();
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly ToggleSwitch _filtered = new() { Header = "Notification source", OffContent = "Unfiltered", OnContent = "Filtered" };
    private readonly Button _refresh = new() { Content = "Refresh", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ListView _notifications = new() { SelectionMode = ListViewSelectionMode.Single, Height = 310 };
    private readonly TextBlock _empty = new() { Text = "No group notifications on this page.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly Button _previous = new() { Content = "Previous" };
    private readonly Button _next = new() { Content = "Next" };
    private readonly TextBlock _pageLabel = new() { Text = "Page 1", VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _requestActions = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBox _reason = new() { Header = "Rejection reason", PlaceholderText = "Optional", MaxHeight = 90, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _permission = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly Button _approve = new() { Content = "Approve" };
    private readonly Button _reject = new() { Content = "Reject" };
    private readonly Button _ignore = new() { Content = "Ignore" };
    private Dictionary<string, string> _userNames = new(StringComparer.Ordinal);
    private Dictionary<string, string> _groupNames = new(StringComparer.Ordinal);
    private HashSet<string> _moderatedGroups = new(StringComparer.Ordinal);
    private long? _cursor;
    private long? _nextCursor;
    private bool _loadedFiltered;
    private bool _hasPage;
    private bool _opened;
    private bool _disposed;
    private bool _busy;
    private bool _refreshing;
    private bool _refreshPending;
    private bool _restoringSelection;
    private bool _restoringFilter;
    private int _selectionVersion;

    public GroupNotificationsDialog(AppEnvironment environment, Window owner)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(owner);
        _environment = environment;
        _owner = owner;
        _selfId = environment.BotPersona?.Id;
        _protocol = environment.Preferences.Protocol;
        _token = _lifetime.Token;
        Title = "Group notifications";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        XamlRoot = (owner.Content as FrameworkElement)?.XamlRoot;
        RequestedTheme = WindowChrome.ToElementTheme(environment.Preferences.Theme);
        Resources["ContentDialogMaxWidth"] = 740d;
        var root = new StackPanel { Width = 660, Spacing = 10 };
        root.Children.Add(_status);
        root.Children.Add(new TextBlock
        {
            Text = $"Reviewing as {environment.BotPersona?.DisplayName ?? "No bot selected"} · {_selfId ?? "—"}",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        toolbar.Children.Add(_filtered);
        toolbar.Children.Add(_refresh);
        root.Children.Add(toolbar);
        root.Children.Add(_notifications);
        root.Children.Add(_empty);
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        pages.Children.Add(_previous);
        pages.Children.Add(_pageLabel);
        pages.Children.Add(_next);
        root.Children.Add(pages);
        root.Children.Add(_details);
        _requestActions.Children.Add(_reason);
        _requestActions.Children.Add(_permission);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_approve);
        actions.Children.Add(_reject);
        actions.Children.Add(_ignore);
        _requestActions.Children.Add(actions);
        root.Children.Add(_requestActions);
        Content = new ScrollViewer { Content = root, MaxHeight = 610, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(_notifications, "Group notification history");
        AutomationProperties.SetName(_details, "Selected group notification details");
        AutomationProperties.SetName(_filtered, "Show only filtered or unfiltered group notifications");
        _notifications.SelectionChanged += (_, _) =>
        {
            if (!_restoringSelection) { _selectionVersion++; _reason.Text = string.Empty; }
            UpdateDetails();
            UpdateActions();
        };
        _filtered.Toggled += async (_, _) => { if (!_restoringFilter) await LoadPageAsync(null, PageMove.Reset); };
        _refresh.Click += async (_, _) => await LoadPageAsync(_cursor, PageMove.Stay);
        _next.Click += async (_, _) => { if (_nextCursor is { } next) await LoadPageAsync(next, PageMove.Forward); };
        _previous.Click += async (_, _) => { if (_previousCursors.TryPeek(out var previous)) await LoadPageAsync(previous, PageMove.Backward); };
        _approve.Click += async (_, _) => await ResolveAsync(RequestAction.Approve);
        _reject.Click += async (_, _) => await ResolveAsync(RequestAction.Reject);
        _ignore.Click += async (_, _) => await ResolveAsync(RequestAction.Ignore);
        Opened += async (_, _) =>
        {
            _opened = true;
            environment.Store.Changed += StoreChanged;
            environment.PropertyChanged += EnvironmentPropertyChanged;
            environment.PreferencesChanged += ContextChanged;
            owner.Closed += OwnerClosed;
            await LoadPageAsync(null, PageMove.Reset);
        };
        CloseButtonClick += (_, _) => _lifetime.Cancel();
        Closed += (_, _) => Dispose();
        UpdateActions();
    }

    private GroupNotificationEntry? Selected => (_notifications.SelectedItem as ListViewItem)?.Tag as GroupNotificationEntry;
    private bool ContextMatches => _protocol == ProtocolKind.Milky && _environment.Preferences.Protocol == _protocol
        && _selfId is not null && _environment.BotPersona?.Id == _selfId;

    private void StoreChanged(object? sender, StoreChangedEventArgs args)
    {
        if ((args.Changes & (StoreChangeKind.Notifications | StoreChangeKind.Requests | StoreChangeKind.Members
            | StoreChangeKind.Users | StoreChangeKind.Groups)) != 0) QueueRefresh();
    }

    private void EnvironmentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppEnvironment.BotPersona) or nameof(AppEnvironment.Preferences)) ContextChanged(sender, EventArgs.Empty);
    }

    private void ContextChanged(object? sender, EventArgs args)
    {
        if (!_disposed && !ContextMatches) _lifetime.Cancel();
        QueueRefresh();
    }

    private void OwnerClosed(object sender, WindowEventArgs args) => Dispose();

    private void QueueRefresh() => DispatcherQueue.TryEnqueue(async () =>
    {
        if (!_opened) return;
        _refreshPending = true;
        RequestedTheme = WindowChrome.ToElementTheme(_environment.Preferences.Theme);
        await LoadPageAsync(_cursor, PageMove.Stay);
    });

    private async Task LoadPageAsync(long? requestedCursor, PageMove move)
    {
        if (!_opened) return;
        if (_busy || _refreshing) { _refreshPending = true; return; }
        _refreshing = true;
        UpdateActions();
        try
        {
            do
            {
                _refreshPending = false;
                RequireContext();
                var filtered = _filtered.IsOn;
                var page = await _environment.Platform.GetGroupNotificationsAsync(_selfId!, filtered,
                    startSequence: requestedCursor, limit: PageSize, cancellationToken: _token);
                var userNames = new Dictionary<string, string>(StringComparer.Ordinal);
                var groupNames = new Dictionary<string, string>(StringComparer.Ordinal);
                var moderatedGroups = new HashSet<string>(StringComparer.Ordinal);
                foreach (var groupId in page.Notifications.Select(entry => entry.GroupId).Distinct(StringComparer.Ordinal))
                {
                    var group = await _environment.Store.GetGroupAsync(groupId, _token);
                    groupNames[groupId] = group is null ? $"Group {groupId}" : $"{group.Name} · {groupId}";
                    var member = await _environment.Store.GetMemberAsync(groupId, _selfId!, _token);
                    if (member is { Role: > GroupRole.Member }) moderatedGroups.Add(groupId);
                }
                var userIds = page.Notifications.SelectMany(entry => new[]
                {
                    entry.TargetUserId, entry.OperatorId, entry.Request?.RequesterId, entry.Request?.ResolvedBy,
                }).OfType<string>().Distinct(StringComparer.Ordinal);
                foreach (var userId in userIds)
                    userNames[userId] = (await _environment.Store.GetUserAsync(userId, _token))?.DisplayName ?? userId;
                RequireContext();
                if (filtered != _filtered.IsOn) { requestedCursor = null; move = PageMove.Reset; _refreshPending = true; continue; }
                if (move == PageMove.Forward) _previousCursors.Push(_cursor);
                else if (move == PageMove.Backward && _previousCursors.Count > 0) _previousCursors.Pop();
                else if (move == PageMove.Reset) _previousCursors.Clear();
                _cursor = requestedCursor;
                _nextCursor = page.NextSequence;
                _loadedFiltered = filtered;
                _hasPage = true;
                _userNames = userNames;
                _groupNames = groupNames;
                _moderatedGroups = moderatedGroups;
                var selectedSequence = Selected?.NotificationSequence;
                _restoringSelection = true;
                try
                {
                    _notifications.Items.Clear();
                    foreach (var entry in page.Notifications)
                    {
                        var item = BuildRow(entry);
                        _notifications.Items.Add(item);
                        if (entry.NotificationSequence == selectedSequence) _notifications.SelectedItem = item;
                    }
                }
                finally { _restoringSelection = false; }
                if (Selected?.NotificationSequence != selectedSequence) { _selectionVersion++; _reason.Text = string.Empty; }
                _empty.Visibility = page.Notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                _pageLabel.Text = $"Page {_previousCursors.Count + 1}";
                UpdateDetails();
                move = PageMove.Stay;
                requestedCursor = _cursor;
            } while (_refreshPending && _opened);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            if (_opened && !ContextMatches) ShowContextChanged();
        }
        catch (Exception error)
        {
            _restoringFilter = true;
            try { _filtered.IsOn = _loadedFiltered; }
            finally { _restoringFilter = false; }
            ShowError(error);
        }
        finally { _refreshing = false; UpdateActions(); }
    }

    private ListViewItem BuildRow(GroupNotificationEntry entry)
    {
        var labels = new StackPanel { Spacing = 3, Padding = new Thickness(4) };
        labels.Children.Add(new TextBlock { Text = Describe(entry), TextWrapping = TextWrapping.Wrap, MaxWidth = 610 });
        labels.Children.Add(new TextBlock
        {
            Text = $"{GroupName(entry.GroupId)} · {FormatTime(entry.Time)}"
                + (entry.Request is { } request ? $" · {RequestState(request)}" : string.Empty)
                + (entry.IsFiltered ? " · Filtered" : string.Empty),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });
        var row = new ListViewItem { Tag = entry, Content = labels, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(row, $"{Describe(entry)}, {GroupName(entry.GroupId)}");
        return row;
    }

    private string Describe(GroupNotificationEntry entry) => entry.Kind switch
    {
        GroupNotificationKind.JoinRequest => $"{UserName(entry.TargetUserId)} requested to join the group",
        GroupNotificationKind.InvitedJoinRequest => entry.Request is { } request
            ? $"{UserName(request.RequesterId)} invited {UserName(entry.TargetUserId)} to join the group"
            : $"Invitation for {UserName(entry.TargetUserId)} to join the group",
        GroupNotificationKind.AdminChange => entry.IsSet switch
        {
            true => $"{UserName(entry.TargetUserId)} was made an administrator",
            false => $"{UserName(entry.TargetUserId)} is no longer an administrator",
            _ => $"Administrator role changed for {UserName(entry.TargetUserId)}",
        },
        GroupNotificationKind.Kick => $"{UserName(entry.TargetUserId)} was removed from the group",
        GroupNotificationKind.Quit => $"{UserName(entry.TargetUserId)} left the group",
        _ => "Group notification",
    };

    private void UpdateDetails()
    {
        if (Selected is not { } entry) { _details.Text = "Select a notification to view its details."; return; }
        var lines = new List<string> { Describe(entry), GroupName(entry.GroupId), FormatTime(entry.Time) };
        if (entry.OperatorId is { } operatorId) lines.Add($"Operator: {UserName(operatorId)} · {operatorId}");
        if (entry.Request is { } request)
        {
            lines.Add($"Request: {RequestState(request)}");
            if (request.Comment.Length > 0) lines.Add($"Comment: {request.Comment}");
            if (request.Resolution is { Reason.Length: > 0 } resolution) lines.Add($"Reason: {resolution.Reason}");
        }
        lines.Add($"Notification #{entry.NotificationSequence}");
        _details.Text = string.Join(Environment.NewLine, lines);
    }

    private void UpdateActions()
    {
        if (_disposed) return;
        var context = _opened && ContextMatches && !_token.IsCancellationRequested;
        var ready = context && !_busy && !_refreshing;
        _notifications.IsEnabled = ready && _hasPage;
        _notifications.Visibility = context ? Visibility.Visible : Visibility.Collapsed;
        _filtered.IsEnabled = _refresh.IsEnabled = ready;
        _previous.IsEnabled = ready && _hasPage && _previousCursors.Count > 0;
        _next.IsEnabled = ready && _hasPage && _nextCursor is not null;
        var pending = Selected is { Request: { Resolution: null } } entry
            && entry.Kind is GroupNotificationKind.JoinRequest or GroupNotificationKind.InvitedJoinRequest;
        _requestActions.Visibility = context && pending ? Visibility.Visible : Visibility.Collapsed;
        var moderator = Selected is { } selected && _moderatedGroups.Contains(selected.GroupId);
        _approve.IsEnabled = _reject.IsEnabled = _ignore.IsEnabled = ready && pending && moderator;
        _reason.IsEnabled = ready && pending && moderator;
        _permission.Text = moderator ? string.Empty : "The bot must currently be a group administrator to review this request.";
        _permission.Visibility = pending && !moderator ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ResolveAsync(RequestAction action)
    {
        if (_busy || _refreshing || !_opened || !_approve.IsEnabled
            || Selected is not { Request: { Resolution: null } request } entry) return;
        var version = _selectionVersion;
        var reason = _reason.Text;
        _busy = true;
        _status.IsOpen = false;
        UpdateActions();
        try
        {
            RequireSelection(entry.NotificationSequence, version);
            var current = await _environment.Store.GetRequestAsync(request.Id, _token);
            RequireSelection(entry.NotificationSequence, version);
            if (current is null || current.SelfId != _selfId || current.Flag != request.Flag
                || current.GroupId != entry.GroupId || current.Kind is not (RequestKind.GroupJoin or RequestKind.GroupInvitedJoin))
                throw new InvalidOperationException("This notification no longer refers to an available group request.");
            if (current.Resolution is not null) throw new InvalidOperationException("This request has already been processed.");
            if (action == RequestAction.Ignore)
                await _environment.Platform.IgnoreRequestAsync(current.Flag, _selfId, current.Id, _token);
            else
                await _environment.Platform.ResolveRequestAsync(current.Flag, action == RequestAction.Approve,
                    reason: action == RequestAction.Reject ? reason : string.Empty,
                    expectedSelfId: _selfId, expectedRequestId: current.Id, cancellationToken: _token);
            RequireContext();
            _reason.Text = string.Empty;
            _status.Title = action switch { RequestAction.Approve => "Request approved", RequestAction.Reject => "Request rejected", _ => "Request ignored" };
            _status.Message = string.Empty;
            _status.Severity = InfoBarSeverity.Success;
            _status.IsOpen = true;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            if (_opened && !ContextMatches) ShowContextChanged();
        }
        catch (Exception error) { ShowError(error); }
        finally { _busy = false; await LoadPageAsync(_cursor, PageMove.Stay); UpdateActions(); }
    }

    private void RequireContext()
    {
        _token.ThrowIfCancellationRequested();
        if (!_opened || !ContextMatches) throw new InvalidOperationException(ContextChangedMessage);
    }

    private void RequireSelection(long sequence, int version)
    {
        RequireContext();
        if (_selectionVersion != version || Selected?.NotificationSequence != sequence)
            throw new InvalidOperationException("The selected notification changed. Select it again to continue.");
    }

    private void ShowContextChanged()
    {
        _status.Title = "Group notifications unavailable";
        _status.Message = ContextChangedMessage;
        _status.Severity = InfoBarSeverity.Warning;
        _status.IsOpen = true;
    }

    private void ShowError(Exception error)
    {
        if (!_opened) return;
        _status.Title = "Group notification action failed";
        _status.Message = error.Message;
        _status.Severity = InfoBarSeverity.Error;
        _status.IsOpen = true;
        _environment.ReportError("Group notifications", error);
    }

    private string UserName(string id) => _userNames.GetValueOrDefault(id, id);
    private string GroupName(string id) => _groupNames.GetValueOrDefault(id, $"Group {id}");
    private static string FormatTime(DateTimeOffset time) => time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    private static string RequestState(PendingRequest request) => request.Resolution?.Status switch
    {
        null => "Pending",
        RequestResolutionStatus.Accepted => "Approved",
        RequestResolutionStatus.Rejected => "Rejected",
        RequestResolutionStatus.Ignored => "Ignored",
        _ => "Processed",
    };

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
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private enum PageMove { Stay, Reset, Forward, Backward }
    private enum RequestAction { Approve, Reject, Ignore }
}
