using System.ComponentModel;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Asuka.App;

/// <summary>Views persisted OneBot V11 honors and edits the local platform's award state.</summary>
public sealed class GroupHonorsDialog : ContentDialog, IDisposable
{
    private readonly AppEnvironment _environment;
    private readonly Window _owner;
    private readonly Chat _chat;
    private readonly string? _actorId;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly TextBlock _current = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _permission = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _type = new() { Header = "Honor", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListView _entries = new() { Height = 160, SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBlock _empty = new() { Text = "No members have this honor.", TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _member = new() { Header = "Member", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _description = new() { Header = "Description", MaxLength = 1024, TextWrapping = TextWrapping.Wrap };
    private readonly NumberBox _days = new() { Header = "Consecutive days as current dragon king", Minimum = 1, Maximum = int.MaxValue, Value = 1, SmallChange = 1 };
    private readonly Button _save = new() { Content = "Set honor" };
    private readonly Button _remove = new() { Content = "Remove selected honor" };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private readonly ComboBox _sender = new() { Header = "Red packet sender", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _winner = new() { Header = "Lucky king", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _publish = new() { Content = "Simulate lucky king notice" };
    private GroupHonorInfo? _snapshot;
    private bool _canManage;
    private bool _busy;
    private bool _rendering;
    private bool _disposed;

    public GroupHonorsDialog(AppEnvironment environment, Chat chat, Window owner)
    {
        _environment = environment;
        _chat = chat;
        _owner = owner;
        _actorId = environment.CurrentPersona?.Id;
        Title = "Group honors & notices";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        RequestedTheme = WindowChrome.ToElementTheme(environment.Preferences.Theme);
        Resources["ContentDialogMaxWidth"] = 650d;
        var root = new StackPanel { Width = 550, Spacing = 10 };
        root.Children.Add(_status);
        root.Children.Add(new TextBlock
        {
            Text = $"Group {chat.PeerId} · {environment.CurrentPersona?.DisplayName ?? "No identity"}",
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(_current);
        root.Children.Add(_type);
        root.Children.Add(_entries);
        root.Children.Add(_empty);
        root.Children.Add(_refresh);
        root.Children.Add(_permission);
        root.Children.Add(_member);
        root.Children.Add(_description);
        root.Children.Add(_days);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_save);
        actions.Children.Add(_remove);
        root.Children.Add(actions);
        var lucky = new StackPanel { Spacing = 8 };
        lucky.Children.Add(new TextBlock
        {
            Text = "Choose a simulated result to notify the connected bot. This does not send a red packet or transfer money.",
            TextWrapping = TextWrapping.Wrap,
        });
        lucky.Children.Add(_sender);
        lucky.Children.Add(_winner);
        lucky.Children.Add(_publish);
        root.Children.Add(new Expander
        {
            Header = "Red packet lucky king",
            Content = lucky,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        Content = new ScrollViewer { Content = root, MaxHeight = 580, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var value in Enum.GetValues<GroupHonorType>())
            _type.Items.Add(new ComboBoxItem { Content = HonorLabel(value), Tag = value });
        _type.SelectedIndex = 0;
        AutomationProperties.SetName(_entries, "Members with the selected group honor");
        _type.SelectionChanged += TypeChanged;
        _entries.SelectionChanged += EntryChanged;
        _member.SelectionChanged += InputChanged;
        _sender.SelectionChanged += InputChanged;
        _winner.SelectionChanged += InputChanged;
        _refresh.Click += RefreshClicked;
        _save.Click += SaveClicked;
        _remove.Click += RemoveClicked;
        _publish.Click += PublishClicked;
        Opened += DialogOpened;
        Closing += DialogClosing;
        Closed += DialogClosed;
        _environment.PropertyChanged += EnvironmentChanged;
        _environment.PreferencesChanged += ContextChanged;
        _environment.Store.Changed += StoreChanged;
        _owner.Closed += OwnerClosed;
        UpdateControls();
    }

    private bool ContextIsCurrent => !_disposed && !_lifetime.IsCancellationRequested
        && _chat.Scene == ChatScene.Group && _environment.Preferences.Protocol == ProtocolKind.OneBotV11
        && _environment.BotPersona?.Id == _chat.SelfId && _actorId is not null
        && _environment.CurrentPersona?.Id == _actorId;
    private GroupHonorType SelectedType => (_type.SelectedItem as ComboBoxItem)?.Tag is GroupHonorType value ? value : GroupHonorType.Talkative;
    private static string? MemberId(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;
    private GroupHonorEntry? SelectedEntry => (_entries.SelectedItem as ListViewItem)?.Tag as GroupHonorEntry;

    private void EnsureCurrent()
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!ContextIsCurrent) throw new InvalidOperationException("The sending identity, bot account or protocol changed. Reopen this window.");
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || !ContextIsCurrent) return;
        _busy = true;
        UpdateControls();
        try { EnsureCurrent(); await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed)
            {
                _status.Severity = InfoBarSeverity.Error;
                _status.Message = error.Message;
                _status.IsOpen = true;
            }
        }
        finally
        {
            _busy = false;
            UpdateControls();
        }
    }

    private async Task RefreshAsync()
    {
        var token = _lifetime.Token;
        EnsureCurrent();
        _canManage = false;
        var snapshot = await _environment.Platform.GetGroupHonorInfoAsync(_chat.PeerId, _actorId!, token);
        var members = await _environment.Store.GetMembersAsync(_chat.PeerId, token);
        var users = await _environment.Store.GetAllUsersAsync(token);
        EnsureCurrent();
        var selfMember = members.FirstOrDefault(member => member.UserId == _chat.SelfId);
        if (selfMember is null) throw new InvalidOperationException("The bot account is no longer a member of this group.");
        _snapshot = snapshot;
        _refresh.Content = "Refresh";
        _canManage = members.FirstOrDefault(member => member.UserId == _actorId)?.Role is GroupRole.Owner or GroupRole.Admin;
        var names = users.ToDictionary(user => user.Id, user => user.DisplayName, StringComparer.Ordinal);
        _rendering = true;
        try
        {
            foreach (var box in new[] { _member, _sender, _winner })
            {
                var selected = MemberId(box);
                box.Items.Clear();
                foreach (var member in members)
                {
                    var item = new ComboBoxItem { Content = $"{names.GetValueOrDefault(member.UserId, member.UserId)} · {member.UserId}", Tag = member.UserId };
                    box.Items.Add(item);
                    if (member.UserId == selected) box.SelectedItem = item;
                }
                if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
            }
        }
        finally { _rendering = false; }
        _permission.Text = _canManage
            ? "Edit simulated platform awards. New dragon king, group fire and emotion awards notify the bot."
            : "Switch to a group owner or administrator to edit awards and simulate notices.";
        RenderEntries();
    }

    private void RenderEntries()
    {
        var selectedId = SelectedEntry?.UserId;
        var entries = SelectedType switch
        {
            GroupHonorType.Talkative => _snapshot?.TalkativeList,
            GroupHonorType.Performer => _snapshot?.PerformerList,
            GroupHonorType.Legend => _snapshot?.LegendList,
            GroupHonorType.StrongNewbie => _snapshot?.StrongNewbieList,
            _ => _snapshot?.EmotionList,
        };
        _rendering = true;
        try
        {
            _entries.Items.Clear();
            foreach (var entry in entries ?? [])
            {
                var item = new ListViewItem
                {
                    Tag = entry,
                    Content = new TextBlock
                    {
                        Text = $"{entry.Nickname} · {entry.UserId}\n{entry.Description}",
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                _entries.Items.Add(item);
                if (entry.UserId == selectedId) _entries.SelectedItem = item;
            }
        }
        finally { _rendering = false; }
        _empty.Visibility = _entries.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _current.Text = _snapshot?.CurrentTalkative is { } current
            ? $"Current dragon king: {current.Nickname} · {current.DayCount} days"
            : "No current dragon king.";
        _days.Visibility = SelectedType == GroupHonorType.Talkative ? Visibility.Visible : Visibility.Collapsed;
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (_disposed) return;
        var available = ContextIsCurrent && !_busy;
        _refresh.IsEnabled = available;
        _type.IsEnabled = _entries.IsEnabled = available && _snapshot is not null;
        var writable = available && _canManage;
        _member.IsEnabled = _description.IsEnabled = _days.IsEnabled = writable;
        _sender.IsEnabled = _winner.IsEnabled = writable;
        _save.IsEnabled = writable && MemberId(_member) is not null;
        _remove.IsEnabled = writable && SelectedEntry is not null;
        _publish.IsEnabled = writable && MemberId(_sender) is not null && MemberId(_winner) is not null;
    }

    private void TypeChanged(object sender, SelectionChangedEventArgs args)
    {
        _entries.SelectedItem = null;
        _description.Text = string.Empty;
        _days.Value = 1;
        RenderEntries();
    }

    private void EntryChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering) return;
        if (SelectedEntry is { } entry)
        {
            _member.SelectedItem = _member.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == entry.UserId);
            _description.Text = entry.Description;
            _days.Value = _snapshot?.CurrentTalkative is { } current && current.UserId == entry.UserId ? current.DayCount : 1;
        }
        UpdateControls();
    }

    private void InputChanged(object sender, SelectionChangedEventArgs args) { if (!_rendering) UpdateControls(); }
    private async void RefreshClicked(object sender, RoutedEventArgs args) => await RunAsync(RefreshAsync);
    private async void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => await RunAsync(RefreshAsync);
    private async void SaveClicked(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        var target = MemberId(_member) ?? throw new InvalidOperationException("Choose a member.");
        var days = _days.Value;
        if (!double.IsFinite(days) || days < 1 || days > int.MaxValue || days != Math.Truncate(days))
            throw new InvalidOperationException("The day count must be a positive whole number.");
        var changed = await _environment.Platform.SetGroupHonorAsync(_chat.PeerId, target, SelectedType, _actorId!,
            _description.Text, (int)days, _lifetime.Token);
        EnsureCurrent();
        await RefreshAsync();
        ShowSuccess(changed ? "Honor saved." : "This honor is already up to date.");
    });

    private async void RemoveClicked(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        var entry = SelectedEntry ?? throw new InvalidOperationException("Select an honor to remove.");
        var changed = await _environment.Platform.RemoveGroupHonorAsync(_chat.PeerId, entry.UserId, SelectedType, _actorId!, _lifetime.Token);
        EnsureCurrent();
        await RefreshAsync();
        ShowSuccess(changed ? "Honor removed." : "This honor was already removed.");
    });

    private async void PublishClicked(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        var senderId = MemberId(_sender) ?? throw new InvalidOperationException("Choose the sender.");
        var winnerId = MemberId(_winner) ?? throw new InvalidOperationException("Choose the lucky king.");
        await _environment.Platform.PublishGroupLuckyKingAsync(_chat.PeerId, senderId, winnerId, _actorId!, _lifetime.Token);
        EnsureCurrent();
        ShowSuccess("Lucky king notice simulated. Connected bots receive it according to their event settings.");
    });

    private void ShowSuccess(string message)
    {
        _status.Severity = InfoBarSeverity.Success;
        _status.Message = message;
        _status.IsOpen = true;
    }

    private void EnvironmentChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppEnvironment.CurrentPersona) or nameof(AppEnvironment.BotPersona) or nameof(AppEnvironment.Preferences))
            ContextChanged(sender, EventArgs.Empty);
    }

    private void ContextChanged(object? sender, EventArgs args)
    {
        if (_disposed || ContextIsCurrent) return;
        _lifetime.Cancel();
        _status.Severity = InfoBarSeverity.Warning;
        _status.Message = "The sending identity, bot account or protocol changed. Reopen this window.";
        _status.IsOpen = true;
        UpdateControls();
    }

    private void StoreChanged(object? sender, StoreChangedEventArgs args)
    {
        if ((args.Changes & (StoreChangeKind.Groups | StoreChangeKind.Users | StoreChangeKind.Members)) == 0) return;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!ContextIsCurrent || _busy) return;
            // Incoming messages also update member.LastSentAt. Keep the active
            // editor and its open drop-down intact; mutations recheck authority.
            _refresh.Content = "Refresh · changes available";
        });
    }

    private void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args) => _lifetime.Cancel();
    private void DialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => Dispose();
    private void OwnerClosed(object sender, WindowEventArgs args) => Dispose();
    private static string HonorLabel(GroupHonorType type) => type switch
    {
        GroupHonorType.Talkative => "Dragon king · talkative",
        GroupHonorType.Performer => "Group fire · performer",
        GroupHonorType.Legend => "Legend · legend",
        GroupHonorType.StrongNewbie => "Strong newbie · strong_newbie",
        _ => "Emotion · emotion",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _environment.PropertyChanged -= EnvironmentChanged;
        _environment.PreferencesChanged -= ContextChanged;
        _environment.Store.Changed -= StoreChanged;
        _owner.Closed -= OwnerClosed;
        Opened -= DialogOpened;
        Closing -= DialogClosing;
        Closed -= DialogClosed;
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
