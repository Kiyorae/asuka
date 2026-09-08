using System.ComponentModel;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Asuka.App;

public sealed class GroupManagementDialog : ContentDialog, IDisposable
{
    private readonly AppEnvironment _environment;
    private readonly User _operator;
    private readonly ProtocolCapabilities _capabilities;
    private readonly string? _contextBotId;
    private readonly Chat? _contextSelectedChat;
    private readonly CancellationTokenSource _anonymousLifetime = new();
    private readonly CancellationToken _anonymousCancellationToken;
    private readonly ListView _rosterList = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 260 };
    private readonly ComboBox _candidateBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _groupNameBox = new();
    private readonly ToggleSwitch _wholeMuteSwitch = new();
    private readonly ToggleSwitch _anonymousSwitch = new();
    private readonly TextBox _cardBox = new() { PlaceholderText = "Selected member card" };
    private readonly TextBox _titleBox = new() { PlaceholderText = "Selected member title" };
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly TextBlock _actingAs = new();
    private readonly Button _renameButton = new() { Content = "Rename", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _applyIdentityButton = new() { Content = "Apply profile", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _toggleAdminButton = new() { Content = "Make admin" };
    private readonly Button _muteButton = new() { Content = "Mute 10 min" };
    private readonly Button _unmuteButton = new() { Content = "Unmute" };
    private readonly Button _removeButton = new() { Content = "Remove" };
    private readonly Button _nudgeButton = new() { Content = "Nudge" };
    private readonly Button _addButton = new() { Content = "Simulate join", VerticalAlignment = VerticalAlignment.Bottom };
    private Group _group;
    private GroupMember? _actor;
    private bool _busy;
    private bool _syncingControls;
    private bool _closed;

    private GroupManagementPolicy Policy => new(_capabilities, _actor, (_rosterList.SelectedItem as MemberItem)?.Member);

    public GroupManagementDialog(AppEnvironment environment, Group group, User @operator)
    {
        _environment = environment;
        _group = group;
        _operator = @operator;
        _capabilities = ProtocolCapabilities.For(environment.Preferences.Protocol);
        _contextBotId = environment.BotPersona?.Id;
        _contextSelectedChat = environment.SelectedChat;
        _anonymousCancellationToken = _anonymousLifetime.Token;
        Title = $"Members · {group.Name}";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        MinWidth = 680;
        Content = BuildContent();
        Opened += Dialog_Opened;
        Closed += Dialog_Closed;
        _rosterList.SelectionChanged += RosterList_SelectionChanged;
        _candidateBox.SelectionChanged += (_, _) => UpdateActionState();
        _groupNameBox.TextChanged += (_, _) => UpdateActionState();
        _cardBox.TextChanged += (_, _) => UpdateActionState();
        _titleBox.TextChanged += (_, _) => UpdateActionState();
        UpdateActionState();
    }

    private ScrollViewer BuildContent()
    {
        var root = new StackPanel { Spacing = 12, Width = 650 };
        _status.IsOpen = false;
        root.Children.Add(_status);

        var groupHeader = new TextBlock
        {
            Text = "Group controls",
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        root.Children.Add(groupHeader);
        root.Children.Add(_actingAs);

        var groupGrid = new Grid { ColumnSpacing = 8 };
        groupGrid.ColumnDefinitions.Add(new ColumnDefinition());
        if (_capabilities.MemberModeration)
        {
            groupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        groupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _groupNameBox.Header = "Group name";
        _groupNameBox.Text = _group.Name;
        groupGrid.Children.Add(_groupNameBox);
        _renameButton.Click += RenameButton_Click;
        Grid.SetColumn(_renameButton, 1);
        groupGrid.Children.Add(_renameButton);
        _wholeMuteSwitch.Header = "Mute all";
        _wholeMuteSwitch.IsOn = _group.WholeMuted;
        _wholeMuteSwitch.Toggled += WholeMuteSwitch_Toggled;
        Grid.SetColumn(_wholeMuteSwitch, 2);
        if (_capabilities.MemberModeration)
        {
            groupGrid.Children.Add(_wholeMuteSwitch);
        }
        root.Children.Add(groupGrid);
        if (_capabilities.AnonymousMessages)
        {
            _anonymousSwitch.Header = "Anonymous messages";
            _anonymousSwitch.IsOn = _group.AnonymousEnabled;
            _anonymousSwitch.Toggled += AnonymousSwitch_Toggled;
            root.Children.Add(_anonymousSwitch);
        }

        root.Children.Add(CreateDivider());
        root.Children.Add(new TextBlock
        {
            Text = "Member roster",
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        root.Children.Add(_rosterList);

        var editorGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        editorGrid.ColumnDefinitions.Add(new ColumnDefinition());
        editorGrid.ColumnDefinitions.Add(new ColumnDefinition());
        editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editorGrid.Children.Add(_cardBox);
        Grid.SetColumn(_titleBox, 1);
        editorGrid.Children.Add(_titleBox);
        _applyIdentityButton.Click += ApplyIdentityButton_Click;
        Grid.SetColumn(_applyIdentityButton, 2);
        editorGrid.Children.Add(_applyIdentityButton);
        if (_capabilities.MemberProfiles)
        {
            root.Children.Add(editorGrid);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _toggleAdminButton.Click += ToggleAdminButton_Click;
        _muteButton.Click += MuteButton_Click;
        _unmuteButton.Click += UnmuteButton_Click;
        _removeButton.Click += RemoveButton_Click;
        _nudgeButton.Click += NudgeButton_Click;
        if (_capabilities.MemberModeration)
        {
            actions.Children.Add(_toggleAdminButton);
            actions.Children.Add(_muteButton);
            actions.Children.Add(_unmuteButton);
        }
        actions.Children.Add(_removeButton);
        if (_capabilities.SupportsNudges(ChatScene.Group))
        {
            actions.Children.Add(_nudgeButton);
        }
        if (actions.Children.Count > 0)
        {
            root.Children.Add(actions);
        }

        root.Children.Add(CreateDivider());
        var addGrid = new Grid { ColumnSpacing = 8 };
        addGrid.ColumnDefinitions.Add(new ColumnDefinition());
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _candidateBox.Header = "Simulate a member joining";
        _candidateBox.DisplayMemberPath = nameof(PersonaItem.Description);
        addGrid.Children.Add(_candidateBox);
        _addButton.Click += AddButton_Click;
        Grid.SetColumn(_addButton, 1);
        addGrid.Children.Add(_addButton);
        root.Children.Add(addGrid);

        return new ScrollViewer
        {
            Content = root,
            MaxHeight = 620,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static Border CreateDivider() => new()
    {
        Height = 1,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(40, 128, 128, 128)),
    };

    private async Task RefreshAsync()
    {
        try
        {
            var selectedId = (_rosterList.SelectedItem as MemberItem)?.Member.UserId;
            var roster = await _environment.GetRosterAsync(_group.Id);
            _group = await _environment.Store.GetGroupAsync(_group.Id) ?? _group;
            _actor = roster.FirstOrDefault(item => item.Member.UserId == _operator.Id)?.Member;
            var memberIds = roster.Select(item => item.Member.UserId).ToHashSet(StringComparer.Ordinal);
            var members = roster.Select(item => new MemberItem(item)).ToList();
            _rosterList.ItemsSource = members;
            _rosterList.SelectedItem = members.FirstOrDefault(item => item.Member.UserId == selectedId);
            _candidateBox.ItemsSource = _environment.Personas.Where(persona => !memberIds.Contains(persona.Id)).ToList();
            _candidateBox.SelectedIndex = _candidateBox.Items.Count > 0 ? 0 : -1;
            _groupNameBox.Text = _group.Name;
            Title = $"Members · {_group.Name}";
            SyncGroupSwitches();
            _status.IsOpen = false;
            UpdateActionState();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void RosterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = _rosterList.SelectedItem as MemberItem;
        _cardBox.Text = selected?.Member.Card ?? "";
        _titleBox.Text = selected?.Member.Title ?? "";
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        var policy = Policy;
        var selected = policy.Target;
        _actingAs.Text = $"Acting as {_operator.DisplayName} · {_actor?.Role.ToString() ?? "Not a member"}";
        _rosterList.IsEnabled = !_busy;
        _candidateBox.IsEnabled = !_busy;
        _addButton.IsEnabled = !_busy && _actor is not null && _candidateBox.SelectedItem is PersonaItem;
        _groupNameBox.IsEnabled = !_busy && policy.CanRename;
        _renameButton.IsEnabled = !_busy && policy.CanRename
            && !string.IsNullOrWhiteSpace(_groupNameBox.Text) && _groupNameBox.Text.Trim() != _group.Name;
        _wholeMuteSwitch.IsEnabled = !_busy && policy.CanSetWholeMute;
        _anonymousSwitch.IsEnabled = !_busy && !_anonymousCancellationToken.IsCancellationRequested
            && HasCurrentAnonymousContext() && policy.CanSetAnonymous;
        _cardBox.IsEnabled = !_busy && policy.CanEditCard;
        _titleBox.IsEnabled = !_busy && policy.CanEditTitle;
        _applyIdentityButton.IsEnabled = !_busy && policy.GetProfileChanges(_cardBox.Text, _titleBox.Text).HasChanges;
        _toggleAdminButton.IsEnabled = !_busy && policy.CanManageAdmin;
        _toggleAdminButton.Content = selected?.Role == GroupRole.Admin ? "Remove admin" : "Make admin";
        _muteButton.IsEnabled = !_busy && policy.CanMute;
        _unmuteButton.IsEnabled = !_busy && policy.CanMute && selected?.MutedUntil > DateTimeOffset.UtcNow;
        _removeButton.IsEnabled = !_busy && policy.CanRemove;
        _removeButton.Visibility = _capabilities.MemberModeration || policy.IsSelf ? Visibility.Visible : Visibility.Collapsed;
        _removeButton.Content = policy.IsSelf ? "Leave group" : "Remove";
        _nudgeButton.IsEnabled = !_busy && policy.CanNudge;

        ToolTipService.SetToolTip(_groupNameBox, "Group administrators and the owner can rename the group.");
        ToolTipService.SetToolTip(_wholeMuteSwitch, "Group administrators and the owner can mute all members.");
        ToolTipService.SetToolTip(_anonymousSwitch, "Group administrators and the owner can allow anonymous messages in OneBot V11.");
        ToolTipService.SetToolTip(_cardBox, "Edit your own card, or a member with a lower role.");
        ToolTipService.SetToolTip(_titleBox, "Only the group owner can edit member titles.");
        ToolTipService.SetToolTip(_toggleAdminButton, "Only the group owner can manage administrators.");
        ToolTipService.SetToolTip(_muteButton, "Administrators can mute members with a lower role.");
        ToolTipService.SetToolTip(_unmuteButton, "Unmute a muted member with a lower role.");
        ToolTipService.SetToolTip(_removeButton, "Leave the group, or remove a member with a lower role.");
    }

    private void SyncGroupSwitches()
    {
        _syncingControls = true;
        try
        {
            _wholeMuteSwitch.IsOn = _group.WholeMuted;
            _anonymousSwitch.IsOn = _group.AnonymousEnabled;
        }
        finally
        {
            _syncingControls = false;
        }
    }

    private async void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Policy.CanRename)
        {
            return;
        }

        var name = _groupNameBox.Text.Trim();
        await RunAsync(async () =>
        {
            await _environment.Platform.SetGroupNameAsync(_group.Id, _operator.Id, name);
            _group = _group with { Name = name };
            _groupNameBox.Text = name;
            Title = $"Members · {_group.Name}";
        });
    }

    private async void WholeMuteSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingControls)
        {
            return;
        }

        if (!Policy.CanSetWholeMute || _busy)
        {
            SyncGroupSwitches();
            return;
        }

        var muted = _wholeMuteSwitch.IsOn;
        await RunAsync(async () =>
        {
            await _environment.Platform.SetWholeMuteAsync(_group.Id, _operator.Id, muted);
            _group = _group with { WholeMuted = muted };
        });
        SyncGroupSwitches();
    }

    private async void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _environment.PropertyChanged += AnonymousContext_PropertyChanged;
        _environment.PreferencesChanged += AnonymousContext_Changed;
        _environment.SelectedChatChanged += AnonymousContext_Changed;
        InvalidateAnonymousContextIfChanged();
        await RefreshAsync();
    }

    private void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args) => Dispose();

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _environment.PropertyChanged -= AnonymousContext_PropertyChanged;
        _environment.PreferencesChanged -= AnonymousContext_Changed;
        _environment.SelectedChatChanged -= AnonymousContext_Changed;
        _anonymousLifetime.Cancel();
        _anonymousLifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool HasCurrentAnonymousContext() => !_closed && _contextBotId is not null
        && _environment.BotPersona?.Id == _contextBotId
        && _environment.CurrentPersona?.Id == _operator.Id
        && _environment.Preferences.Protocol == _capabilities.Protocol
        && _environment.SelectedChat == _contextSelectedChat;

    private void AnonymousContext_PropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        InvalidateAnonymousContextIfChanged();

    private void AnonymousContext_Changed(object? sender, EventArgs args) => InvalidateAnonymousContextIfChanged();

    private void InvalidateAnonymousContextIfChanged()
    {
        if (_closed || _anonymousCancellationToken.IsCancellationRequested || HasCurrentAnonymousContext()) return;
        _anonymousLifetime.Cancel();
        UpdateActionState();
        if (_capabilities.AnonymousMessages)
            ShowMessage("The conversation, sending identity or protocol changed. Reopen group members to change anonymous sending.", InfoBarSeverity.Warning);
    }

    private void EnsureCurrentAnonymousContext()
    {
        _anonymousCancellationToken.ThrowIfCancellationRequested();
        if (!HasCurrentAnonymousContext() || !_environment.Capabilities.AnonymousMessages)
            throw new InvalidOperationException("The conversation, sending identity or protocol changed. Reopen group members.");
    }

    private async void AnonymousSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (_syncingControls) return;
        if (_busy || _anonymousCancellationToken.IsCancellationRequested || !HasCurrentAnonymousContext() || !Policy.CanSetAnonymous)
        {
            SyncGroupSwitches();
            return;
        }

        var enabled = _anonymousSwitch.IsOn;
        await RunAsync(async () =>
        {
            EnsureCurrentAnonymousContext();
            _actor = await _environment.Store.GetMemberAsync(_group.Id, _operator.Id, _anonymousCancellationToken);
            EnsureCurrentAnonymousContext();
            if (!Policy.CanSetAnonymous)
                throw new InvalidOperationException("Only group administrators and the owner can change anonymous sending.");
            await _environment.Platform.SetGroupAnonymousAsync(_group.Id, _operator.Id, enabled, _anonymousCancellationToken);
            EnsureCurrentAnonymousContext();
            _group = _group with { AnonymousEnabled = enabled };
        });
        if (!_closed) SyncGroupSwitches();
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_candidateBox.SelectedItem is not PersonaItem selected)
        {
            ShowMessage("Choose a persona to add.", InfoBarSeverity.Warning);
            return;
        }

        await RunAsync(async () =>
        {
            await _environment.Platform.AddMemberAsync(
                _group.Id,
                selected.Id,
                _operator.Id,
                GroupMemberChangeReason.Administrative);
            await RefreshAsync();
        });
    }

    private async void ToggleAdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMember() is not { } selected || !Policy.CanManageAdmin)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _environment.Platform.SetAdminAsync(
                _group.Id,
                selected.Member.UserId,
                _operator.Id,
                selected.Member.Role != GroupRole.Admin);
            await RefreshAsync();
        });
    }

    private async void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMember() is not { } selected || !Policy.CanMute)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _environment.Platform.MuteMemberAsync(
                _group.Id,
                selected.Member.UserId,
                _operator.Id,
                TimeSpan.FromMinutes(10));
            await RefreshAsync();
        });
    }

    private async void UnmuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMember() is not { } selected || !Policy.CanMute)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _environment.Platform.MuteMemberAsync(
                _group.Id,
                selected.Member.UserId,
                _operator.Id,
                TimeSpan.Zero);
            await RefreshAsync();
        });
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMember() is not { } selected || !Policy.CanRemove)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _environment.Platform.RemoveMemberAsync(
                _group.Id,
                selected.Member.UserId,
                _operator.Id,
                selected.Member.UserId == _operator.Id
                    ? GroupMemberChangeReason.Voluntary
                    : GroupMemberChangeReason.Administrative);
            await RefreshAsync();
        });
    }

    private async void ApplyIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMember() is not { } selected)
        {
            return;
        }

        var changes = Policy.GetProfileChanges(_cardBox.Text, _titleBox.Text);
        if (!changes.HasChanges)
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (changes.Card is { } card)
            {
                await _environment.Platform.SetMemberCardAsync(_group.Id, selected.Member.UserId, _operator.Id, card);
            }
            if (changes.Title is { } title)
            {
                await _environment.Platform.SetMemberTitleAsync(_group.Id, selected.Member.UserId, _operator.Id, title);
            }
            await RefreshAsync();
        });
    }

    private async void NudgeButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMember() is not { } selected || !Policy.CanNudge)
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (_contextBotId is null || _environment.BotPersona?.Id != _contextBotId
                || _environment.CurrentPersona?.Id != _operator.Id
                || _environment.Preferences.Protocol != _capabilities.Protocol
                || _environment.SelectedChat != _contextSelectedChat
                || !_environment.Capabilities.SupportsNudges(ChatScene.Group))
                throw new InvalidOperationException("The conversation, sending identity or protocol changed. Reopen group members.");
            await _environment.NudgeAsync(new Chat(ChatScene.Group, _group.Id, _contextBotId), selected.Member.UserId);
            ShowMessage($"Nudged {selected.User.DisplayName}.", InfoBarSeverity.Success);
        });
    }

    private MemberItem? SelectedMember()
    {
        if (_rosterList.SelectedItem is MemberItem selected)
        {
            return selected;
        }

        ShowMessage("Select a member first.", InfoBarSeverity.Warning);
        return null;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateActionState();
        try
        {
            _status.IsOpen = false;
            await action();
        }
        catch (OperationCanceledException) when (_anonymousCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _environment.ReportError("Group management failed", exception);
            ShowError(exception);
        }
        finally
        {
            _busy = false;
            UpdateActionState();
        }
    }

    private void ShowError(Exception exception) => ShowMessage(exception.Message, InfoBarSeverity.Error);

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        _status.Title = severity == InfoBarSeverity.Error ? "Group action failed" : "Group members";
        _status.Message = message;
        _status.Severity = severity;
        _status.IsOpen = true;
    }
}
