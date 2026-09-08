using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace Asuka.App;

public sealed partial class MainWindow : Window
{
    private readonly AppEnvironment _environment;
    private readonly ObservableCollection<AttachmentDraft> _attachments = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _conversationRefreshTimer;
    private readonly ObservableCollection<LogEntryItem> _inlineConsoleEntries = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _inlineConsoleRefreshTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _messageRefreshTimer;
    private readonly ObservableCollection<MentionDraft> _mentions = [];
    private readonly ObservableCollection<MessageSegment> _richContent = [];
    private readonly Dictionary<string, ComposerDraft> _drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingAttachmentImports = new(StringComparer.Ordinal);
    private readonly ObservableCollection<ConversationItem> _visibleConversations = [];
    private readonly HashSet<string> _resolvingRequestFlags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _renderedMessageIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MessageRow> _messageRows = new(StringComparer.Ordinal);
    private ReplyDraft? _reply;
    private bool _anonymous;
    private string? _activeDraftKey;
    private CancellationTokenSource? _consoleMotionCancellation;
    private readonly PageNavigationState _navigation = new();
    private string? _lastConnectionMotionState;
    private string _lastSendStatusText = string.Empty;
    private Control? _pendingNavigationFocus;
    private string? _pendingNavigationFocusPageTag;
    private FocusState _pendingNavigationFocusState = FocusState.Programmatic;
    private CancellationTokenSource? _pageMotionCancellation;
    private CancellationTokenSource? _protocolMotionCancellation;
    private bool _closed;
    private bool _consolePanelClosing;
    private bool _consoleVisible;
    private bool _currentUserOwnsSelectedGroup;
    private bool _loaded;
    private readonly TaskCompletionSource _startupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task StartupReady => _startupReady.Task;
    internal CancellationToken StartupCancellationToken { get; set; }
    private bool? _paneDetailsVisible;
    private bool? _protocolPaneOverlay;
    private bool? _inlineConsoleStacked;
    private bool _utilityLayoutQueued;
    private bool _pageTransitionRunning;
    private bool _pageTransitionEntering;
    private bool _syncingUtilityToggles;
    private bool _protocolPanelClosing;
    private bool _protocolPaneVisible;
    private int _consolePanelTransitionVersion;
    private int _conversationNavigationVersion;
    private int _connectionInProgress;
    private int _dialogInProgress;
    private int _sendInProgress;
    private int _protocolPanelTransitionVersion;
    private long _paneOpenChangedToken;

    public MainWindow(AppEnvironment environment)
    {
        _environment = environment;
        InitializeComponent();
        _inlineConsoleRefreshTimer = RootGrid.DispatcherQueue.CreateTimer();
        _inlineConsoleRefreshTimer.Interval = TimeSpan.FromMilliseconds(150);
        _inlineConsoleRefreshTimer.IsRepeating = false;
        _inlineConsoleRefreshTimer.Tick += (_, _) => RefreshInlineConsole();
        _conversationRefreshTimer = RootGrid.DispatcherQueue.CreateTimer();
        _conversationRefreshTimer.Interval = TimeSpan.FromMilliseconds(16);
        _conversationRefreshTimer.IsRepeating = false;
        _conversationRefreshTimer.Tick += (_, _) => RefreshConversationFilter();
        _messageRefreshTimer = RootGrid.DispatcherQueue.CreateTimer();
        _messageRefreshTimer.Interval = TimeSpan.FromMilliseconds(16);
        _messageRefreshTimer.IsRepeating = false;
        _messageRefreshTimer.Tick += (_, _) => RefreshMessagesAfterCollectionChange();
        WireUiEvents();
        Title = "Asuka";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        WindowChrome.Configure(this, 1320, 820);
        RootGrid.DataContext = environment;
        ActivityLogsPage.Initialize(environment, this);
        SettingsPageHost.Initialize(environment, this);
        PendingRequestList.ItemsSource = environment.PendingRequests;
        ConversationList.ItemsSource = _visibleConversations;
        ProtocolInspectorHost.Initialize(environment);
        ProtocolInspectorHost.CloseRequested += (_, _) => SetProtocolPaneVisible(false);
        environment.PendingRequests.CollectionChanged += PendingRequests_CollectionChanged;
        AccountSendingIdentityBox.ItemsSource = environment.Personas;
        AccountBotBox.ItemsSource = environment.Personas;
        AttachmentList.ItemsSource = _attachments;
        InlineConsoleList.ItemsSource = _inlineConsoleEntries;
        MentionList.ItemsSource = _mentions;
        RichContentList.ItemsSource = _richContent;
        RefreshMessageVisuals();
        UpdateEnvironmentText();
        environment.PropertyChanged += Environment_PropertyChanged;
        environment.PreferencesChanged += Environment_PreferencesChanged;
        environment.SelectedChatChanged += Environment_SelectedChatChanged;
        environment.Conversations.CollectionChanged += Conversations_CollectionChanged;
        environment.Logs.CollectionChanged += Logs_CollectionChanged;
        environment.Personas.CollectionChanged += Personas_CollectionChanged;
        environment.RawTraffic.CollectionChanged += RawTraffic_CollectionChanged;
        environment.Messages.CollectionChanged += Messages_CollectionChanged;
        RootGrid.Loaded += RootGrid_Loaded;
        RootGrid.SizeChanged += RootGrid_SizeChanged;
        WorkspaceLayout.SizeChanged += WorkspaceLayout_SizeChanged;
        Closed += MainWindow_Closed;
    }

