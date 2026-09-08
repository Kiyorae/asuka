using System.ComponentModel;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Asuka.App;

/// <summary>Creates incoming requests for the selected bot through the platform's request lifecycle.</summary>
public sealed class RequestSimulatorDialog : ContentDialog, IDisposable
{
    private const string ContextChangedMessage = "The active bot or protocol changed. Close this dialog and reopen it to continue.";
    private readonly AppEnvironment _environment;
    private readonly string? _selfId;
    private readonly ProtocolKind _protocol;
    private readonly string? _initialActorId;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly StackPanel _form = new() { Spacing = 12 };
    private readonly ComboBox _kind = new() { Header = "Request type", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _group = new() { Header = "Group", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _initiator = new() { Header = "Requester", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _invitee = new() { Header = "Invited persona", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _comment = new() { Header = "Comment", PlaceholderText = "Optional", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 100 };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock _availability = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly Expander _advanced = new() { Header = "Advanced", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _filtered = new() { Content = "Mark as filtered" };
    private readonly TextBox _via = new() { Header = "Request source", Text = "asuka", PlaceholderText = "For example, group or search" };
    private readonly ComboBox _sourceGroup = new() { Header = "Invitation source group", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _refresh = new() { Content = "Refresh choices", HorizontalAlignment = HorizontalAlignment.Left };
    private IReadOnlyList<User> _users = [];
    private IReadOnlyList<GroupChoice> _groups = [];
    private HashSet<string> _friends = new(StringComparer.Ordinal);
    private bool _selfExists;
    private bool _opened;
    private bool _disposed;
    private bool _busy;
    private bool _refreshing;
    private bool _refreshPending;
    private bool _syncing;

    public RequestSimulatorDialog(AppEnvironment environment, Window parent)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(parent);
        _environment = environment;
        _selfId = environment.BotPersona?.Id;
        _protocol = environment.Preferences.Protocol;
        _initialActorId = environment.CurrentPersona?.Id;
        _token = _lifetime.Token;
        XamlRoot = (parent.Content as FrameworkElement)?.XamlRoot;
        RequestedTheme = WindowChrome.ToElementTheme(environment.Preferences.Theme);
        Title = "Simulate request";
        PrimaryButtonText = "Create request";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        var root = new StackPanel { Spacing = 12, Width = 440 };
        root.Children.Add(_status);
        root.Children.Add(new TextBlock
        {
            Text = $"Receiving bot: {environment.BotPersona?.DisplayName ?? "No bot selected"} · {_selfId ?? "—"}",
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(_form);
        _form.Children.Add(_kind);
        _form.Children.Add(_description);
        _form.Children.Add(_group);
        _form.Children.Add(_initiator);
        _form.Children.Add(_invitee);
        _form.Children.Add(_comment);
        var metadata = new StackPanel { Spacing = 10 };
        metadata.Children.Add(_filtered);
        metadata.Children.Add(_via);
        metadata.Children.Add(_sourceGroup);
        _advanced.Content = metadata;
        _form.Children.Add(_advanced);
        _form.Children.Add(_availability);
        _form.Children.Add(_refresh);
        Content = new ScrollViewer { Content = root, MaxHeight = 570, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (ProtocolCapabilities.For(_protocol).Requests)
        {
            AddKind(RequestKind.Friend, "Friend request");
            AddKind(RequestKind.GroupJoin, "Join group request");
            AddKind(RequestKind.GroupInvite, "Invite bot to group");
            if (_protocol == ProtocolKind.Milky)
                AddKind(RequestKind.GroupInvitedJoin, "Invite another persona to group");
            _kind.SelectedIndex = 0;
        }
        AutomationProperties.SetName(_status, "Request creation status");
        AutomationProperties.SetName(_availability, "Available request choices");
        _kind.SelectionChanged += (_, _) => { if (!_syncing) RefreshChoices(); };
        _group.SelectionChanged += (_, _) => { if (!_syncing) RefreshPeople(); };
        _initiator.SelectionChanged += (_, _) => { if (!_syncing) { RefreshSourceGroups(); UpdateActions(); } };
        _invitee.SelectionChanged += (_, _) => UpdateActions();
        _refresh.Click += async (_, _) => await RefreshAsync();
        PrimaryButtonClick += CreateRequestAsync;
        CloseButtonClick += (_, _) => _lifetime.Cancel();
        Opened += async (_, _) =>
        {
            _opened = true;
            environment.Store.Changed += StoreChanged;
            environment.PreferencesChanged += PreferencesChanged;
            environment.PropertyChanged += EnvironmentPropertyChanged;
            await RefreshAsync();
        };
        Closed += (_, _) => Dispose();
        UpdateActions();
    }

    public PendingRequest? CreatedRequest { get; private set; }

    private RequestKind? SelectedKind => (_kind.SelectedItem as ComboBoxItem)?.Tag as RequestKind?;
    private GroupChoice? SelectedGroup => (_group.SelectedItem as ComboBoxItem)?.Tag as GroupChoice;
    private User? SelectedInitiator => (_initiator.SelectedItem as ComboBoxItem)?.Tag as User;
    private User? SelectedInvitee => (_invitee.SelectedItem as ComboBoxItem)?.Tag as User;
    private string? SelectedSourceGroupId => ((_sourceGroup.SelectedItem as ComboBoxItem)?.Tag as GroupChoice)?.Group.Id;
    private bool ContextMatches => _selfId is not null && _environment.BotPersona?.Id == _selfId
        && _environment.Preferences.Protocol == _protocol && _environment.Capabilities.Requests;

    private void AddKind(RequestKind kind, string label) => _kind.Items.Add(new ComboBoxItem { Content = label, Tag = kind });

    private void StoreChanged(object? sender, StoreChangedEventArgs args)
    {
        if ((args.Changes & (StoreChangeKind.Users | StoreChangeKind.Groups | StoreChangeKind.Members | StoreChangeKind.Friendships)) != 0)
            QueueRefresh();
    }

    private void EnvironmentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppEnvironment.BotPersona) or nameof(AppEnvironment.Preferences))
        {
            if (!_disposed && !ContextMatches) _lifetime.Cancel();
            QueueRefresh();
        }
    }

    private void PreferencesChanged(object? sender, EventArgs args)
    {
        if (!_disposed && !ContextMatches) _lifetime.Cancel();
        QueueRefresh();
    }

    private void QueueRefresh() => DispatcherQueue.TryEnqueue(async () =>
    {
        if (!_opened) return;
        _refreshPending = true;
        if (!ContextMatches) _lifetime.Cancel();
        await RefreshAsync();
    });

    private async Task RefreshAsync()
    {
        if (!_opened || _busy || _refreshing) return;
        if (!ContextMatches || _token.IsCancellationRequested)
        {
            ShowUnavailable(!ProtocolCapabilities.For(_protocol).Requests
                ? "This protocol does not support requests."
                : _selfId is null ? "Select a bot account before creating requests."
                : ContextChangedMessage);
            return;
        }
        _refreshing = true;
        UpdateActions();
        try
        {
            do
            {
                _refreshPending = false;
                RequireCurrent();
                var users = await _environment.Store.GetAllUsersAsync(_token);
                var groups = await _environment.Store.GetAllGroupsAsync(_token);
                var friendships = await _environment.Store.GetFriendshipsAsync(_selfId!, _token);
                var choices = new List<GroupChoice>();
                foreach (var group in groups)
                {
                    var members = await _environment.Store.GetMembersAsync(group.Id, _token);
                    choices.Add(new GroupChoice(group,
                        members.Select(member => member.UserId).ToHashSet(StringComparer.Ordinal),
                        members.Any(member => member.UserId == _selfId && member.Role > GroupRole.Member)));
                }
                RequireCurrent();
                _users = users;
                _groups = choices;
                _friends = friendships.Select(friendship => friendship.FriendId).ToHashSet(StringComparer.Ordinal);
                _selfExists = users.Any(user => user.Id == _selfId);
                RefreshChoices();
            } while (_refreshPending && _opened);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            if (_opened && !ContextMatches) ShowUnavailable(ContextChangedMessage);
        }
        catch (Exception error) { _selfExists = false; ShowError(error); }
        finally { _refreshing = false; UpdateActions(); }
    }

    private void RefreshChoices()
    {
        _syncing = true;
        try
        {
            var kind = SelectedKind;
            _description.Text = kind switch
            {
                RequestKind.Friend => "A persona asks to add the bot as a friend.",
                RequestKind.GroupJoin => "A persona asks to join a group moderated by the bot.",
                RequestKind.GroupInvite => "A group member invites the bot to join their group.",
                RequestKind.GroupInvitedJoin => "A member invites another persona. The bot reviews the invitation as a group moderator.",
                _ => "This protocol does not support requests.",
            };
            _group.Visibility = kind == RequestKind.Friend ? Visibility.Collapsed : Visibility.Visible;
            _invitee.Visibility = kind == RequestKind.GroupInvitedJoin ? Visibility.Visible : Visibility.Collapsed;
            _initiator.Header = kind is RequestKind.GroupInvite or RequestKind.GroupInvitedJoin ? "Inviter" : "Requester";
            _comment.Visibility = kind is RequestKind.Friend or RequestKind.GroupJoin
                || (kind == RequestKind.GroupInvite && _protocol == ProtocolKind.OneBotV11)
                    ? Visibility.Visible : Visibility.Collapsed;
            _advanced.Visibility = _protocol == ProtocolKind.Milky ? Visibility.Visible : Visibility.Collapsed;
            _filtered.Visibility = kind == RequestKind.GroupInvite ? Visibility.Collapsed : Visibility.Visible;
            _via.Visibility = kind == RequestKind.Friend ? Visibility.Visible : Visibility.Collapsed;
            _sourceGroup.Visibility = kind == RequestKind.GroupInvite ? Visibility.Visible : Visibility.Collapsed;
            var groupId = SelectedGroup?.Group.Id;
            _group.Items.Clear();
            foreach (var choice in _groups.Where(choice => kind == RequestKind.GroupInvite
                ? !choice.Members.Contains(_selfId!) : choice.BotIsModerator))
            {
                var item = new ComboBoxItem { Content = $"{choice.Group.Name} · {choice.Group.Id}", Tag = choice };
                _group.Items.Add(item);
                if (choice.Group.Id == groupId) _group.SelectedItem = item;
            }
            if (_group.SelectedItem is null && _group.Items.Count > 0) _group.SelectedIndex = 0;
        }
        finally { _syncing = false; }
        RefreshPeople();
    }

    private void RefreshPeople()
    {
        _syncing = true;
        try
        {
            var kind = SelectedKind;
            var group = SelectedGroup;
            var initiatorId = SelectedInitiator?.Id ?? _initialActorId;
            var inviteeId = SelectedInvitee?.Id;
            _initiator.Items.Clear();
            _invitee.Items.Clear();
            foreach (var user in _users.Where(user => user.Id != _selfId))
            {
                var eligible = kind switch
                {
                    RequestKind.Friend => !_friends.Contains(user.Id),
                    RequestKind.GroupJoin => group is not null && !group.Members.Contains(user.Id),
                    RequestKind.GroupInvite or RequestKind.GroupInvitedJoin => group?.Members.Contains(user.Id) == true,
                    _ => false,
                };
                if (eligible) AddUser(_initiator, user, initiatorId);
                if (kind == RequestKind.GroupInvitedJoin && group is not null && !group.Members.Contains(user.Id))
                    AddUser(_invitee, user, inviteeId);
            }
            if (_initiator.SelectedItem is null && _initiator.Items.Count > 0) _initiator.SelectedIndex = 0;
            if (_invitee.SelectedItem is null && _invitee.Items.Count > 0) _invitee.SelectedIndex = 0;
        }
        finally { _syncing = false; }
        RefreshSourceGroups();
        UpdateActions();
    }

    private static void AddUser(ComboBox box, User user, string? selectedId)
    {
        var item = new ComboBoxItem { Content = $"{user.DisplayName} · {user.Id}", Tag = user };
        box.Items.Add(item);
        if (user.Id == selectedId) box.SelectedItem = item;
    }

    private void RefreshSourceGroups()
    {
        var selectedId = SelectedSourceGroupId;
        var initiatorId = SelectedInitiator?.Id;
        _sourceGroup.Items.Clear();
        _sourceGroup.Items.Add(new ComboBoxItem { Content = "None" });
        foreach (var group in _groups.Where(group => initiatorId is not null
            && group.Members.Contains(initiatorId) && group.Members.Contains(_selfId!)))
        {
            var item = new ComboBoxItem { Content = $"{group.Group.Name} · {group.Group.Id}", Tag = group };
            _sourceGroup.Items.Add(item);
            if (group.Group.Id == selectedId) _sourceGroup.SelectedItem = item;
        }
        if (_sourceGroup.SelectedItem is null) _sourceGroup.SelectedIndex = 0;
    }

    private void UpdateActions()
    {
        if (_disposed) return;
        var active = _opened && ContextMatches && !_token.IsCancellationRequested;
        _form.Visibility = ContextMatches ? Visibility.Visible : Visibility.Collapsed;
        var enabled = active && _selfExists && !_busy && !_refreshing;
        _kind.IsEnabled = enabled;
        _group.IsEnabled = enabled;
        _initiator.IsEnabled = enabled;
        _invitee.IsEnabled = enabled;
        _comment.IsEnabled = enabled;
        _advanced.IsEnabled = enabled;
        _refresh.IsEnabled = active && !_busy && !_refreshing;
        IsPrimaryButtonEnabled = enabled && SelectedInitiator is not null && SelectedKind is { } kind
            && (kind == RequestKind.Friend || SelectedGroup is not null)
            && (kind != RequestKind.GroupInvitedJoin || SelectedInvitee is not null);
        _availability.Text = _refreshing ? "Loading available personas and groups…"
            : !_selfExists ? "Select an existing bot account before creating requests."
            : SelectedKind != RequestKind.Friend && SelectedGroup is null
                ? SelectedKind == RequestKind.GroupInvite ? "Create a group that the bot has not joined."
                    : "The bot needs an owner or administrator role in a group to review requests."
            : SelectedInitiator is null ? "No eligible initiator. Create another persona or update the group membership."
            : SelectedKind == RequestKind.GroupInvitedJoin && SelectedInvitee is null
                ? "Create a persona who has not joined the selected group."
            : string.Empty;
        _availability.Visibility = _availability.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void CreateRequestAsync(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (!IsPrimaryButtonEnabled || _busy) return;
        var deferral = args.GetDeferral();
        _busy = true;
        _status.IsOpen = false;
        UpdateActions();
        try
        {
            RequireCurrent();
            var actor = SelectedInitiator ?? throw new InvalidOperationException("Choose an initiator.");
            var filtered = _protocol == ProtocolKind.Milky && _filtered.IsChecked == true;
            CreatedRequest = SelectedKind switch
            {
                RequestKind.Friend => await _environment.Platform.RequestFriendAsync(actor.Id, _selfId!,
                    _comment.Text, filtered, _protocol == ProtocolKind.Milky ? _via.Text : "asuka", _token),
                RequestKind.GroupJoin => await _environment.Platform.RequestJoinGroupAsync(SelectedGroup!.Group.Id,
                    actor.Id, _selfId!, _comment.Text, filtered, _token),
                RequestKind.GroupInvite => await _environment.Platform.InviteToGroupAsync(SelectedGroup!.Group.Id,
                    actor.Id, _selfId!, _protocol == ProtocolKind.OneBotV11 ? _comment.Text : string.Empty,
                    _protocol == ProtocolKind.Milky ? SelectedSourceGroupId : null, _token),
                RequestKind.GroupInvitedJoin when _protocol == ProtocolKind.Milky =>
                    await _environment.Platform.RequestInvitedJoinGroupAsync(SelectedGroup!.Group.Id,
                        actor.Id, SelectedInvitee!.Id, _selfId!, filtered, _token),
                _ => throw new InvalidOperationException("This protocol does not support the selected request."),
            };
            args.Cancel = false;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            if (_opened && !ContextMatches) ShowUnavailable(ContextChangedMessage);
        }
        catch (Exception error) { ShowError(error); }
        finally
        {
            _busy = false;
            UpdateActions();
            deferral.Complete();
            if (CreatedRequest is null && _opened && !_token.IsCancellationRequested) await RefreshAsync();
        }
    }

    private void RequireCurrent()
    {
        _token.ThrowIfCancellationRequested();
        if (!_opened || !ContextMatches) throw new InvalidOperationException("The active bot or protocol changed. Reopen the dialog to continue.");
    }

    private void ShowUnavailable(string message)
    {
        _status.Title = "Request creation unavailable";
        _status.Message = message;
        _status.Severity = InfoBarSeverity.Warning;
        _status.IsOpen = true;
        UpdateActions();
    }

    private void ShowError(Exception error)
    {
        if (!_opened) return;
        _status.Title = "Could not create request";
        _status.Message = error.Message;
        _status.Severity = InfoBarSeverity.Error;
        _status.IsOpen = true;
        _environment.ReportError("Request creation failed", error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _opened = false;
        _environment.Store.Changed -= StoreChanged;
        _environment.PreferencesChanged -= PreferencesChanged;
        _environment.PropertyChanged -= EnvironmentPropertyChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record GroupChoice(Group Group, HashSet<string> Members, bool BotIsModerator);
}
