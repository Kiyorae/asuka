using Asuka.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Asuka.App;

public sealed class GroupManagementDialog : ContentDialog
{
    private readonly AppEnvironment _environment;
    private readonly User _operator;
    private readonly ListView _rosterList = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 260 };
    private readonly ComboBox _candidateBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _groupNameBox = new();
    private readonly ToggleSwitch _wholeMuteSwitch = new();
    private readonly TextBox _cardBox = new() { PlaceholderText = "Selected member card" };
    private readonly TextBox _titleBox = new() { PlaceholderText = "Selected member title" };
    private readonly InfoBar _status = new() { IsClosable = true };
    private Group _group;

    public GroupManagementDialog(AppEnvironment environment, Group group, User @operator)
    {
        _environment = environment;
        _group = group;
        _operator = @operator;
        Title = $"Members · {group.Name}";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        MinWidth = 680;
        Content = BuildContent();
        Opened += async (_, _) => await RefreshAsync();
        _rosterList.SelectionChanged += RosterList_SelectionChanged;
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

        var groupGrid = new Grid { ColumnSpacing = 8 };
        groupGrid.ColumnDefinitions.Add(new ColumnDefinition());
        groupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        groupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _groupNameBox.Header = "Group name";
        _groupNameBox.Text = _group.Name;
        groupGrid.Children.Add(_groupNameBox);
        var renameButton = new Button { Content = "Rename", VerticalAlignment = VerticalAlignment.Bottom };
        renameButton.Click += RenameButton_Click;
        Grid.SetColumn(renameButton, 1);
        groupGrid.Children.Add(renameButton);
        _wholeMuteSwitch.Header = "Mute all";
        _wholeMuteSwitch.IsOn = _group.WholeMuted;
        _wholeMuteSwitch.Toggled += WholeMuteSwitch_Toggled;
        Grid.SetColumn(_wholeMuteSwitch, 2);
        groupGrid.Children.Add(_wholeMuteSwitch);
        root.Children.Add(groupGrid);

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
        editorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorGrid.Children.Add(_cardBox);
        Grid.SetColumn(_titleBox, 1);
        editorGrid.Children.Add(_titleBox);
        var applyIdentityButton = new Button { Content = "Apply profile", VerticalAlignment = VerticalAlignment.Bottom };
        applyIdentityButton.Click += ApplyIdentityButton_Click;
        Grid.SetColumn(applyIdentityButton, 2);
        editorGrid.Children.Add(applyIdentityButton);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(CreateActionButton("Toggle admin", ToggleAdminButton_Click));
        actions.Children.Add(CreateActionButton("Mute 10 min", MuteButton_Click));
        actions.Children.Add(CreateActionButton("Unmute", UnmuteButton_Click));
        actions.Children.Add(CreateActionButton("Remove", RemoveButton_Click));
        Grid.SetRow(actions, 1);
        Grid.SetColumnSpan(actions, 3);
        editorGrid.Children.Add(actions);
        root.Children.Add(editorGrid);

        root.Children.Add(CreateDivider());
        var addGrid = new Grid { ColumnSpacing = 8 };
        addGrid.ColumnDefinitions.Add(new ColumnDefinition());
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _candidateBox.Header = "Add an existing persona";
        _candidateBox.DisplayMemberPath = nameof(PersonaItem.Description);
        addGrid.Children.Add(_candidateBox);
        var addButton = new Button { Content = "Add member", VerticalAlignment = VerticalAlignment.Bottom };
        addButton.Click += AddButton_Click;
        Grid.SetColumn(addButton, 1);
        addGrid.Children.Add(addButton);
        root.Children.Add(addGrid);

        return new ScrollViewer
        {
            Content = root,
            MaxHeight = 620,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static Button CreateActionButton(string content, RoutedEventHandler handler)
    {
        var button = new Button { Content = content };
        button.Click += handler;
        return button;
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
            var roster = await _environment.GetRosterAsync(_group.Id);
            var memberIds = roster.Select(item => item.Member.UserId).ToHashSet(StringComparer.Ordinal);
            _rosterList.ItemsSource = roster.Select(item => new MemberItem(item)).ToList();
            _candidateBox.ItemsSource = _environment.Personas.Where(persona => !memberIds.Contains(persona.Id)).ToList();
            _candidateBox.SelectedIndex = _candidateBox.Items.Count > 0 ? 0 : -1;
            _status.IsOpen = false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void RosterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rosterList.SelectedItem is MemberItem selected)
        {
            _cardBox.Text = selected.Member.Card;
            _titleBox.Text = selected.Member.Title;
        }
    }

    private async void RenameButton_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await _environment.Platform.SetGroupNameAsync(_group.Id, _operator.Id, _groupNameBox.Text);
        _group = _group with { Name = _groupNameBox.Text.Trim() };
        Title = $"Members · {_group.Name}";
    });

    private async void WholeMuteSwitch_Toggled(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await _environment.Platform.SetWholeMuteAsync(_group.Id, _operator.Id, _wholeMuteSwitch.IsOn);
        _group = _group with { WholeMuted = _wholeMuteSwitch.IsOn };
    });

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
        if (SelectedMember() is not { } selected)
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
        if (SelectedMember() is not { } selected)
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
        if (SelectedMember() is not { } selected)
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
        if (SelectedMember() is not { } selected)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _environment.Platform.RemoveMemberAsync(
                _group.Id,
                selected.Member.UserId,
                _operator.Id,
                GroupMemberChangeReason.Administrative);
            await RefreshAsync();
        });
    }

    private async void ApplyIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMember() is not { } selected)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _environment.Platform.SetMemberCardAsync(
                _group.Id,
                selected.Member.UserId,
                _operator.Id,
                _cardBox.Text);
            await _environment.Platform.SetMemberTitleAsync(
                _group.Id,
                selected.Member.UserId,
                _operator.Id,
                _titleBox.Text);
            await RefreshAsync();
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
        try
        {
            _status.IsOpen = false;
            await action();
        }
        catch (Exception exception)
        {
            _environment.ReportError("Group management failed", exception);
            ShowError(exception);
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