    private void WireUiEvents()
    {
        AppNavigationView.SelectionChanged += AppNavigationView_SelectionChanged;
        AppNavigationView.SelectedItem = MessagesNavigationItem;
        AppTitleBar.PaneToggleRequested += (_, _) =>
            AppNavigationView.IsPaneOpen = !AppNavigationView.IsPaneOpen;
        _paneOpenChangedToken = AppNavigationView.RegisterPropertyChangedCallback(
            NavigationView.IsPaneOpenProperty, (_, _) => UpdatePanePresentation());
        UpdatePanePresentation();
        ConnectButton.Click += ConnectButton_Click;
        NewPersonaMenuItem.Click += NewPersona_Click;
        AddFriendButton.Click += AddFriend_Click;
        NewGroupButton.Click += NewGroup_Click;
        ConversationSearchBox.TextChanged += ConversationSearchBox_TextChanged;
        ConversationList.ItemClick += ConversationList_ItemClick;
        ConversationList.ContainerContentChanging += ConversationList_ContainerContentChanging;
        ConversationTypeBox.SelectionChanged += (_, _) => RefreshConversationFilter();
        MessageList.SelectionChanged += MessageList_SelectionChanged;
        BrowsePeopleButton.Click += (_, _) => AppNavigationView.SelectedItem = PeopleNavigationItem;
        ProfileButton.Click += (_, _) => ShowAccountPage();
        ApplyAccountButton.Click += ApplyAccount_Click;
        AccountNewPersonaButton.Click += NewPersona_Click;
        ConnectionSettingsButton.Click += OpenSettings_Click;
        CopyCurrentConversationIdMenuItem.Click += CopyConversationId_Click;
        ManageCurrentGroupMenuItem.Click += ManageGroup_Click;
        ClearHistoryMenuItem.Click += ClearHistory_Click;
        RemoveCurrentConversationMenuItem.Click += RemoveConversationEntity_Click;
        NewPersonaEmptyMenuItem.Click += NewPersona_Click;
        AddFriendEmptyMenuItem.Click += AddFriend_Click;
        NewGroupEmptyMenuItem.Click += NewGroup_Click;
        ComposerBorder.DragOver += Composer_DragOver;
        ComposerBorder.Drop += Composer_Drop;
        AttachButton.Click += AttachButton_Click;
        MentionButton.Click += MentionButton_Click;
        AnonymousButton.Click += AnonymousButton_Click;
        RemoveAttachmentButton.Click += RemoveAttachment_Click;
        RemoveMentionButton.Click += RemoveMention_Click;
        ComposerTextBox.TextChanged += ComposerTextBox_TextChanged;
        ComposerTextBox.KeyDown += ComposerTextBox_KeyDown;
        ComposerTextBox.GotFocus += (_, _) => FluentMotion.Pulse(ComposerPulseHost, 1.008f);
        SendButton.Click += SendButton_Click;
        ToggleProtocolPaneButton.Checked += (_, _) =>
        {
            if (!_syncingUtilityToggles) SetProtocolPaneVisible(true);
        };
        ToggleProtocolPaneButton.Unchecked += (_, _) =>
        {
            if (!_syncingUtilityToggles) SetProtocolPaneVisible(false);
        };
        ToggleConsoleButton.Checked += (_, _) =>
        {
            if (!_syncingUtilityToggles) SetConsoleVisible(true);
        };
        ToggleConsoleButton.Unchecked += (_, _) =>
        {
            if (!_syncingUtilityToggles) SetConsoleVisible(false);
        };
        CloseConsoleButton.Click += (_, _) => SetConsoleVisible(false);
        OpenFullConsoleButton.Click += OpenLogs_Click;
        InlineConsoleClearButton.Click += InlineConsoleClear_Click;
        InlineConsoleSearchBox.TextChanged += (_, _) => RefreshInlineConsole();
        InlineConsoleCategoryBox.SelectionChanged += (_, _) => RefreshInlineConsole();
        InlineConsoleList.SelectionChanged += InlineConsoleList_SelectionChanged;
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _environment.AddLog("Lifecycle", "Window shown", "The main workspace is ready for interaction.");
        InstallKeyboardAccelerators();
        try
        {
            await _environment.InitializeAsync(StartupCancellationToken);
            if (_closed || StartupCancellationToken.IsCancellationRequested) return;
            ActivityLogsPage.Initialize(_environment, this);
            SettingsPageHost.Initialize(_environment, this);
            await RefreshSelectedGroupAuthorityAsync();
            if (_closed || StartupCancellationToken.IsCancellationRequested) return;
            ApplyTheme();
            RefreshConversationFilter();
            UpdateChatState();
            RestoreDraftForCurrentContext();
            RefreshInlineConsole();
            UpdatePanePresentation();
            UpdateUtilityPanels();
            if (!StartupCancellationToken.CanBeCanceled && !_pageTransitionRunning && _navigation.Revision == 0)
            {
                var motionToken = RestartMotion(ref _pageMotionCancellation);
                await Task.WhenAll(
                    FluentMotion.EnterAsync(
                        PageFor(_navigation.CurrentPage),
                        new System.Numerics.Vector3(0, 16, 0),
                        TimeSpan.FromMilliseconds(200),
                        cancellationToken: motionToken),
                    FluentMotion.EnterAsync(
                        PageHeaderContent,
                        new System.Numerics.Vector3(0, 16, 0),
                        TimeSpan.FromMilliseconds(200),
                        cancellationToken: motionToken));
            }
            StartShowcasePlayback();
        }
        catch (OperationCanceledException) when (StartupCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed) ShowError(exception);
        }
        finally { _startupReady.TrySetResult(); }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _environment.AddLog("Lifecycle", "Window closing", "The main workspace is closing.");
        _closed = true;
        if (!_loaded) _startupReady.TrySetCanceled();
        DisposeShowcasePlayback();
        AppNavigationView.UnregisterPropertyChangedCallback(
            NavigationView.IsPaneOpenProperty, _paneOpenChangedToken);
        WorkspaceLayout.SizeChanged -= WorkspaceLayout_SizeChanged;
        ++_protocolPanelTransitionVersion;
        ++_consolePanelTransitionVersion;
        _navigation.Request(_navigation.CurrentPage);
        CancelMotion(ref _pageMotionCancellation);
        CancelMotion(ref _protocolMotionCancellation);
        CancelMotion(ref _consoleMotionCancellation);
        ClearQueuedNavigationFocus();
        SaveActiveDraft();
        _conversationRefreshTimer.Stop();
        _inlineConsoleRefreshTimer.Stop();
        _messageRefreshTimer.Stop();
        ProtocolInspectorHost.Dispose();
        _environment.PendingRequests.CollectionChanged -= PendingRequests_CollectionChanged;
        ActivityLogsPage.Dispose();
        SettingsPageHost.Dispose();
        _environment.PropertyChanged -= Environment_PropertyChanged;
        _environment.PreferencesChanged -= Environment_PreferencesChanged;
        _environment.SelectedChatChanged -= Environment_SelectedChatChanged;
        _environment.Conversations.CollectionChanged -= Conversations_CollectionChanged;
        _environment.Logs.CollectionChanged -= Logs_CollectionChanged;
        _environment.Personas.CollectionChanged -= Personas_CollectionChanged;
        _environment.RawTraffic.CollectionChanged -= RawTraffic_CollectionChanged;
        _environment.Messages.CollectionChanged -= Messages_CollectionChanged;
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var useCompactTitleBar = e.NewSize.Width < 760;
        AppTitleBar.Subtitle = useCompactTitleBar ? string.Empty : "Native protocol workspace";
    }

    private void UpdatePanePresentation()
    {
        // Observe the actual open state before the native inline pane animation starts.
        var showDetails = AppNavigationView.IsPaneOpen;
        if (_closed || _paneDetailsVisible == showDetails)
        {
            return;
        }

        _paneDetailsVisible = showDetails;
        ConnectionDetailsPanel.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        ProfileDetailsPanel.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        ProfileChevron.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        ProfileButton.HorizontalAlignment = showDetails ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ProfileButton.Width = showDetails ? double.NaN : 40;
    }

    private void WorkspaceLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_closed || _utilityLayoutQueued)
        {
            return;
        }

        // A native pane transition can change the available workspace without resizing the window.
        // Coalesce notifications and apply layout after the current measure/arrange pass.
        _utilityLayoutQueued = WorkspaceLayout.DispatcherQueue.TryEnqueue(() =>
        {
            _utilityLayoutQueued = false;
            if (!_closed)
            {
                UpdateUtilityPanels();
            }
        });
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e) =>
        await RunConnectionOperationAsync(
            retry: _environment.ConnectionStatus.StartsWith("Failed", StringComparison.OrdinalIgnoreCase));

    private async Task RunConnectionOperationAsync(bool retry)
    {
        if (_environment.IsShowcaseMode)
        {
            ToggleShowcasePlayback();
            return;
        }
        if (Interlocked.Exchange(ref _connectionInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            UpdateConnectionPresentation();
            if (retry)
            {
                await _environment.ConnectAsync();
            }
            else
            {
                await _environment.ToggleConnectionAsync();
            }
        }
        catch (Exception exception)
        {
            _environment.ReportError(retry ? "Connection retry failed" : "Connection failed", exception);
            ShowError(exception);
        }
        finally
        {
            Volatile.Write(ref _connectionInProgress, 0);
            UpdateConnectionPresentation();
        }
    }

    private async void ConversationList_ItemClick(object sender, ItemClickEventArgs e)
    {
        var conversation = e.ClickedItem switch
        {
            ConversationItem item => item,
            ListViewItem { Tag: ConversationItem item } => item,
            _ => null,
        };
        if (conversation is null)
        {
            return;
        }

        await OpenConversationPageAsync(conversation);
    }

    private async Task OpenConversationPageAsync(ConversationItem conversation)
    {
        var navigationRevision = _navigation.Revision;
        var requestVersion = ++_conversationNavigationVersion;
        SaveActiveDraft();
        ConversationList.SelectedItem = conversation;
        try
        {
            await _environment.SelectConversationAsync(conversation);
            if (_closed || navigationRevision != _navigation.Revision
                || requestVersion != _conversationNavigationVersion
                || _environment.SelectedChat?.Id != conversation.Id)
            {
                return;
            }

            QueueNavigationFocus(ComposerTextBox, "messages");
            AppNavigationView.SelectedItem = MessagesNavigationItem;
        }
        catch (Exception exception)
        {
            if (!_closed && requestVersion == _conversationNavigationVersion)
            {
                ConversationList.SelectedItem = CurrentConversation();
                ShowError(exception);
            }
        }
    }

    private void ConversationSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshConversationFilter();

    private void ConversationList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            args.ItemContainer.ContextFlyout = null;
            return;
        }
        if (args.Item is ConversationItem item)
        {
            args.ItemContainer.ContextFlyout = CreateConversationContextFlyout(item);
            AutomationProperties.SetName(args.ItemContainer, item.AccessibilityLabel);
        }
    }

    private async void NewPersona_Click(object sender, RoutedEventArgs e)
    {
        if (!TryEnterDialog())
        {
            return;
        }

        try
        {
            var nameBox = new TextBox { Header = "Name", PlaceholderText = "Required" };
            var nicknameBox = new TextBox { Header = "Nickname", PlaceholderText = "Defaults to the name" };
            var signBox = new TextBox { Header = "Signature", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            var avatarBox = new TextBox { Header = "Avatar URL", PlaceholderText = "Optional image URL" };
            var makeCurrent = new CheckBox { Content = "Use as the sending identity", IsChecked = true };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(nameBox);
            content.Children.Add(nicknameBox);
            content.Children.Add(signBox);
            content.Children.Add(avatarBox);
            content.Children.Add(makeCurrent);

            var dialog = CreateDialog("New persona", "Create", content);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await _environment.CreatePersonaAsync(
                nameBox.Text,
                nicknameBox.Text,
                signBox.Text,
                makeCurrent.IsChecked == true,
                avatarBox.Text);
        }
        catch (Exception exception)
        {
            _environment.ReportError("Persona creation failed", exception);
            ShowError(exception);
        }
        finally
        {
            ExitDialog();
        }
    }

    private async void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (!TryEnterDialog())
        {
            return;
        }

        try
        {
            var nameBox = new TextBox { Header = "Group name", PlaceholderText = "Required" };
            var introBox = new TextBox { Header = "Description", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            var capacityBox = new NumberBox
            {
                Header = "Member limit",
                Minimum = 2,
                Maximum = 10_000,
                Value = 200,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(nameBox);
            content.Children.Add(introBox);
            content.Children.Add(capacityBox);

            var dialog = CreateDialog("New group", "Create", content);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await _environment.CreateGroupAsync(nameBox.Text, introBox.Text, (int)capacityBox.Value);
            RefreshConversationFilter();
        }
        catch (Exception exception)
        {
            _environment.ReportError("Group creation failed", exception);
            ShowError(exception);
        }
        finally
        {
            ExitDialog();
        }
    }

    private async void AddFriend_Click(object sender, RoutedEventArgs e)
    {
        if (!TryEnterDialog())
        {
            return;
        }

        try
        {
            var candidates = _environment.Personas
                .Where(persona => persona.Id != _environment.BotPersona?.Id)
                .ToList();
            if (candidates.Count == 0)
            {
                ShowErrorMessage("Create another persona before adding a friend.");
                return;
            }

            var personBox = new ComboBox
            {
                Header = "Persona",
                ItemsSource = candidates,
                DisplayMemberPath = nameof(PersonaItem.Description),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var remarkBox = new TextBox { Header = "Remark", PlaceholderText = "Optional" };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(personBox);
            content.Children.Add(remarkBox);

            var dialog = CreateDialog("Add friend", "Add", content);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || personBox.SelectedItem is not PersonaItem selected)
            {
                return;
            }

            await _environment.AddFriendAsync(selected.User, remarkBox.Text);
            RefreshConversationFilter();
        }
        catch (Exception exception)
        {
            _environment.ReportError("Friendship creation failed", exception);
            ShowError(exception);
        }
        finally
        {
            ExitDialog();
        }
    }

    private async void ManageGroup_Click(object sender, RoutedEventArgs e)
    {
        if (!TryEnterDialog())
        {
            return;
        }

        try
        {
            var chat = _environment.SelectedChat;
            var persona = _environment.CurrentPersona;
            if (chat?.Scene != ChatScene.Group
                || persona is null
                || !_environment.CurrentPersonaBelongsToSelectedGroup)
            {
                ShowErrorMessage("Only a member of this group can manage its roster and settings.");
                return;
            }

            var group = await _environment.Store.GetGroupAsync(chat.PeerId);
            if (group is null)
            {
                ShowErrorMessage("The selected group no longer exists.");
                return;
            }

            using var dialog = new GroupManagementDialog(_environment, group, persona)
            {
                XamlRoot = RootGrid.XamlRoot,
            };
            await dialog.ShowAsync();
            await RefreshSelectedGroupAuthorityAsync();
        }
        catch (Exception exception)
        {
            _environment.ReportError("Group management failed", exception);
            ShowError(exception);
        }
        finally
        {
            ExitDialog();
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(AppNavigationView.SelectedItem, SettingsNavigationItem))
        {
            ShowPage("settings");
            return;
        }

        AppNavigationView.SelectedItem = SettingsNavigationItem;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(AppNavigationView.SelectedItem, ActivityLogsNavigationItem))
        {
            ShowPage("logs");
            return;
        }

        AppNavigationView.SelectedItem = ActivityLogsNavigationItem;
    }

    private void AppNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ShowPage("settings");
            return;
        }

        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        ShowPage(tag ?? "messages");
    }

    private void ShowPage(string tag)
    {
        if (_closed) return;
        tag = PageNavigationState.Normalize(tag);
        _navigation.Request(tag);
        if (_pendingNavigationFocusPageTag is not null
            && !string.Equals(_pendingNavigationFocusPageTag, tag, StringComparison.Ordinal))
        {
            ClearQueuedNavigationFocus();
        }

        if (!_loaded)
        {
            ApplyRequestedPage();
            ApplyQueuedNavigationFocus(tag);
            return;
        }

        if (_pageTransitionRunning)
        {
            // Finish the short exit, but interrupt an obsolete entrance immediately.
            if (_pageTransitionEntering && _navigation.HasPendingPage)
            {
                CancelMotion(ref _pageMotionCancellation);
            }
            return;
        }

        if (!_navigation.HasPendingPage)
        {
            // Reselecting a page must not reload its editors or discard an in-progress draft.
            UpdatePageHeader();
            ApplyQueuedNavigationFocus(tag);
            return;
        }

        _ = RunPageTransitionQueueAsync();
    }

    private async Task RunPageTransitionQueueAsync()
    {
        _pageTransitionRunning = true;
        try
        {
            while (!_closed && _navigation.HasPendingPage)
            {
                var previousTag = _navigation.CurrentPage;
                var previousPage = PageFor(previousTag);
                var motionToken = RestartMotion(ref _pageMotionCancellation);
                _pageTransitionEntering = false;
                previousPage.IsHitTestVisible = false;
                // A neutral exit allows a rapid reversal to pick the correct latest direction.
                await Task.WhenAll(
                    FluentMotion.ExitAsync(previousPage, System.Numerics.Vector3.Zero,
                        TimeSpan.FromMilliseconds(90), cancellationToken: motionToken),
                    FluentMotion.ExitAsync(PageHeaderContent, System.Numerics.Vector3.Zero,
                        TimeSpan.FromMilliseconds(90), cancellationToken: motionToken));
                if (_closed) return;

                var tag = _navigation.RequestedPage;
                var direction = PageNavigationState.GetDirection(previousTag, tag);
                if (direction != 0)
                {
                    ApplyRequestedPage();
                    FluentMotion.Reset(previousPage);
                    previousPage.IsHitTestVisible = true;
                }

                var currentPage = PageFor(tag);
                currentPage.IsHitTestVisible = false;
                _pageTransitionEntering = true;
                var offset = new System.Numerics.Vector3(24 * direction, 0, 0);
                await Task.WhenAll(
                    FluentMotion.EnterAsync(currentPage, offset, TimeSpan.FromMilliseconds(180),
                        cancellationToken: motionToken, resumeFromCurrentState: direction == 0),
                    FluentMotion.EnterAsync(PageHeaderContent, offset, TimeSpan.FromMilliseconds(180),
                        cancellationToken: motionToken, resumeFromCurrentState: direction == 0));
                _pageTransitionEntering = false;
                if (_closed) return;

                if (!_navigation.HasPendingPage)
                {
                    // Also settles A -> B -> A when the entrance was already cancelled for B.
                    FluentMotion.Reset(currentPage);
                    FluentMotion.Reset(PageHeaderContent);
                    currentPage.IsHitTestVisible = true;
                    ApplyQueuedNavigationFocus(tag);
                }
            }
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                _environment.ReportError("Page transition failed", exception);
                ApplyRequestedPage();
                var currentPage = PageFor(_navigation.CurrentPage);
                FluentMotion.Reset(currentPage);
                FluentMotion.Reset(PageHeaderContent);
                currentPage.IsHitTestVisible = true;
                ApplyQueuedNavigationFocus(_navigation.CurrentPage);
            }
        }
        finally
        {
            _pageTransitionEntering = false;
            _pageTransitionRunning = false;
        }
    }

    private void ApplyRequestedPage()
    {
        var change = _navigation.CommitRequested();
        var tag = change.To;
        var pageChanged = change.Direction != 0;
        MessagesPage.Visibility = tag == "messages" ? Visibility.Visible : Visibility.Collapsed;
        PeoplePage.Visibility = tag == "people" ? Visibility.Visible : Visibility.Collapsed;
        AccountPage.Visibility = tag == "account" ? Visibility.Visible : Visibility.Collapsed;
        ActivityLogsPage.Visibility = tag == "logs" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPageHost.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (pageChanged && _loaded)
        {
            _environment.AddLog("Navigation", "Page opened", tag);
        }

        if (tag == "account")
        {
            RefreshAccountPage();
        }
        else if (tag == "logs")
        {
            ActivityLogsPage.Refresh();
        }
        else if (tag == "settings" && pageChanged)
        {
            SettingsPageHost.LoadDraft();
        }

        UpdatePageHeader();
    }

    private FrameworkElement PageFor(string tag) => tag switch
    {
        "people" => PeoplePage,
        "account" => AccountPage,
        "logs" => ActivityLogsPage,
        "settings" => SettingsPageHost,
        _ => MessagesPage,
    };

    private void UpdatePageHeader()
    {
        switch (_navigation.CurrentPage)
        {
            case "people":
                PageHeaderIcon.Glyph = "\uE716";
                ChatTitleText.Text = "People & groups";
                ChatSubtitleText.Text = "Find conversations, review requests, and manage groups";
                RoundTripTextBlock.Visibility = Visibility.Collapsed;
                break;
            case "account":
                PageHeaderIcon.Glyph = "\uE77B";
                ChatTitleText.Text = "Account & bot";
                ChatSubtitleText.Text = _environment.CurrentPersona is { } persona
                    ? $"Current user · {persona.Id}"
                    : "Create or select a sending identity";
                RoundTripTextBlock.Visibility = Visibility.Collapsed;
                break;
            case "logs":
                PageHeaderIcon.Glyph = "\uE8A5";
                ChatTitleText.Text = "Activity logs";
                ChatSubtitleText.Text = "Application diagnostics and protocol traffic";
                RoundTripTextBlock.Visibility = Visibility.Collapsed;
                break;
            case "settings":
                PageHeaderIcon.Glyph = "\uE713";
                ChatTitleText.Text = "Settings";
                ChatSubtitleText.Text = "Connection, identity, appearance, and app information";
                RoundTripTextBlock.Visibility = Visibility.Collapsed;
                break;
            default:
                PageHeaderIcon.Glyph = "\uE8BD";
                ChatTitleText.Text = _environment.SelectedChatTitle;
                ChatSubtitleText.Text = _environment.SelectedChat?.Scene == ChatScene.Group
                    && !_environment.CurrentPersonaBelongsToSelectedGroup
                        ? $"{_environment.SelectedChatSubtitle} · Not joined; view only"
                        : _environment.SelectedChatSubtitle;
                RoundTripTextBlock.Text = _environment.RoundTripText;
                RoundTripTextBlock.Visibility = Visibility.Visible;
                break;
        }
    }

    private void ShowAccountPage()
    {
        if (ReferenceEquals(AppNavigationView.SelectedItem, AccountNavigationItem))
        {
            ShowPage("account");
        }
        else
        {
            AppNavigationView.SelectedItem = AccountNavigationItem;
        }
    }

    private async void SetProtocolPaneVisible(bool visible)
    {
        if (_closed)
        {
            return;
        }

        if (visible == _protocolPaneVisible)
        {
            return;
        }

        var motionToken = RestartMotion(ref _protocolMotionCancellation);
        var transitionVersion = ++_protocolPanelTransitionVersion;
        var resumeTransition = _protocolPanelClosing;
        if (visible)
        {
            _protocolPanelClosing = false;
            _protocolPaneVisible = true;
            ProtocolInspectorHost.SetActive(true);
            UpdateUtilityPanels();
            FluentMotion.Pulse(ToggleProtocolIcon, 1.12f, 5f);
            await FluentMotion.EnterAsync(
                ProtocolPanel,
                new System.Numerics.Vector3(24, 0, 0),
                TimeSpan.FromMilliseconds(200),
                cancellationToken: motionToken,
                resumeFromCurrentState: resumeTransition);

            return;
        }

        _protocolPaneVisible = false;
        ProtocolInspectorHost.SetActive(false);
        _protocolPanelClosing = true;
        UpdateUtilityPanels();
        FluentMotion.Pulse(ToggleProtocolIcon, 1.08f, -4f);
        await FluentMotion.ExitAsync(
            ProtocolPanel,
            new System.Numerics.Vector3(24, 0, 0),
            TimeSpan.FromMilliseconds(140),
            cancellationToken: motionToken);
        if (transitionVersion != _protocolPanelTransitionVersion)
        {
            return;
        }

        _protocolPanelClosing = false;
        UpdateUtilityPanels();
        FluentMotion.Reset(ProtocolPanel);
    }

    private async void SetConsoleVisible(bool visible)
    {
        if (_closed)
        {
            return;
        }

        if (visible == _consoleVisible)
        {
            if (visible)
            {
                RefreshInlineConsole();
            }

            return;
        }

        var motionToken = RestartMotion(ref _consoleMotionCancellation);
        var transitionVersion = ++_consolePanelTransitionVersion;
        var resumeTransition = _consolePanelClosing;
        if (visible)
        {
            _consolePanelClosing = false;
            _consoleVisible = true;
            RefreshInlineConsole();
            UpdateUtilityPanels();
            FluentMotion.Pulse(ToggleConsoleIcon, 1.12f, 5f);
            await FluentMotion.EnterAsync(
                ConsolePanel,
                new System.Numerics.Vector3(0, 24, 0),
                TimeSpan.FromMilliseconds(200),
                cancellationToken: motionToken,
                resumeFromCurrentState: resumeTransition);

            return;
        }

        _consoleVisible = false;
        _consolePanelClosing = true;
        _inlineConsoleRefreshTimer.Stop();
        UpdateUtilityPanels();
        FluentMotion.Pulse(ToggleConsoleIcon, 1.08f, -4f);
        await FluentMotion.ExitAsync(
            ConsolePanel,
            new System.Numerics.Vector3(0, 24, 0),
            TimeSpan.FromMilliseconds(140),
            cancellationToken: motionToken);
        if (transitionVersion != _consolePanelTransitionVersion)
        {
            return;
        }

        _consolePanelClosing = false;
        UpdateUtilityPanels();
        FluentMotion.Reset(ConsolePanel);
    }

    private void UpdateUtilityPanels()
    {
        var availableWidth = WorkspaceLayout.ActualWidth;
        var availableHeight = WorkspaceLayout.ActualHeight;
        var renderProtocolPanel = _protocolPaneVisible || _protocolPanelClosing;
        var useOverlayProtocol = availableWidth < 960;
        if (_protocolPaneOverlay != useOverlayProtocol)
        {
            _protocolPaneOverlay = useOverlayProtocol;
            Grid.SetColumn(ProtocolPanel, useOverlayProtocol ? 0 : 1);
            Grid.SetColumnSpan(ProtocolPanel, useOverlayProtocol ? 2 : 1);
            ProtocolPanel.HorizontalAlignment = useOverlayProtocol
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Stretch;
            Canvas.SetZIndex(ProtocolPanel, useOverlayProtocol ? 20 : 0);
        }

        var protocolColumnWidth = new GridLength(renderProtocolPanel && !useOverlayProtocol ? 400 : 0);
        if (ProtocolPaneColumn.Width != protocolColumnWidth)
        {
            ProtocolPaneColumn.Width = protocolColumnWidth;
        }

        var protocolWidth = useOverlayProtocol ? Math.Min(400, availableWidth) : double.NaN;
        if (!ProtocolPanel.Width.Equals(protocolWidth))
        {
            ProtocolPanel.Width = protocolWidth;
        }

        ProtocolPanel.IsHitTestVisible = _protocolPaneVisible;
        ProtocolPanel.Visibility = renderProtocolPanel ? Visibility.Visible : Visibility.Collapsed;

        var stackConsole = availableWidth < 720;
        UpdateInlineConsoleLayout(stackConsole);
        InlineConsoleStatusText.Visibility = availableWidth < 480
            ? Visibility.Collapsed
            : Visibility.Visible;
        var desiredConsoleHeight = stackConsole
            ? Math.Clamp(availableHeight * 0.55, 260, 420)
            : Math.Clamp(availableHeight * 0.34, 210, 280);
        var consoleHeight = Math.Min(desiredConsoleHeight, Math.Max(0, availableHeight - 180));
        var renderConsolePanel = _consoleVisible || _consolePanelClosing;
        var consoleRowHeight = renderConsolePanel
            ? new GridLength(consoleHeight)
            : new GridLength(0);
        if (ConsoleRow.Height != consoleRowHeight)
        {
            ConsoleRow.Height = consoleRowHeight;
        }
        ConsolePanel.IsHitTestVisible = _consoleVisible;
        ConsolePanel.Visibility = renderConsolePanel ? Visibility.Visible : Visibility.Collapsed;
        _syncingUtilityToggles = true;
        try
        {
            ToggleProtocolPaneButton.IsChecked = _protocolPaneVisible;
            ToggleConsoleButton.IsChecked = _consoleVisible;
        }
        finally
        {
            _syncingUtilityToggles = false;
        }
    }

    private void UpdateInlineConsoleLayout(bool stacked)
    {
        if (_inlineConsoleStacked == stacked)
        {
            return;
        }

        _inlineConsoleStacked = stacked;
        if (stacked)
        {
            InlineConsoleListColumn.Width = new GridLength(1, GridUnitType.Star);
            InlineConsoleSpacerColumn.Width = new GridLength(0);
            InlineConsoleDetailColumn.Width = new GridLength(0);
            InlineConsoleListRow.Height = new GridLength(1, GridUnitType.Star);
            InlineConsoleDetailRow.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(InlineConsoleList, 0);
            Grid.SetColumn(InlineConsoleList, 0);
            Grid.SetColumnSpan(InlineConsoleList, 3);
            Grid.SetRow(InlineConsoleDetailPanel, 1);
            Grid.SetColumn(InlineConsoleDetailPanel, 0);
            Grid.SetColumnSpan(InlineConsoleDetailPanel, 3);
            InlineConsoleDetailPanel.Margin = new Thickness(0, 8, 0, 0);
            return;
        }

        InlineConsoleListColumn.Width = new GridLength(2, GridUnitType.Star);
        InlineConsoleSpacerColumn.Width = new GridLength(10);
        InlineConsoleDetailColumn.Width = new GridLength(3, GridUnitType.Star);
        InlineConsoleListRow.Height = new GridLength(1, GridUnitType.Star);
        InlineConsoleDetailRow.Height = new GridLength(0);
        Grid.SetRow(InlineConsoleList, 0);
        Grid.SetColumn(InlineConsoleList, 0);
        Grid.SetColumnSpan(InlineConsoleList, 1);
        Grid.SetRow(InlineConsoleDetailPanel, 0);
        Grid.SetColumn(InlineConsoleDetailPanel, 2);
        Grid.SetColumnSpan(InlineConsoleDetailPanel, 1);
        InlineConsoleDetailPanel.Margin = new Thickness(0);
    }

    private async void ApplyAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountSendingIdentityBox.SelectedItem is not PersonaItem active || AccountBotBox.SelectedItem is not PersonaItem bot)
        {
            ShowErrorMessage("Choose both a sending identity and protocol bot account.");
            return;
        }
        try { await _environment.ApplyPreferencesAsync(_environment.Preferences with { ActiveUserId = active.Id, BotUserId = bot.Id }); }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendComposerAsync();

    private void ComposerTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateChatState();

    private async void ComposerTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if ((shiftState & CoreVirtualKeyStates.Down) != 0)
        {
            return;
        }

        e.Handled = true;
        await SendComposerAsync();
    }

    private async Task SendComposerAsync()
    {
        if (Interlocked.Exchange(ref _sendInProgress, 1) != 0)
        {
            return;
        }

        if (!_environment.CanCompose)
        {
            ShowErrorMessage("Select a compatible conversation and connect a ready protocol session before sending.");
            Volatile.Write(ref _sendInProgress, 0);
            return;
        }

        var chat = _environment.SelectedChat;
        var draftKey = _activeDraftKey;
        var senderId = _environment.CurrentPersona?.Id;
        var text = ComposerTextBox.Text;
        var attachments = _attachments.ToArray();
        var mentions = _mentions.ToArray();
        var reply = _reply;
        var richContent = _richContent.ToArray();
        var anonymous = _anonymous;
        try
        {
            if (chat is null || draftKey is null || senderId is null)
            {
                throw new InvalidOperationException("The active conversation or sending identity is no longer available.");
            }

            if (HasPendingAttachmentImport(draftKey))
            {
                throw new InvalidOperationException("Wait for pending attachment imports to finish before sending.");
            }

            if (string.IsNullOrWhiteSpace(text) && attachments.Length == 0 && mentions.Length == 0 && richContent.Length == 0)
            {
                throw new InvalidOperationException("Enter a message, mention someone, or attach a file first.");
            }

            _drafts[draftKey] = new ComposerDraft(text, mentions, attachments, reply, richContent, anonymous);
            UpdateChatState();
            FluentMotion.Pulse(SendButtonIcon, 1.2f, -8f);
            if (mentions.Length > 0)
            {
                if (chat.Scene != ChatScene.Group)
                {
                    throw new InvalidOperationException("Mentions can only be sent in a group conversation.");
                }

                if (mentions.Any(mention => mention.UserId is null) && mentions.Length > 1)
                {
                    throw new InvalidOperationException("@everyone cannot be combined with individual mentions.");
                }

                var roster = await _environment.GetRosterAsync(chat.PeerId);
                EnsureDraftContextIsCurrent(draftKey, chat.Id);
                var memberIds = roster.Select(item => item.User.Id).ToHashSet(StringComparer.Ordinal);
                if (mentions.Any(mention => mention.UserId is { } userId
                    && (userId == senderId || !memberIds.Contains(userId))))
                {
                    throw new InvalidOperationException("The group roster changed. Review the selected mentions and try again.");
                }
            }

            EnsureDraftContextIsCurrent(draftKey, chat.Id);
            var segments = new List<MessageSegment>();
            if (anonymous)
            {
                if (!_environment.Capabilities.SupportsAnonymous(chat.Scene))
                {
                    throw new InvalidOperationException("Anonymous messages are only available in OneBot V11 group conversations.");
                }
                segments.Add(new AnonymousSegment(Ignore: false));
            }
            if (reply is not null)
            {
                segments.Add(new ReplySegment(reply.MessageId, reply.SenderId));
            }
            segments.AddRange(mentions.Select(mention => mention.ToSegment()));
            if (!string.IsNullOrWhiteSpace(text))
            {
                segments.Add(MessageSegment.FromText(text.Trim()));
            }

            segments.AddRange(attachments.Select(attachment => attachment.ToSegment()));
            segments.AddRange(richContent);
            await _environment.SendSegmentsAsync(segments);
            _drafts.Remove(draftKey);
            if (string.Equals(_activeDraftKey, draftKey, StringComparison.Ordinal)
                && _environment.SelectedChat?.Id == chat.Id)
            {
                ComposerTextBox.Text = string.Empty;
                _attachments.Clear();
                _mentions.Clear();
                _reply = null;
                _richContent.Clear();
                _anonymous = false;

                ComposerTextBox.Focus(FocusState.Programmatic);
                FluentMotion.Pulse(ComposerPulseHost, 1.006f);
            }
        }
        catch (Exception exception)
        {
            _environment.ReportError("Message send failed", exception);
            ShowError(exception);
        }
        finally
        {
            Volatile.Write(ref _sendInProgress, 0);
            UpdateChatState();
        }
    }

    private void AnonymousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_environment.CanCompose && Volatile.Read(ref _sendInProgress) == 0
            && _environment.SelectedChat is { } chat && _environment.Capabilities.SupportsAnonymous(chat.Scene)
            && _activeDraftKey is not null && string.Equals(_activeDraftKey, GetCurrentDraftKey(), StringComparison.Ordinal))
        {
            _anonymous = AnonymousButton.IsChecked == true;
            SaveActiveDraft();
        }
        UpdateChatState();
        UpdateEnvironmentText();
    }

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await PickAttachmentsAsync();
        }
        catch (Exception exception)
        {
            _environment.ReportError("File picker failed", exception);
            ShowError(exception);
        }
    }

    private async void MentionButton_Click(object sender, RoutedEventArgs e)
    {
        var chat = _environment.SelectedChat;
        var draftKey = _activeDraftKey;
        var senderId = _environment.CurrentPersona?.Id;
        if (chat?.Scene != ChatScene.Group || draftKey is null || senderId is null)
        {
            ShowErrorMessage("Mentions are available in group conversations.");
            return;
        }

        if (!TryEnterDialog())
        {
            return;
        }

        try
        {
            var roster = await _environment.GetRosterAsync(chat.PeerId);
            EnsureDraftContextIsCurrent(draftKey, chat.Id);
            var choices = new List<MentionChoice> { new(null, "@everyone") };
            choices.AddRange(roster
                .Where(item => item.User.Id != senderId)
                .Select(item => new MentionChoice(
                    item.User.Id,
                    $"@{(string.IsNullOrWhiteSpace(item.Member.Card) ? item.User.DisplayName : item.Member.Card)} · {item.User.Id}")));
            var choiceBox = new ComboBox
            {
                Header = "Group member",
                ItemsSource = choices,
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var dialog = CreateDialog("Add mention", "Add", choiceBox);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || choiceBox.SelectedItem is not MentionChoice selected)
            {
                return;
            }

            EnsureDraftContextIsCurrent(draftKey, chat.Id);
            if (selected.UserId is null)
            {
                _mentions.Clear();
                _mentions.Add(new MentionDraft(null, "everyone"));
            }
            else if (selected.UserId != senderId
                && _mentions.All(mention => mention.UserId != selected.UserId))
            {
                foreach (var everyone in _mentions.Where(mention => mention.UserId is null).ToArray())
                {
                    _mentions.Remove(everyone);
                }

                _mentions.Add(new MentionDraft(selected.UserId, selected.Label.TrimStart('@').Split('·')[0].Trim()));
            }

            SaveActiveDraft();
            UpdateChatState();
        }
        catch (Exception exception)
        {
            _environment.ReportError("Mention selection failed", exception);
            ShowError(exception);
        }
        finally
        {
            ExitDialog();
        }
    }

    private async Task PickAttachmentsAsync()
    {
        if (Volatile.Read(ref _sendInProgress) != 0)
        {
            throw new InvalidOperationException("Wait for the current message to finish sending before attaching files.");
        }

        var draftKey = _activeDraftKey
            ?? throw new InvalidOperationException("Select a conversation before attaching files.");
        SaveActiveDraft();
        BeginAttachmentImport(draftKey);
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            foreach (var extension in _environment.Capabilities.Files ? new[] { "*" }
                : new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".mp4", ".mov", ".mkv", ".mp3", ".wav", ".ogg", ".m4a", ".amr", ".silk" })
            {
                picker.FileTypeFilter.Add(extension);
            }
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            await AddAttachmentsAsync(files.OfType<StorageFile>(), draftKey);
        }
        finally
        {
            EndAttachmentImport(draftKey);
        }
    }

    private void Composer_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)
            && _activeDraftKey is not null
            && _environment.CanCompose
            && Volatile.Read(ref _sendInProgress) == 0)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Attach to message";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void Composer_Drop(object sender, DragEventArgs e)
    {
        string? draftKey = null;
        var importStarted = false;
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            if (Volatile.Read(ref _sendInProgress) != 0)
            {
                throw new InvalidOperationException("Wait for the current message to finish sending before attaching files.");
            }

            draftKey = _activeDraftKey
                ?? throw new InvalidOperationException("Select a conversation before attaching files.");
            SaveActiveDraft();
            BeginAttachmentImport(draftKey);
            importStarted = true;
            var items = await e.DataView.GetStorageItemsAsync();
            await AddAttachmentsAsync(items.OfType<StorageFile>(), draftKey);
        }
        catch (Exception exception)
        {
            _environment.ReportError("Dropped files could not be read", exception);
            ShowError(exception);
        }
        finally
        {
            if (importStarted && draftKey is not null)
            {
                EndAttachmentImport(draftKey);
            }
        }
    }

    private async Task AddAttachmentsAsync(IEnumerable<StorageFile> files, string draftKey)
    {
        try
        {
            foreach (var file in files)
            {
                if (!_environment.SupportsMessageAttachment(file))
                {
                    throw new InvalidOperationException("The selected protocol supports media attachments only.");
                }
                var attachment = await _environment.CreateAttachmentAsync(file);
                if (!_environment.Capabilities.SupportsSegment(attachment.ToSegment()))
                {
                    throw new InvalidOperationException("The selected protocol supports media attachments only.");
                }
                AddAttachmentToDraft(draftKey, attachment);
            }

            UpdateChatState();
        }
        catch (Exception exception)
        {
            _environment.ReportError("Attachment import failed", exception);
            ShowError(exception);
        }
    }

    private void AddAttachmentToDraft(string draftKey, AttachmentDraft attachment)
    {
        if (string.Equals(_activeDraftKey, draftKey, StringComparison.Ordinal))
        {
            if (_attachments.All(existing => existing.Asset.Id != attachment.Asset.Id))
            {
                _attachments.Add(attachment);
                SaveActiveDraft();
            }

            return;
        }

        var draft = _drafts.TryGetValue(draftKey, out var existingDraft)
            ? existingDraft
            : new ComposerDraft(string.Empty, [], []);
        if (draft.Attachments.All(existing => existing.Asset.Id != attachment.Asset.Id))
        {
            _drafts[draftKey] = draft with
            {
                Attachments = draft.Attachments.Append(attachment).ToArray(),
            };
        }
    }

    private void BeginAttachmentImport(string draftKey)
    {
        _pendingAttachmentImports[draftKey] = _pendingAttachmentImports.GetValueOrDefault(draftKey) + 1;
        UpdateChatState();
    }

    private void EndAttachmentImport(string draftKey)
    {
        if (_pendingAttachmentImports.TryGetValue(draftKey, out var count) && count > 1)
        {
            _pendingAttachmentImports[draftKey] = count - 1;
        }
        else
        {
            _pendingAttachmentImports.Remove(draftKey);
        }

        UpdateChatState();
    }

    private bool HasPendingAttachmentImport(string? draftKey) => draftKey is not null
        && _pendingAttachmentImports.GetValueOrDefault(draftKey) > 0;

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        var attachment = (sender as FrameworkElement)?.DataContext as AttachmentDraft
            ?? AttachmentList.SelectedItem as AttachmentDraft;
        if (attachment is not null)
        {
            _attachments.Remove(attachment);
            UpdateChatState();
        }
    }

    private void RemoveMention_Click(object sender, RoutedEventArgs e)
    {
        if (MentionList.SelectedItem is MentionDraft mention)
        {
            _mentions.Remove(mention);
            UpdateChatState();
        }
    }

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMessageActions();

    private async void OpenAttachment_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var asset = GetSelectedAsset() ?? throw new InvalidOperationException("Select a message with an attachment.");
            if (!_environment.Assets.Exists(asset.Id))
            {
                throw new FileNotFoundException("The attachment is not available in the local asset cache.", asset.Name);
            }

            var file = await StorageFile.GetFileFromPathAsync(_environment.Assets.LocationOf(asset.Id));
            if (!await Launcher.LaunchFileAsync(file))
            {
                throw new InvalidOperationException("Windows could not find an app that can open this attachment.");
            }
        }
        catch (Exception exception)
        {
            _environment.ReportError("Attachment open failed", exception);
            ShowError(exception);
        }
    }

    private async void SaveAttachment_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var asset = GetSelectedAsset() ?? throw new InvalidOperationException("Select a message with an attachment.");
            if (!_environment.Assets.Exists(asset.Id))
            {
                throw new FileNotFoundException("The attachment is not available in the local asset cache.", asset.Name);
            }

            var extension = Path.GetExtension(asset.Name);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".bin";
            }

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = Path.GetFileNameWithoutExtension(asset.Name),
            };
            picker.FileTypeChoices.Add("Attachment", new List<string> { extension });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var destination = await picker.PickSaveFileAsync();
            if (destination is null)
            {
                return;
            }

            var source = await StorageFile.GetFileFromPathAsync(_environment.Assets.LocationOf(asset.Id));
            await source.CopyAndReplaceAsync(destination);
        }
        catch (Exception exception)
        {
            _environment.ReportError("Attachment save failed", exception);
            ShowError(exception);
        }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var chat = _environment.SelectedChat;
        if (chat is null || !TryEnterDialog())
        {
            return;
        }

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Clear this conversation?",
                Content = "All locally stored messages in this conversation will be removed. This cannot be undone.",
                PrimaryButtonText = "Clear history",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (_environment.SelectedChat?.Id != chat.Id)
                {
                    throw new InvalidOperationException("The active conversation changed before history could be cleared.");
                }

                await _environment.ClearSelectedConversationHistoryAsync();
            }
        }
        catch (Exception exception)
        {
            _environment.ReportError("Conversation history could not be cleared", exception);
            ShowError(exception);
        }
        finally
        {
            ExitDialog();
        }
    }

    private Asset? GetSelectedAsset()
    {
        if (SelectedMessage() is not { } item)
        {
            return null;
        }

        foreach (var segment in item.Message.Content)
        {
            var asset = segment switch
            {
                ImageSegment image => image.Asset,
                RecordSegment record => record.Asset,
                AudioSegment audio => audio.Asset,
                VideoSegment video => video.Asset,
                FileSegment file => file.Asset,
                _ => null,
            };
            if (asset is not null)
            {
                return asset;
            }
        }

        return null;
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as MessageItem
            ?? (sender as FrameworkElement)?.DataContext as MessageItem
            ?? SelectedMessage();
        if (item is not null)
        {
            CopyText(item.Text);
        }
    }

    private void CopyMessageId_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as MessageItem
            ?? (sender as FrameworkElement)?.DataContext as MessageItem
            ?? SelectedMessage();
        if (item is not null)
        {
            CopyText(item.Id);
        }
    }

    private async void RecallMessage_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as MessageItem
            ?? (sender as FrameworkElement)?.DataContext as MessageItem
            ?? SelectedMessage();
        if (item is null)
        {
            return;
        }

        try
        {
            await _environment.RecallMessageAsync(item.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void CopyConversationId_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as ConversationItem
            ?? (sender as FrameworkElement)?.DataContext as ConversationItem
            ?? CurrentConversation();
        if (item is not null)
        {
            CopyText(item.Chat.PeerId);
        }
    }

    private async void RemoveConversationEntity_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as ConversationItem
            ?? (sender as FrameworkElement)?.DataContext as ConversationItem
            ?? CurrentConversation();
        if (item is null
            || _environment.BotPersona is not { } botPersona)
        {
            return;
        }

        if (item.Chat.Scene == ChatScene.Temp)
        {
            return;
        }

        if (item.Chat.Scene == ChatScene.Group)
        {
            var currentPersona = _environment.CurrentPersona;
            var membership = currentPersona is null
                ? null
                : await _environment.Store.GetMemberAsync(item.Chat.PeerId, currentPersona.Id);
            if (membership?.Role != GroupRole.Owner)
            {
                ShowErrorMessage("Only the group owner can disband this group.");
                return;
            }
        }

        if (!TryEnterDialog())
        {
            return;
        }

        try
        {
            var isGroup = item.Chat.Scene == ChatScene.Group;
            var confirm = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = isGroup ? "Disband group?" : "Remove friendship?",
                Content = isGroup
                    ? $"This will disband {item.Title}, removing its members and messages. Group notifications and resolved request history are retained."
                    : $"This will remove the friendship with {item.Title}.",
                PrimaryButtonText = isGroup ? "Disband" : "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (item.Chat.Scene == ChatScene.Group)
            {
                var currentPersona = _environment.CurrentPersona
                    ?? throw new InvalidOperationException("No active persona.");
                await _environment.Platform.DeleteGroupAsync(item.Chat.PeerId, currentPersona.Id);
            }
            else
            {
                await _environment.Platform.RemoveFriendAsync(botPersona.Id, item.Chat.PeerId);
            }

            await _environment.SelectConversationAsync(null);
        }
        catch (Exception exception)
        {
            _environment.ReportError("Conversation removal failed", exception);
            ShowError(exception);
        }
        finally
        {
            ExitDialog();
        }
    }

    private async void AcceptRequest_Click(object sender, RoutedEventArgs e) => await ResolveRequestAsync(sender, true);

    private async void DeclineRequest_Click(object sender, RoutedEventArgs e) => await ResolveRequestAsync(sender, false);

    private async Task ResolveRequestAsync(object sender, bool approve)
    {
        var item = (sender as FrameworkElement)?.DataContext as PendingRequestItem
            ?? PendingRequestList.SelectedItem as PendingRequestItem;
        if (item is null)
        {
            return;
        }

        if (!_resolvingRequestFlags.Add(item.Request.Id)) return;
        try
        {
            await _environment.ResolveRequestAsync(item.Request, approve, approve ? string.Empty : "Declined in Asuka");
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            _resolvingRequestFlags.Remove(item.Request.Id);
        }
    }

    private void Environment_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateEnvironmentText();
        UpdateChatState();
    }

    private async void Environment_PreferencesChanged(object? sender, EventArgs e)
    {
        SaveActiveDraft();
        ApplyTheme();
        RestoreDraftForCurrentContext();
        UpdateEnvironmentText();
        RefreshMessageVisuals();
        UpdatePendingRequests();
        UpdateChatState();
        await RefreshSelectedGroupAuthorityAsync();
    }

    private async void Environment_SelectedChatChanged(object? sender, EventArgs e)
    {
        _currentUserOwnsSelectedGroup = false;
        SaveActiveDraft();
        RestoreDraftForCurrentContext();
        UpdateEnvironmentText();
        UpdateChatState();
        await RefreshSelectedGroupAuthorityAsync();
    }

    private void Conversations_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_conversationRefreshTimer.IsRunning)
        {
            _conversationRefreshTimer.Start();
        }
    }

    private void Personas_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshAccountPage();

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_consoleVisible)
        {
            ScheduleInlineConsoleRefresh();
        }
    }

    private void RawTraffic_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_consoleVisible)
        {
            ScheduleInlineConsoleRefresh();
        }
    }

    private void ScheduleInlineConsoleRefresh()
    {
        if (!_inlineConsoleRefreshTimer.IsRunning)
        {
            _inlineConsoleRefreshTimer.Start();
        }
    }

    private void RefreshInlineConsole()
    {
        if (InlineConsoleCategoryBox is null || InlineConsoleSearchBox is null)
        {
            return;
        }

        var protocolEntries = _environment.RawTraffic.ToHashSet();
        var allEntries = _environment.Logs
            .Concat(_environment.RawTraffic)
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();
        var query = InlineConsoleSearchBox.Text.Trim();
        var category = InlineConsoleCategoryBox.SelectedIndex;
        var filtered = allEntries.Where(entry =>
            (query.Length == 0 || entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            && (category == 0
                || (category == 1 && !protocolEntries.Contains(entry))
                || (category == 2 && protocolEntries.Contains(entry))))
            .ToList();

        var selectedEntry = InlineConsoleList.SelectedItem as LogEntryItem;
        CollectionSync.Apply(_inlineConsoleEntries, filtered);

        InlineConsoleStatusText.Text = $"{filtered.Count}/{allEntries.Count}";
        if (selectedEntry is not null && filtered.Contains(selectedEntry))
        {
            InlineConsoleList.SelectedItem = selectedEntry;
        }
        else
        {
            InlineConsoleDetailHeader.Text = "Select an activity entry";
            InlineConsoleDetailBox.Text = string.Empty;
        }
    }

    private void InlineConsoleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InlineConsoleList.SelectedItem is LogEntryItem entry)
        {
            InlineConsoleDetailHeader.Text = entry.Header;
            InlineConsoleDetailBox.Text = entry.Detail;
        }
        else
        {
            InlineConsoleDetailHeader.Text = "Select an activity entry";
            InlineConsoleDetailBox.Text = string.Empty;
        }
    }

    private void InlineConsoleClear_Click(object sender, RoutedEventArgs e)
    {
        _environment.ClearLogs();
        _environment.RawTraffic.Clear();
        InlineConsoleDetailHeader.Text = "Select an activity entry";
        InlineConsoleDetailBox.Text = string.Empty;
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_messageRefreshTimer.IsRunning)
        {
            _messageRefreshTimer.Start();
        }
    }

    private void RefreshMessagesAfterCollectionChange()
    {
        var hasNewMessages = _environment.Messages.Any(item => !_renderedMessageIds.Contains(item.Id));
        RefreshMessageVisuals();
        UpdateChatState();
        if (hasNewMessages && MessageList.Items.Count > 0)
        {
            if (_environment.IsShowcaseMode) FollowShowcaseMessages();
            else MessageList.ScrollIntoView(MessageList.Items[MessageList.Items.Count - 1], ScrollIntoViewAlignment.Leading);
        }
    }

    private void RefreshMessageVisuals()
    {
        var animateNewItems = _loaded && _navigation.CurrentPage == "messages";
        var newContainers = new List<ListViewItem>();
        var activeIds = _environment.Messages.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removedId in _messageRows.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            MessageList.Items.Remove(_messageRows[removedId].Container);
            _messageRows.Remove(removedId);
        }

        var position = 0;
        foreach (var item in _environment.Messages)
        {
            var quotes = item.Message.Content.OfType<ReplySegment>().Select(reply =>
            {
                var target = _environment.Messages.FirstOrDefault(message => message.Id == reply.MessageId);
                return new { reply.MessageId, Available = target is not null, Sender = target?.Sender, Text = target?.Text, Recalled = target?.Message.IsRecalled };
            });
            var key = new MessageRenderKey(JsonSerializer.Serialize(item.Message.Content),
                item.Message.IsRecalled, item.Alignment, _environment.Preferences.Protocol, JsonSerializer.Serialize(quotes));
            if (!_messageRows.TryGetValue(item.Id, out var row) || row.Key != key)
            {
                if (row is not null) MessageList.Items.Remove(row.Container);
                var reactions = new StackPanel();
                var body = new StackPanel
                {
                    Spacing = 6,
                    RequestedTheme = item.UseDefaultForeground ? ElementTheme.Default : ElementTheme.Dark,
                };
                body.Children.Add(BuildMessageContent(item));
                body.Children.Add(reactions);
                var panel = new StackPanel { MaxWidth = 720, HorizontalAlignment = item.Alignment, Spacing = 3 };
                var meta = new TextBlock { Text = item.Meta, Opacity = 0.62, HorizontalAlignment = item.MetaAlignment };
                panel.Children.Add(meta);
                panel.Children.Add(new Border
                {
                    Background = item.BubbleBrush,
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12, 8, 12, 8),
                    Child = body,
                });
                row = new MessageRow(new ListViewItem
                {
                    Content = panel,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 3, 0, 3),
                }, key, reactions, meta);
                _messageRows[item.Id] = row;
            }

            var container = row.Container;
            row.Meta.Text = item.Meta;
            container.Tag = item;
            container.ContextFlyout = CreateMessageContextFlyout(container, item);
            AutomationProperties.SetName(container, item.AccessibilityLabel);
            row.Reactions.Children.Clear();
            if (!item.Message.IsRecalled && item.Reactions.Count > 0 && _environment.Capabilities.SupportsReactions(item.Message.Scene))
            {
                row.Reactions.Children.Add(CreateReactionBar(item));
            }

            if (position >= MessageList.Items.Count || !ReferenceEquals(MessageList.Items[position], container))
            {
                MessageList.Items.Remove(container);
                MessageList.Items.Insert(position, container);
            }

            position++;
            if (animateNewItems && !_renderedMessageIds.Contains(item.Id))
            {
                newContainers.Add(container);
            }
        }

        _renderedMessageIds.Clear();
        foreach (var item in _environment.Messages)
        {
            _renderedMessageIds.Add(item.Id);
        }

        var animatedContainers = newContainers.TakeLast(8).ToArray();
        for (var index = 0; index < animatedContainers.Length; index++)
        {
            _ = FluentMotion.EnterAsync(
                animatedContainers[index],
                new System.Numerics.Vector3(0, 12, 0),
                TimeSpan.FromMilliseconds(180),
                TimeSpan.FromMilliseconds(index * 18),
                0.99f);
        }
    }

    private MessageItem? SelectedMessage() => MessageList.SelectedItem switch
    {
        MessageItem item => item,
        ListViewItem { Tag: MessageItem item } => item,
        _ => null,
    };

    private UIElement BuildMessageContent(MessageItem item)
    {
        var foreground = item.UseDefaultForeground ? null : item.ForegroundBrush;
        if (item.Message.IsRecalled)
        {
            var recalled = new TextBlock
            {
                Text = item.Text,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Opacity = foreground is null ? 0.72 : 1,
            };
            if (foreground is not null) recalled.Foreground = foreground;
            return recalled;
        }

        var content = new StackPanel { Spacing = 6 };
        foreach (var reply in item.Message.Content.OfType<ReplySegment>())
        {
            content.Children.Add(CreateReplyPreview(reply, foreground));
        }
        foreach (var segment in item.Message.Content.Where(segment => segment is not ReplySegment))
        {
            content.Children.Add(BuildSegmentContent(segment, foreground));
        }

        return content;
    }

    private UIElement BuildSegmentContent(MessageSegment segment, Brush? foreground = null) => segment switch
    {
        TextSegment text => MessageTextRenderer.Render(text.Text, foreground),
        MentionSegment mention => CreateSegmentBadge(mention.UserId is null ? "@everyone" : $"@{mention.UserId}", foreground),
        FaceSegment face => CreateSegmentBadge(face.Name is null ? $"Emoji {face.Id}" : face.Name, foreground),
        ImageSegment image => CreateImagePreview(image.Asset, foreground),
        RecordSegment record => CreateAudioPreview(record.Asset),
        AudioSegment audio => CreateAudioPreview(audio.Asset),
        LocationSegment location => CreateQuotedText($"{location.Title}\n{location.Content}\n{location.Latitude:0.######}, {location.Longitude:0.######}", foreground),
        VideoSegment video => CreateVideoPreview(video),
        FileSegment file => CreateAssetButton(file.Asset, $"Open file · {file.Asset.Name}"),
        ReplySegment reply => CreateReplyPreview(reply, foreground),
        PokeSegment poke => CreateSegmentBadge(poke.UserId is null ? "Nudge" : $"Nudge {poke.UserId}", foreground),
        ForwardSegment forward => CreateForwardPreview(forward, foreground),
        UnsupportedSegment unsupported => CreateQuotedText($"Unsupported segment: {unsupported.Type}", foreground),
        _ => CreateQuotedText(segment.TextPreview, foreground),
    };

    private static Border CreateSegmentBadge(string text, Brush? foreground = null)
    {
        var label = new TextBlock { Text = text };
        if (foreground is not null) label.Foreground = foreground;
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(7, 3, 7, 3),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 80, 140, 240)),
            Child = label,
        };
    }

    private static Border CreateQuotedText(string text, Brush? foreground = null)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = foreground is null ? 0.75 : 1 };
        if (foreground is not null) label.Foreground = foreground;
        return new Border
        {
            Padding = new Thickness(9, 6, 9, 6),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 80, 140, 240)),
            Child = label,
        };
    }

    private UIElement CreateImagePreview(Asset asset, Brush? foreground = null)
    {
        if (!_environment.Assets.Exists(asset.Id))
        {
            return CreateQuotedText($"Image unavailable · {asset.Name}", foreground);
        }

        var image = new Image
        {
            MaxWidth = 360,
            MaxHeight = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
            Stretch = Stretch.Uniform,
            Source = new BitmapImage(new Uri(_environment.Assets.LocationOf(asset.Id))) { AutoPlay = true },
        };
        AutomationProperties.SetName(image, $"Image attachment {asset.Name}");
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(image);
        panel.Children.Add(CreateAssetButton(asset, $"Open image · {asset.Name}"));
        return panel;
    }

    private Button CreateAssetButton(Asset asset, string label)
    {
        var button = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = asset,
        };
        AutomationProperties.SetName(button, label);
        button.Click += async (sender, _) =>
        {
            if ((sender as FrameworkElement)?.Tag is Asset selected)
            {
                await OpenAssetAsync(selected);
            }
        };
        return button;
    }

    private static Expander CreateForwardPreview(ForwardSegment forward, Brush? foreground = null)
    {
        var nodes = new StackPanel { Spacing = 5 };
        foreach (var node in forward.Nodes)
        {
            var label = new TextBlock
            {
                Text = $"{node.SenderName}: {node.Content.TextPreview()}",
                TextWrapping = TextWrapping.Wrap,
            };
            if (foreground is not null) label.Foreground = foreground;
            nodes.Children.Add(label);
        }

        return new Expander
        {
            Header = $"Forwarded messages ({forward.Nodes.Count})",
            Content = nodes,
        };
    }

    private async Task OpenAssetAsync(Asset asset)
    {
        try
        {
            if (!_environment.Assets.Exists(asset.Id))
            {
                throw new FileNotFoundException("The attachment is not available in the local asset cache.", asset.Name);
            }

            using var cacheLease = await MediaService.AcquireCacheLeaseAsync();
            if (_closed) return;
            var source = _environment.Assets.LocationOf(asset.Id);
            var directory = Path.Combine(Path.GetTempPath(), "Asuka", "attachment-preview", asset.Id);
            Directory.CreateDirectory(directory);
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string(asset.Name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).TrimEnd(' ', '.');
            if (string.IsNullOrWhiteSpace(name)) name = "attachment.bin";
            if (name.Length > 220)
            {
                var extension = Path.GetExtension(name);
                var stem = Path.GetFileNameWithoutExtension(name);
                name = stem[..Math.Min(stem.Length, 200)] + extension[..Math.Min(extension.Length, 16)];
            }
            // A prefix avoids Windows device names and leaves the extension usable by the shell.
            var preview = Path.Combine(directory, "Asuka-" + name);
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(preview))
                await input.CopyToAsync(output);
            if (_closed) return;
            var file = await StorageFile.GetFileFromPathAsync(preview);
            if (_closed) return;
            if (!await Launcher.LaunchFileAsync(file))
            {
                throw new InvalidOperationException("Windows could not find an app that can open this attachment.");
            }
        }
        catch (Exception exception)
        {
            _environment.ReportError("Attachment open failed", exception);
            ShowError(exception);
        }
    }

    private void RefreshConversationFilter()
    {
        if (ConversationSearchBox is null || ConversationTypeBox is null) return;
        var query = ConversationSearchBox.Text.Trim();
        var selectedId = SelectedConversation()?.Id ?? _environment.SelectedChat?.Id;
        var type = ConversationTypeBox.SelectedIndex;
        var items = _environment.Conversations.Where(item =>
            (type == 0 || (type == 1 && item.Chat.Scene != ChatScene.Group) || (type == 2 && item.Chat.Scene == ChatScene.Group))
            && (query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Preview.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.DisplayId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Chat.PeerId.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.IsPinned).ToList();
        CollectionSync.Apply(_visibleConversations, items);
        ConversationList.SelectedItem = items.FirstOrDefault(item => item.Id == selectedId);
        var groups = _environment.Conversations.Count(item => item.Chat.Scene == ChatScene.Group);
        var people = _environment.Conversations.Count - groups;
        PeopleCountText.Text = $"{items.Count} shown · {people} {(people == 1 ? "person" : "people")} · {groups} groups";
        PeopleEmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var filtering = query.Length > 0 || type > 0;
        PeopleEmptyTitle.Text = filtering ? "No matching conversations" : "No conversations yet";
        PeopleEmptyDescription.Text = filtering ? "Try another name or ID, or change the filter." : "Use New to add a friend or create a group.";
        UpdatePendingRequests();
    }

    private void PendingRequests_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => UpdatePendingRequests();

    private void UpdatePendingRequests()
    {
        var count = _environment.PendingRequests.Count;
        PendingRequestsSummary.Text = count == 0 ? "No pending requests" : $"{count} pending requests";
        PendingRequestsExpander.Header = $"Pending requests ({count})";
        PendingRequestsExpander.Visibility = count > 0 && _environment.Capabilities.Requests
            ? Visibility.Visible : Visibility.Collapsed;
        PendingRequestsSummary.Visibility = _environment.Capabilities.Requests ? Visibility.Visible : Visibility.Collapsed;
        SimulateRequestMenuItem.Visibility = _environment.Capabilities.Requests ? Visibility.Visible : Visibility.Collapsed;
        RequestHistoryButton.Visibility = _environment.Capabilities.Requests ? Visibility.Visible : Visibility.Collapsed;
    }

    private ConversationItem? SelectedConversation() => ConversationList.SelectedItem switch
    {
        ConversationItem item => item,
        ListViewItem { Tag: ConversationItem item } => item,
        _ => null,
    };

    private ConversationItem? CurrentConversation()
    {
        var chatId = _environment.SelectedChat?.Id;
        if (chatId is not null)
        {
            return _environment.Conversations.FirstOrDefault(item => item.Id == chatId);
        }

        return null;
    }

    private MenuFlyout CreateMessageContextFlyout(ListViewItem container, MessageItem item)
    {
        var flyout = new MenuFlyout();
        void SelectTarget() => MessageList.SelectedItem = container;
        var copy = new MenuFlyoutItem { Text = "Copy text", Tag = item };
        copy.Click += (_, _) => { SelectTarget(); CopyText(item.Text); };
        var copyId = new MenuFlyoutItem { Text = "Copy message ID", Tag = item };
        copyId.Click += (_, _) => { SelectTarget(); CopyText(item.Id); };
        var recall = new MenuFlyoutItem
        {
            Text = "Recall",
            Tag = item,
            IsEnabled = !item.Message.IsRecalled,
        };
        recall.Click += (_, _) => { SelectTarget(); RecallMessage_Click(recall, new RoutedEventArgs()); };
        flyout.Items.Add(copy); flyout.Items.Add(copyId); flyout.Items.Add(recall);
        AddMessageInteractions(flyout, item);
        if (!item.Message.IsRecalled && item.Message.Content.Any(segment => segment is ImageSegment or RecordSegment or AudioSegment or VideoSegment or FileSegment))
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var open = new MenuFlyoutItem { Text = "Open", Tag = item };
            open.Click += (_, _) => { SelectTarget(); OpenAttachment_Click(open, new RoutedEventArgs()); };
            var save = new MenuFlyoutItem { Text = "Save as", Tag = item };
            save.Click += (_, _) => { SelectTarget(); SaveAttachment_Click(save, new RoutedEventArgs()); };
            flyout.Items.Add(open); flyout.Items.Add(save);
        }
        return flyout;
    }

    private MenuFlyout CreateConversationContextFlyout(ConversationItem item)
    {
        var flyout = new MenuFlyout();
        async Task SelectTargetAsync()
        {
            ConversationList.SelectedItem = item;
            if (_environment.SelectedChat?.Id != item.Id)
            {
                SaveActiveDraft();
                await _environment.SelectConversationAsync(item);
            }
        }

        var open = new MenuFlyoutItem { Text = "Open conversation", Tag = item };
        open.Click += async (_, _) => await OpenConversationPageAsync(item);
        var copy = new MenuFlyoutItem { Text = "Copy ID", Tag = item };
        copy.Click += (_, _) =>
        {
            ConversationList.SelectedItem = item;
            CopyText(item.DisplayId);
        };
        flyout.Items.Add(open); flyout.Items.Add(copy);
        AddConversationActions(flyout, item);
        AddGroupHonorActions(flyout, item.Chat);
        MenuFlyoutItem? manage = null;
        if (item.Chat.Scene == ChatScene.Group)
        {
            manage = new MenuFlyoutItem { Text = "Manage group", Tag = item, IsEnabled = false };
            manage.Click += async (_, _) =>
            {
                try
                {
                    await SelectTargetAsync();
                    ManageGroup_Click(manage, new RoutedEventArgs());
                }
                catch (Exception exception)
                {
                    ShowError(exception);
                }
            };
            flyout.Items.Add(manage);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var remove = new MenuFlyoutItem
        {
            Text = item.Chat.Scene == ChatScene.Group ? "Disband group" : "Remove friendship",
            Tag = item,
            IsEnabled = item.Chat.Scene == ChatScene.Friend,
        };
        remove.Click += async (_, _) =>
        {
            try
            {
                await SelectTargetAsync();
                RemoveConversationEntity_Click(remove, new RoutedEventArgs());
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
        };
        flyout.Items.Add(remove);
        if (item.Chat.Scene == ChatScene.Group)
        {
            flyout.Opening += async (_, _) =>
            {
                try
                {
                    var currentPersona = _environment.CurrentPersona;
                    var membership = currentPersona is null
                        ? null
                        : await _environment.Store.GetMemberAsync(item.Chat.PeerId, currentPersona.Id);
                    manage!.IsEnabled = membership is not null;
                    remove.IsEnabled = membership?.Role == GroupRole.Owner;
                }
                catch (Exception exception)
                {
                    manage!.IsEnabled = false;
                    remove.IsEnabled = false;
                    _environment.ReportError("Group permissions could not be loaded", exception);
                }
            };
        }

        return flyout;
    }

    private void UpdateChatState()
    {
        GroupNotificationsButton.Visibility = _environment.Capabilities.GroupNotifications ? Visibility.Visible : Visibility.Collapsed;
        var canCompose = _environment.CanCompose && Volatile.Read(ref _sendInProgress) == 0;
        var hasDraftContent = !string.IsNullOrWhiteSpace(ComposerTextBox.Text)
            || _mentions.Count > 0
            || _attachments.Count > 0
            || _richContent.Count > 0;
        var hasPendingImport = HasPendingAttachmentImport(_activeDraftKey);
        EmptyChatState.Visibility = _environment.SelectedChat is null ? Visibility.Visible : Visibility.Collapsed;
        ComposerBorder.Opacity = canCompose ? 1 : 0.65;
        ComposerTextBox.IsEnabled = canCompose;
        AttachButton.IsEnabled = canCompose;
        AttachButton.Text = _environment.Capabilities.Files ? "Attach file" : "Attach media";
        FaceButton.Visibility = _environment.Capabilities.Faces ? Visibility.Visible : Visibility.Collapsed;
        CustomFacesButton.Visibility = _environment.Capabilities.CustomFaces ? Visibility.Visible : Visibility.Collapsed;
        CustomFacesButton.IsEnabled = canCompose;
        LocationButton.Visibility = _environment.Capabilities.Locations ? Visibility.Visible : Visibility.Collapsed;
        AudioFileButton.Visibility = _environment.Capabilities.Audio ? Visibility.Visible : Visibility.Collapsed;
        FaceButton.IsEnabled = canCompose;
        LocationButton.IsEnabled = canCompose;
        AudioFileButton.IsEnabled = canCompose;
        RichContentRow.Visibility = _richContent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveRichContentButton.IsEnabled = canCompose && _richContent.Count > 0;
        MentionButton.IsEnabled = canCompose && _environment.SelectedChat?.Scene == ChatScene.Group;
        MentionButton.Visibility = _environment.SelectedChat?.Scene == ChatScene.Group ? Visibility.Visible : Visibility.Collapsed;
        var supportsAnonymous = _environment.SelectedChat is { } selectedChat
            && _environment.Capabilities.SupportsAnonymous(selectedChat.Scene);
        AnonymousButton.Visibility = supportsAnonymous ? Visibility.Visible : Visibility.Collapsed;
        AnonymousButton.IsEnabled = canCompose && supportsAnonymous;
        AnonymousButton.IsChecked = IsAnonymousDraftActive();
        UpdateSendingIdentityText();
        ReplyBanner.IsOpen = _reply is not null;
        ReplyBanner.Message = _reply is { } reply ? $"{reply.Sender}: {reply.Preview}" : string.Empty;
        RemoveMentionButton.IsEnabled = canCompose && _mentions.Count > 0;
        RemoveMentionButton.Visibility = _mentions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveAttachmentButton.IsEnabled = canCompose && _attachments.Count > 0;
        RemoveAttachmentButton.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SendButtonIcon.Glyph = "\uE74A";
        var sendStatusText = hasPendingImport
            ? "Importing…"
            : Volatile.Read(ref _sendInProgress) != 0 ? "Sending…" : string.Empty;
        SendStatusText.Text = sendStatusText;
        if (_loaded
            && sendStatusText.Length > 0
            && !string.Equals(sendStatusText, _lastSendStatusText, StringComparison.Ordinal))
        {
            _ = FluentMotion.EnterAsync(
                SendStatusText,
                new System.Numerics.Vector3(0, 5, 0),
                TimeSpan.FromMilliseconds(160),
                startScale: 0.98f);
        }

        _lastSendStatusText = sendStatusText;
        SendButton.IsEnabled = canCompose && hasDraftContent && !hasPendingImport;

        var conversation = CurrentConversation();
        var hasConversation = conversation is not null;
        CopyCurrentConversationIdMenuItem.IsEnabled = hasConversation;
        ManageCurrentGroupMenuItem.Visibility = conversation?.Chat.Scene == ChatScene.Group
            ? Visibility.Visible
            : Visibility.Collapsed;
        ManageCurrentGroupMenuItem.IsEnabled = _environment.CurrentPersonaBelongsToSelectedGroup;
        ClearHistoryMenuItem.IsEnabled = hasConversation && _environment.Messages.Count > 0;
        RemoveCurrentConversationMenuItem.IsEnabled = CanRemoveConversation(conversation);
        RemoveCurrentConversationMenuItem.Text = conversation?.Chat.Scene == ChatScene.Group ? "Disband group" : "Remove friendship";
    }

    private void UpdateEnvironmentText()
    {
        EditProfileButton.Visibility = _environment.Capabilities.ProfileEditing ? Visibility.Visible : Visibility.Collapsed;
        ManageCustomFacesButton.Visibility = _environment.Capabilities.CustomFaces ? Visibility.Visible : Visibility.Collapsed;
        ManageCustomFacesButton.IsEnabled = _environment.BotPersona is not null;
        AccountCredentialsButton.Visibility = _environment.Capabilities.AccountCredentials ? Visibility.Visible : Visibility.Collapsed;
        AccountCredentialsButton.IsEnabled = !_environment.IsShowcaseMode && _environment.BotPersona is not null;
        BotPresenceText.Text = _environment.BotPresenceDescription;
        ToggleBotPresenceButton.Content = _environment.IsBotOnline ? "Simulate offline" : "Restore online";
        ToggleBotPresenceButton.IsEnabled = !_environment.IsShowcaseMode && _environment.BotPersona is not null;
        UpdateMaintenanceControls();
        UpdateConnectionPresentation();
        UpdateProfilePresentation();
        UpdatePageHeader();
        UpdateSendingIdentityText();
    }

    private void UpdateSendingIdentityText()
    {
        SendingAsText.Text = IsAnonymousDraftActive()
            ? "Sending anonymously"
            : _environment.CurrentPersona is { } persona
            ? $"Sending as {persona.DisplayName} · {persona.Id}" + (_environment.IsBotOnline ? string.Empty : " · bot offline")
            : "Choose a sending identity";
    }

    private void UpdateConnectionPresentation()
    {
        if (_closed)
        {
            return;
        }

        var status = _environment.ConnectionStatus;
        var isSuccess = status.StartsWith("Ready", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Connected", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Listening", StringComparison.OrdinalIgnoreCase);
        var isCaution = status.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase);
        var isFailed = status.StartsWith("Failed", StringComparison.OrdinalIgnoreCase);
        var operationInProgress = Volatile.Read(ref _connectionInProgress) != 0;
        var showProgress = operationInProgress || isCaution;
        var protocol = _environment.Preferences.Protocol switch
        {
            ProtocolKind.OneBotV12 => "OneBot V12",
            ProtocolKind.Milky => "Milky",
            _ => "OneBot V11",
        };
        var transport = _environment.Preferences.Protocol == ProtocolKind.Milky
            ? "HTTP + WebSocket"
            : _environment.Preferences.Transport == TransportMode.OneBotHttpServer
                ? "HTTP server"
            : _environment.Preferences.Transport == TransportMode.WebSocketClient
                ? "WebSocket client"
                : "WebSocket server";
        var action = isFailed ? "Retry" : _environment.ConnectionButtonText;

        ConnectionProtocolText.Text = protocol;
        ConnectionStatusLabel.Text = isFailed ? "Connection failed" : status;
        ToolTipService.SetToolTip(ConnectionDetailsPanel, $"{protocol} · {transport}\n{status}");
        ToolTipService.SetToolTip(ConnectButton, $"{action} {protocol}\n{status} (Ctrl+Shift+K)");
        AutomationProperties.SetName(ConnectButton, $"{action} {protocol}: {status}");
        ConnectButton.IsEnabled = !operationInProgress;
        ConnectionActionIcon.Glyph = isFailed ? "\uE72C" : "\uE7E8";
        ConnectionActionIcon.Visibility = showProgress ? Visibility.Collapsed : Visibility.Visible;
        ConnectionProgressRing.IsActive = showProgress;
        ConnectionProgressRing.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        ConnectionStateIndicator.Visibility = showProgress ? Visibility.Collapsed : Visibility.Visible;
        ConnectionStatusDot.Visibility = !isSuccess && !isCaution && !isFailed
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConnectionSuccessDot.Visibility = isSuccess ? Visibility.Visible : Visibility.Collapsed;
        ConnectionCautionDot.Visibility = isCaution ? Visibility.Visible : Visibility.Collapsed;
        ConnectionFailedDot.Visibility = isFailed ? Visibility.Visible : Visibility.Collapsed;
        var motionState = isSuccess ? "success" : isCaution ? "caution" : isFailed ? "failed" : "neutral";
        if (_loaded && !string.Equals(motionState, _lastConnectionMotionState, StringComparison.Ordinal))
        {
            var activeDot = isSuccess
                ? ConnectionSuccessDot
                : isCaution ? ConnectionCautionDot : isFailed ? ConnectionFailedDot : ConnectionStatusDot;
            FluentMotion.Pulse(activeDot, 1.55f);
        }

        _lastConnectionMotionState = motionState;
        UpdateShowcaseControls();
    }

    private static void UpdateMessageActions()
    {
    }

    private void RefreshAccountPage()
    {
        UpdateProfilePresentation();
        AccountSendingIdentityBox.SelectedItem = _environment.Personas.FirstOrDefault(item => item.Id == _environment.CurrentPersona?.Id);
        AccountBotBox.SelectedItem = _environment.Personas.FirstOrDefault(item => item.Id == _environment.BotPersona?.Id);
        AccountBotBox.IsEnabled = !_environment.IsShowcaseMode;
    }

    private void UpdateProfilePresentation()
    {
        var current = _environment.CurrentPersona;
        ProfileNameText.Text = current?.DisplayName ?? "No persona";
        ProfileIdText.Text = current?.Id ?? "Create or select a persona";
        AccountNameText.Text = ProfileNameText.Text;
        AccountIdText.Text = ProfileIdText.Text;
        ProfilePicture.DisplayName = AccountPicture.DisplayName = ProfileNameText.Text;
        SetAvatar(ProfilePicture, current?.Avatar);
        SetAvatar(AccountPicture, current?.Avatar);
        var profileDescription = current is null
            ? "Create or select a current user"
            : $"Current user: {current.DisplayName} · {current.Id}";
        ToolTipService.SetToolTip(ProfileButton, $"{profileDescription}\nAccount and bot configuration");
        AutomationProperties.SetName(ProfileButton, $"{profileDescription}. Open account and bot configuration");
    }

    private static void SetAvatar(PersonPicture picture, string? avatar)
    {
        if (!Uri.TryCreate(avatar, UriKind.Absolute, out var uri))
        {
            picture.ProfilePicture = null;
            return;
        }

        // Status and layout updates should not decode the same avatar again.
        if (picture.ProfilePicture is BitmapImage image && image.UriSource == uri)
        {
            return;
        }

        try { picture.ProfilePicture = new BitmapImage(uri); }
        catch { picture.ProfilePicture = null; }
    }

    private bool IsAnonymousDraftActive() => _anonymous
        && _environment.SelectedChat is { } chat && _environment.Capabilities.SupportsAnonymous(chat.Scene)
        && _activeDraftKey is not null && string.Equals(_activeDraftKey, GetCurrentDraftKey(), StringComparison.Ordinal);

    private void SaveActiveDraft()
    {
        if (_activeDraftKey is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(ComposerTextBox.Text) && _attachments.Count == 0 && _mentions.Count == 0 && _reply is null && _richContent.Count == 0 && !_anonymous)
        {
            _drafts.Remove(_activeDraftKey);
            return;
        }

        _drafts[_activeDraftKey] = new ComposerDraft(
            ComposerTextBox.Text,
            _mentions.ToArray(),
            _attachments.ToArray(),
            _reply,
            _richContent.ToArray(),
            _anonymous);
    }

    private void RestoreDraftForCurrentContext()
    {
        _activeDraftKey = GetCurrentDraftKey();
        _reply = null;
        _anonymous = false;
        _richContent.Clear();
        ComposerTextBox.Text = string.Empty;
        _mentions.Clear();
        _attachments.Clear();
        if (_activeDraftKey is null || !_drafts.TryGetValue(_activeDraftKey, out var draft))
        {
            return;
        }

        _anonymous = draft.Anonymous && _environment.SelectedChat is { } chat
            && _environment.Capabilities.SupportsAnonymous(chat.Scene);
        _reply = draft.Reply;
        foreach (var segment in draft.RichContent ?? []) _richContent.Add(segment);
        ComposerTextBox.Text = draft.Text;
        foreach (var mention in draft.Mentions)
        {
            _mentions.Add(mention);
        }

        foreach (var attachment in draft.Attachments)
        {
            _attachments.Add(attachment);
        }
    }

    private string? GetCurrentDraftKey()
    {
        var chat = _environment.SelectedChat;
        var sender = _environment.CurrentPersona;
        var bot = _environment.BotPersona;
        return chat is null || sender is null || bot is null
            ? null
            : $"{chat.Id}|{bot.Id.Length}:{bot.Id}|{sender.Id.Length}:{sender.Id}|{_environment.Preferences.Protocol}";
    }

    private void EnsureDraftContextIsCurrent(string draftKey, string chatId)
    {
        if (!string.Equals(_activeDraftKey, draftKey, StringComparison.Ordinal)
            || !string.Equals(GetCurrentDraftKey(), draftKey, StringComparison.Ordinal)
            || _environment.SelectedChat?.Id != chatId)
        {
            throw new InvalidOperationException("The active conversation or sending identity changed. Review the draft and try again.");
        }
    }

    private bool CanRemoveConversation(ConversationItem? item) => item?.Chat.Scene switch
    {
        ChatScene.Friend => true,
        ChatScene.Group => _environment.SelectedChat?.Id == item.Chat.Id
            && _currentUserOwnsSelectedGroup,
        _ => false,
    };

    private void QueueNavigationFocus(
        Control control,
        string pageTag,
        FocusState focusState = FocusState.Programmatic)
    {
        _pendingNavigationFocus = control;
        _pendingNavigationFocusPageTag = pageTag;
        _pendingNavigationFocusState = focusState;
        if (!_pageTransitionRunning
            && string.Equals(_navigation.CurrentPage, pageTag, StringComparison.Ordinal)
            && PageFor(pageTag).Visibility == Visibility.Visible)
        {
            ApplyQueuedNavigationFocus(pageTag);
        }
    }

    private void ApplyQueuedNavigationFocus(string pageTag)
    {
        if (_pendingNavigationFocus is not { } control
            || !string.Equals(_pendingNavigationFocusPageTag, pageTag, StringComparison.Ordinal))
        {
            return;
        }

        var focusState = _pendingNavigationFocusState;
        var navigationRevision = _navigation.Revision;
        ClearQueuedNavigationFocus();
        RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed && _navigation.Revision == navigationRevision
                && !_navigation.HasPendingPage
                && string.Equals(_navigation.CurrentPage, pageTag, StringComparison.Ordinal))
            {
                control.Focus(focusState);
            }
        });
    }

    private void ClearQueuedNavigationFocus()
    {
        _pendingNavigationFocus = null;
        _pendingNavigationFocusPageTag = null;
        _pendingNavigationFocusState = FocusState.Programmatic;
    }

    private static CancellationToken RestartMotion(ref CancellationTokenSource? source)
    {
        CancelMotion(ref source);
        source = new CancellationTokenSource();
        return source.Token;
    }

    private static void CancelMotion(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }

    private async Task RefreshSelectedGroupAuthorityAsync()
    {
        var chat = _environment.SelectedChat;
        var persona = _environment.CurrentPersona;
        var isOwner = false;
        if (chat?.Scene == ChatScene.Group && persona is not null)
        {
            try
            {
                var membership = await _environment.Store.GetMemberAsync(chat.PeerId, persona.Id);
                isOwner = membership?.Role == GroupRole.Owner;
            }
            catch (Exception exception)
            {
                _environment.ReportError("Group permissions could not be loaded", exception);
            }
        }

        if (_environment.SelectedChat?.Id == chat?.Id
            && _environment.CurrentPersona?.Id == persona?.Id)
        {
            _currentUserOwnsSelectedGroup = isOwner;
            UpdateChatState();
        }
    }

    private bool TryEnterDialog() => Interlocked.CompareExchange(ref _dialogInProgress, 1, 0) == 0;

    private void ExitDialog() => Volatile.Write(ref _dialogInProgress, 0);

    private void ApplyTheme() => RootGrid.RequestedTheme = WindowChrome.ToElementTheme(_environment.Preferences.Theme);

    private ContentDialog CreateDialog(string title, string primaryButtonText, object content) => new()
    {
        XamlRoot = RootGrid.XamlRoot,
        Title = title,
        Content = content,
        PrimaryButtonText = primaryButtonText,
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary,
    };

    private void InstallKeyboardAccelerators()
    {
        AddAccelerator(VirtualKey.N, VirtualKeyModifiers.Control, (_, _) => NewPersona_Click(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (_, _) => NewGroup_Click(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.K, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (_, _) => ConnectButton_Click(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.I, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, (_, _) => SetProtocolPaneVisible(!_protocolPaneVisible));
        AddAccelerator(VirtualKey.L, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, (_, _) => OpenLogs_Click(this, new RoutedEventArgs()));
        AddAccelerator((VirtualKey)192, VirtualKeyModifiers.Control, (_, _) => SetConsoleVisible(!_consoleVisible));
        AddAccelerator(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, (_, _) => SetProtocolPaneVisible(!_protocolPaneVisible));
        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, async (_, _) => await PickAttachmentsSafelyAsync());
        AddAccelerator((VirtualKey)188, VirtualKeyModifiers.Control, (_, _) => OpenSettings_Click(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.K, VirtualKeyModifiers.Control, (_, _) =>
        {
            QueueNavigationFocus(ConversationSearchBox, "people", FocusState.Keyboard);
            AppNavigationView.SelectedItem = PeopleNavigationItem;
        });
    }

    private void AddAccelerator(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += handler;
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private async Task PickAttachmentsSafelyAsync()
    {
        try
        {
            await PickAttachmentsAsync();
        }
        catch (Exception exception)
        {
            _environment.ReportError("File picker failed", exception);
            ShowError(exception);
        }
    }

    private void CopyText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception exception)
        {
            _environment.ReportError("Clipboard operation failed", exception);
            ShowError(exception);
        }
    }

    private void ShowError(Exception exception) => ShowErrorMessage(exception.Message);

    private void ShowErrorMessage(string message)
    {
        ErrorInfoBar.Severity = InfoBarSeverity.Error;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }

    private sealed record ComposerDraft(
        string Text,
        IReadOnlyList<MentionDraft> Mentions,
        IReadOnlyList<AttachmentDraft> Attachments,
        ReplyDraft? Reply = null,
        IReadOnlyList<MessageSegment>? RichContent = null,
        bool Anonymous = false);

    private sealed record ReplyDraft(string MessageId, string SenderId, string Sender, string Preview);
    private sealed record MessageRenderKey(string Content, bool Recalled,
        HorizontalAlignment Alignment, ProtocolKind Protocol, string Quotes);
    private sealed record MessageRow(ListViewItem Container, MessageRenderKey Key, StackPanel Reactions, TextBlock Meta);

    private sealed record MentionChoice(string? UserId, string Label)
    {
        public override string ToString() => Label;
    }
}
