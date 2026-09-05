using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Dispatching;
using Windows.Security.Credentials;
using Windows.Storage;

namespace Asuka.App;

public sealed class AppEnvironment : BindableBase, IAsyncDisposable
{
    private const string CredentialResource = "Asuka.ProtocolAccessToken";
    private const string CredentialUser = "default";
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly CancellationTokenSource _refreshCancellation = new();
    private readonly object _refreshTasksGate = new();
    private readonly HashSet<Task> _outstandingRefreshes = [];
    private readonly AppStoragePaths _storagePaths;
    private readonly string _preferencesPath;
    private ProtocolSession? _session;
    private AppPreferences _preferences = new();
    private User? _currentPersona;
    private User? _botPersona;
    private Chat? _selectedChat;
    private bool _currentPersonaBelongsToSelectedGroup;
    private string _selectedChatTitle = "Choose a conversation";
    private string _selectedChatSubtitle = "Create a persona, group, or friendship to get started.";
    private string _connectionStatus = "Offline";
    private string _roundTripText = "RTT unavailable";
    private bool _initialized;
    private bool _disposed;

    public AppEnvironment(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _storagePaths = AppStoragePaths.Create();
        _preferencesPath = _storagePaths.SettingsPath;
        Store = new AsukaStore(_storagePaths.DatabasePath);
        Assets = new AssetStore(_storagePaths.AssetsDirectory);
        Platform = new PlatformService(Store, Assets);
        Media = new MediaService(Store, Assets);
        Store.Changed += OnStoreChanged;
    }

    public AsukaStore Store { get; }
    public AssetStore Assets { get; }
    public PlatformService Platform { get; }
    public MediaService Media { get; }
    public ObservableCollection<PersonaItem> Personas { get; } = [];
    public ObservableCollection<Group> Groups { get; } = [];
    public ObservableCollection<ConversationItem> Conversations { get; } = [];
    public ObservableCollection<PendingRequestItem> PendingRequests { get; } = [];
    public ObservableCollection<MessageItem> Messages { get; } = [];
    public ObservableCollection<LogEntryItem> Logs { get; } = [];
    public ObservableCollection<LogEntryItem> RawTraffic { get; } = [];

    public event EventHandler? PreferencesChanged;
    public event EventHandler? SelectedChatChanged;

    public AppPreferences Preferences
    {
        get => _preferences;
        private set => SetProperty(ref _preferences, value);
    }

    public User? CurrentPersona
    {
        get => _currentPersona;
        private set
        {
            if (SetProperty(ref _currentPersona, value))
            {
                CurrentPersonaBelongsToSelectedGroup = false;
                RaisePropertyChanged(nameof(CurrentPersonaName));
                RaisePropertyChanged(nameof(IdentityStatus));
                RaisePropertyChanged(nameof(CanCompose));
            }
        }
    }

    public string CurrentPersonaName => CurrentPersona?.DisplayName ?? "No persona";

    public User? BotPersona
    {
        get => _botPersona;
        private set
        {
            if (SetProperty(ref _botPersona, value))
            {
                CurrentPersonaBelongsToSelectedGroup = false;
                RaisePropertyChanged(nameof(BotPersonaName));
                RaisePropertyChanged(nameof(IdentityStatus));
                RaisePropertyChanged(nameof(CanCompose));
            }
        }
    }

    public string BotPersonaName => BotPersona?.DisplayName ?? "No bot account";

    public string IdentityStatus => CurrentPersona is null || BotPersona is null
        ? "Identity not configured"
        : CurrentPersona.Id == BotPersona.Id
            ? CurrentPersona.DisplayName
            : $"{CurrentPersona.DisplayName} → {BotPersona.DisplayName}";

    public Chat? SelectedChat
    {
        get => _selectedChat;
        private set
        {
            if (SetProperty(ref _selectedChat, value))
            {
                CurrentPersonaBelongsToSelectedGroup = false;
                RaisePropertyChanged(nameof(CanCompose));
            }
        }
    }

    public string SelectedChatTitle
    {
        get => _selectedChatTitle;
        private set => SetProperty(ref _selectedChatTitle, value);
    }

    public string SelectedChatSubtitle
    {
        get => _selectedChatSubtitle;
        private set => SetProperty(ref _selectedChatSubtitle, value);
    }

    public bool CurrentPersonaBelongsToSelectedGroup
    {
        get => _currentPersonaBelongsToSelectedGroup;
        private set
        {
            if (SetProperty(ref _currentPersonaBelongsToSelectedGroup, value))
            {
                RaisePropertyChanged(nameof(CanCompose));
            }
        }
    }

    public bool CanCompose => IsChatCompatible(SelectedChat, CurrentPersona, BotPersona)
        && (SelectedChat?.Scene != ChatScene.Group || CurrentPersonaBelongsToSelectedGroup)
        && _session?.State.Kind is SessionStateKind.Ready or SessionStateKind.Connected;

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            if (SetProperty(ref _connectionStatus, value))
            {
                RaisePropertyChanged(nameof(ConnectionButtonText));
            }
        }
    }

    public string ConnectionButtonText => _session?.State.IsActive == true ? "Disconnect" : "Connect";

    public string RoundTripText
    {
        get => _roundTripText;
        private set => SetProperty(ref _roundTripText, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Preferences = await LoadPreferencesAsync(cancellationToken);
            foreach (var migrationMessage in _storagePaths.MigrationMessages)
            {
                AddLog("Storage", "Legacy migration", migrationMessage);
            }
            var users = await Store.GetAllUsersAsync(cancellationToken);
            var legacyId = Preferences.PersonaId;
            User? seededActivePersona = null;
            User? seededBotPersona = null;
            if (users.Count == 0)
            {
                seededActivePersona = new User(
                    "You",
                    nickname: "You",
                    sign: "Simulated active Windows persona");
                seededBotPersona = new User(
                    "Asuka Bot",
                    nickname: "Asuka Bot",
                    sign: "Windows native protocol account");
                await Platform.SaveUserAsync(seededActivePersona, cancellationToken);
                await Platform.SaveUserAsync(seededBotPersona, cancellationToken);
                await Platform.AddFriendshipAsync(
                    seededActivePersona.Id,
                    seededBotPersona.Id,
                    "Asuka Bot",
                    cancellationToken);
                users = [seededActivePersona, seededBotPersona];
            }
            else if (users.Count == 1)
            {
                var existingPersona = users[0];
                var selectedActivePersona = users.FirstOrDefault(user =>
                    user.Id == (Preferences.ActiveUserId ?? legacyId));
                var selectedBotPersona = users.FirstOrDefault(user =>
                    user.Id == (Preferences.BotUserId ?? legacyId));
                if (selectedActivePersona is null
                    || selectedBotPersona is null
                    || selectedActivePersona.Id == selectedBotPersona.Id)
                {
                    var isLegacyBot = string.Equals(
                        existingPersona.Name,
                        "Asuka Bot",
                        StringComparison.OrdinalIgnoreCase);
                    seededActivePersona = isLegacyBot
                        ? new User(
                            "You",
                            nickname: "You",
                            sign: "Simulated active Windows persona")
                        : existingPersona;
                    seededBotPersona = isLegacyBot
                        ? existingPersona
                        : new User(
                            "Asuka Bot",
                            nickname: "Asuka Bot",
                            sign: "Windows native protocol account");

                    if (seededActivePersona.Id != existingPersona.Id)
                    {
                        await Platform.SaveUserAsync(seededActivePersona, cancellationToken);
                    }

                    if (seededBotPersona.Id != existingPersona.Id)
                    {
                        await Platform.SaveUserAsync(seededBotPersona, cancellationToken);
                    }

                    await Platform.AddFriendshipAsync(
                        seededActivePersona.Id,
                        seededBotPersona.Id,
                        "Asuka Bot",
                        cancellationToken);
                    users = [seededActivePersona, seededBotPersona];
                }
            }

            var selectedPersonas = seededActivePersona is not null && seededBotPersona is not null
                ? new PersonaPair(seededActivePersona, seededBotPersona)
                : PersonaSelection.Resolve(
                    users,
                    Preferences.ActiveUserId,
                    Preferences.BotUserId,
                    legacyId);
            CurrentPersona = selectedPersonas.Active;
            BotPersona = selectedPersonas.Bot;
            Preferences = Preferences with
            {
                ActiveUserId = CurrentPersona.Id,
                BotUserId = BotPersona.Id,
                PersonaId = null,
            };
            Platform.SetRegisteredBot(BotPersona.Id);
            Media.SetEndpoint(Preferences.Host, Preferences.Port, Preferences.AdvertisedHost);
            await RefreshAllAsync(cancellationToken);
            await SavePreferencesAsync(Preferences, cancellationToken);
            AddLog(
                "App",
                "Ready",
                $"Sending as {CurrentPersona.DisplayName}; protocol bot is {BotPersona.DisplayName} ({BotPersona.Id}).");
            _initialized = true;
        }
        catch (Exception exception)
        {
            ReportError("Initialization failed", exception);
            throw;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task ToggleConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_session?.State.IsActive == true)
        {
            await DisconnectAsync(cancellationToken);
        }
        else
        {
            await ConnectAsync(cancellationToken);
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (BotPersona is null)
        {
            throw new InvalidOperationException("Create or select a bot account before connecting.");
        }

        await DisconnectAsync(cancellationToken);
        var settings = Preferences.ToConnectionSettings();
        Media.SetEndpoint(settings.Host, settings.Port, settings.AdvertisedHost);
        IProtocolImplementation implementation = Preferences.Protocol switch
        {
            ProtocolKind.OneBotV11 => new OneBotProtocol(OneBotVersion.V11, BotPersona.Id, Platform, Media),
            ProtocolKind.OneBotV12 => new OneBotProtocol(OneBotVersion.V12, BotPersona.Id, Platform, Media),
            ProtocolKind.Milky => new MilkyProtocol(BotPersona.Id, Platform, Media),
            _ => throw new InvalidOperationException("Unsupported protocol selection."),
        };

        var session = new ProtocolSession(implementation, Platform, settings, Assets);
        session.StateChanged += OnSessionStateChanged;
        session.RoundTripTimeChanged += OnRoundTripTimeChanged;
        session.TrafficObserved += OnTrafficObserved;
        session.OutboundDeliveryFailed += OnOutboundDeliveryFailed;
        _session = session;
        RaisePropertyChanged(nameof(ConnectionButtonText));
        try
        {
            await session.StartAsync(cancellationToken);
            AddLog("Connection", "Started", $"{implementation.DisplayName} using {settings.Transport}.");
        }
        catch (Exception exception)
        {
            DetachSession(session);
            _session = null;
            await session.DisposeAsync();
            await RunOnUiAsync(() =>
            {
                ConnectionStatus = $"Failed: {exception.Message}";
                RaisePropertyChanged(nameof(ConnectionButtonText));
                RaisePropertyChanged(nameof(CanCompose));
            });
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        _session = null;
        DetachSession(session);
        try
        {
            await session.StopAsync(cancellationToken);
            AddLog("Connection", "Stopped", "Protocol transport stopped.");
        }
        finally
        {
            await session.DisposeAsync();
            await RunOnUiAsync(() =>
            {
                ConnectionStatus = "Offline";
                RoundTripText = "RTT unavailable";
                RaisePropertyChanged(nameof(ConnectionButtonText));
                RaisePropertyChanged(nameof(CanCompose));
            });
        }
    }

    public async Task ApplyPreferencesAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var requiresServerToken = preferences.Protocol == ProtocolKind.Milky
            || preferences.Transport == TransportMode.WebSocketServer;
        if (requiresServerToken && string.IsNullOrWhiteSpace(preferences.AccessToken))
        {
            preferences = preferences with { AccessToken = CreateAccessToken() };
        }

        var settings = preferences.ToConnectionSettings();
        settings.Validate();
        var wasActive = _session?.State.IsActive == true;
        await DisconnectAsync(cancellationToken);

        var users = await Store.GetAllUsersAsync(cancellationToken);
        var selectedPersonas = PersonaSelection.Resolve(
            users,
            preferences.ActiveUserId,
            preferences.BotUserId,
            preferences.PersonaId);
        var activePersona = selectedPersonas.Active;
        var botPersona = selectedPersonas.Bot;
        Preferences = preferences with
        {
            ActiveUserId = activePersona.Id,
            BotUserId = botPersona.Id,
            PersonaId = null,
            Transport = preferences.Protocol == ProtocolKind.Milky
                ? TransportMode.MilkyService
                : preferences.Transport,
        };
        CurrentPersona = activePersona;
        BotPersona = botPersona;
        Platform.SetRegisteredBot(botPersona.Id);
        Media.SetEndpoint(Preferences.Host, Preferences.Port, Preferences.AdvertisedHost);
        SelectedChat = null;
        SelectedChatTitle = "Choose a conversation";
        SelectedChatSubtitle = $"Sending as {activePersona.DisplayName}; bot account {botPersona.DisplayName}.";
        await SavePreferencesAsync(Preferences, cancellationToken);
        await RefreshAllAsync(cancellationToken);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
        SelectedChatChanged?.Invoke(this, EventArgs.Empty);
        AddLog(
            "Settings",
            "Saved",
            $"Active identity: {activePersona.Id}; bot account: {botPersona.Id}; protocol: {Preferences.Protocol}.");

        if (wasActive)
        {
            await ConnectAsync(cancellationToken);
        }
    }

    public async Task<User> CreatePersonaAsync(
        string name,
        string nickname,
        string sign,
        bool makeCurrent,
        string? avatar = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = name.Trim();
        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("Name cannot be empty.", nameof(name));
        }

        var user = new User(trimmedName, nickname: nickname.Trim(), sign: sign.Trim(), avatar: string.IsNullOrWhiteSpace(avatar) ? null : avatar.Trim());
        await Platform.SaveUserAsync(user, cancellationToken);
        if (makeCurrent)
        {
            await ApplyPreferencesAsync(Preferences with { ActiveUserId = user.Id }, cancellationToken);
        }

        return user;
    }

    public async Task<Group> CreateGroupAsync(
        string name,
        string intro,
        int maximumMembers,
        CancellationToken cancellationToken = default)
    {
        if (CurrentPersona is null || BotPersona is null)
        {
            throw new InvalidOperationException("Select a sending identity and bot account before creating a group.");
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("Group name cannot be empty.", nameof(name));
        }

        var group = new Group(trimmedName, intro: intro.Trim(), maxMemberCount: Math.Clamp(maximumMembers, 2, 10_000));
        await Platform.CreateGroupAsync(group, CurrentPersona.Id, cancellationToken);
        if (BotPersona.Id != CurrentPersona.Id)
        {
            await Platform.AddMemberAsync(
                group.Id,
                BotPersona.Id,
                CurrentPersona.Id,
                GroupMemberChangeReason.Administrative,
                cancellationToken);
        }

        await SelectChatAsync(new Chat(ChatScene.Group, group.Id, BotPersona.Id), group.Name, cancellationToken);
        return group;
    }

    public async Task AddFriendAsync(User friend, string remark, CancellationToken cancellationToken = default)
    {
        if (BotPersona is null)
        {
            throw new InvalidOperationException("Select a bot account before adding a friend.");
        }

        if (friend.Id == BotPersona.Id)
        {
            throw new InvalidOperationException("A persona cannot add itself as a friend.");
        }

        await Platform.AddFriendshipAsync(BotPersona.Id, friend.Id, remark.Trim(), cancellationToken);
        await SelectChatAsync(new Chat(ChatScene.Friend, friend.Id, BotPersona.Id), friend.DisplayName, cancellationToken);
    }

    public Task ResolveRequestAsync(
        PendingRequest request,
        bool approve,
        string reason = "",
        CancellationToken cancellationToken = default) =>
        Platform.ResolveRequestAsync(request.Flag, approve, reason, cancellationToken: cancellationToken);

    public Task SelectConversationAsync(ConversationItem? conversation, CancellationToken cancellationToken = default) =>
        conversation is null
            ? ClearSelectedChatAsync()
            : SelectChatAsync(conversation.Chat, conversation.Title, cancellationToken);

    public async Task SelectChatAsync(
        Chat chat,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var currentPersona = CurrentPersona;
        var botPersona = BotPersona;
        string? counterpartId = null;
        if (chat.Scene.IsPrivate())
        {
            if (currentPersona is null || botPersona is null)
            {
                await ClearSelectedChatAsync();
                throw new InvalidOperationException("Select a sending identity and bot account before opening a private conversation.");
            }

            try
            {
                counterpartId = ValidatePrivateChat(chat, currentPersona, botPersona);
            }
            catch
            {
                await ClearSelectedChatAsync();
                throw;
            }
        }

        SelectedChat = chat;
        SelectedChatTitle = chat.Scene.IsPrivate()
            ? await ResolveChatTitleAsync(chat, counterpartId, cancellationToken)
            : title ?? await ResolveChatTitleAsync(chat, null, cancellationToken);
        SelectedChatSubtitle = chat.Scene switch
        {
            ChatScene.Group => $"Group · {chat.PeerId}",
            ChatScene.Friend => $"Direct message · {counterpartId}",
            _ => $"Temporary chat · {counterpartId}",
        };
        await RefreshSelectedGroupMembershipAsync(cancellationToken);
        await RefreshMessagesAsync(cancellationToken);
        SelectedChatChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<Message> SendAsync(
        string text,
        IEnumerable<AttachmentDraft> attachments,
        CancellationToken cancellationToken = default)
    {
        var segments = new List<MessageSegment>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            segments.Add(MessageSegment.FromText(text.Trim()));
        }

        segments.AddRange(attachments.Select(attachment => attachment.ToSegment()));
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("Enter a message or attach a file first.");
        }

        return await SendSegmentsAsync(segments, cancellationToken);
    }

    public async Task<Message> SendSegmentsAsync(
        IEnumerable<MessageSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var content = segments.ToArray();
        if (content.Length == 0)
        {
            throw new InvalidOperationException("Enter a message or attach a file first.");
        }

        var persona = CurrentPersona ?? throw new InvalidOperationException("No active persona.");
        var bot = BotPersona ?? throw new InvalidOperationException("No bot account.");
        var chat = SelectedChat ?? throw new InvalidOperationException("No conversation selected.");
        _ = ValidatePrivateChat(chat, persona, bot);
        var session = _session;
        if (session?.State.Kind is not (SessionStateKind.Ready or SessionStateKind.Connected))
        {
            throw new InvalidOperationException("Connect a ready protocol session before sending.");
        }

        return await Platform.SendMessageAsync(
            chat.Scene,
            chat.PeerId,
            persona.Id,
            bot.Id,
            content,
            cancellationToken);
    }

    public async Task<AttachmentDraft> CreateAttachmentAsync(
        StorageFile file,
        CancellationToken cancellationToken = default)
    {
        await using var source = await file.OpenStreamForReadAsync();
        var asset = await Assets.StoreAsync(source, file.Name, file.ContentType, cancellationToken);
        await Platform.SaveAssetAsync(asset, cancellationToken);
        AddLog("Attachment", "Prepared", $"{file.Name} ({asset.ByteCount} bytes, {asset.Id}).");
        return new AttachmentDraft(file, asset);
    }

    public async Task RecallMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var persona = CurrentPersona ?? throw new InvalidOperationException("No active persona.");
        await Platform.RecallMessageAsync(messageId, persona.Id, cancellationToken);
    }

    public async Task ClearSelectedConversationHistoryAsync(CancellationToken cancellationToken = default)
    {
        var chat = SelectedChat ?? throw new InvalidOperationException("No conversation selected.");
        await Platform.ClearMessageHistoryAsync(chat, cancellationToken);
        if (SelectedChat?.Id == chat.Id)
        {
            await RefreshSelectedGroupMembershipAsync(cancellationToken);
            await RefreshMessagesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<MemberRosterItem>> GetRosterAsync(
        string groupId,
        CancellationToken cancellationToken = default) =>
        await Store.GetMemberRosterAsync(groupId, cancellationToken);

    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var users = await Store.GetAllUsersAsync(cancellationToken);
            var groups = await Store.GetAllGroupsAsync(cancellationToken);
            var conversations = BotPersona is null
                ? []
                : await Store.GetConversationSummariesAsync(BotPersona.Id, cancellationToken);
            var friends = BotPersona is null
                ? []
                : await Store.GetFriendsAsync(BotPersona.Id, cancellationToken);
            var joinedGroups = BotPersona is null
                ? []
                : await Store.GetGroupsContainingAsync(BotPersona.Id, cancellationToken);
            var pendingRequests = BotPersona is null
                ? []
                : await Store.GetPendingRequestsAsync(BotPersona.Id, cancellationToken: cancellationToken);
            var userMap = users.ToDictionary(user => user.Id, StringComparer.Ordinal);
            var groupMap = groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
            var currentPersona = CurrentPersona;
            var botPersona = BotPersona;
            var chatItems = conversations
                .Where(summary => !summary.Chat.Scene.IsPrivate()
                    || IsChatCompatible(summary.Chat, currentPersona, botPersona))
                .Select(summary =>
                {
                    var title = summary.Chat.Scene.IsPrivate()
                        ? ResolvePrivateChatTitle(summary.Chat, currentPersona, userMap)
                        : summary.Title;
                    return new ConversationItem(
                        summary.Chat,
                        title,
                        summary.LastMessage.IsRecalled
                            ? "[Recalled]"
                            : string.IsNullOrWhiteSpace(summary.LastMessage.Content.TextPreview())
                                ? "No text content"
                                : summary.LastMessage.Content.TextPreview(),
                        summary.LastMessage.Time,
                        summary.Chat.Scene.IsPrivate() && currentPersona is not null
                            ? summary.Chat.CounterpartId(currentPersona.Id)
                            : summary.Chat.PeerId);
                })
                .ToList();
            var knownChatIds = chatItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var knownPrivatePeers = chatItems
                .Where(item => item.Chat.Scene.IsPrivate())
                .Select(item => item.Chat.PeerId)
                .ToHashSet(StringComparer.Ordinal);
            if (botPersona is not null)
            {
                var joinedGroupIds = joinedGroups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
                chatItems.AddRange(groups
                    .Select(group => new ConversationItem(
                        new Chat(ChatScene.Group, group.Id, botPersona.Id),
                        group.Name,
                        joinedGroupIds.Contains(group.Id)
                            ? "Start a group conversation"
                            : "Not joined — view group details"))
                    .Where(item => knownChatIds.Add(item.Id)));

                var friendIds = friends.Select(friend => friend.User.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var user in users)
                {
                    if (!TryCreatePrivateChatCandidate(
                        user.Id,
                        currentPersona,
                        botPersona,
                        friendIds,
                        out var chat)
                        || chat is null
                        || knownChatIds.Contains(chat.Id)
                        || knownPrivatePeers.Contains(chat.PeerId))
                    {
                        continue;
                    }

                    knownChatIds.Add(chat.Id);
                    knownPrivatePeers.Add(chat.PeerId);
                    chatItems.Add(new ConversationItem(
                        chat,
                        ResolvePrivateChatTitle(chat, currentPersona, userMap),
                        chat.Scene == ChatScene.Friend
                            ? "Start a direct message"
                            : "Start a temporary conversation",
                        displayId: currentPersona is null ? chat.PeerId : chat.CounterpartId(currentPersona.Id)));
                }
            }

            await RunOnUiAsync(() =>
            {
                Replace(Personas, users.Select(user => new PersonaItem(user)));
                Replace(Groups, groups);
                Replace(Conversations, chatItems);
                Replace(PendingRequests, pendingRequests.Select(request => new PendingRequestItem(
                    request,
                    userMap.GetValueOrDefault(request.RequesterId),
                    request.GroupId is null ? null : groupMap.GetValueOrDefault(request.GroupId))));
            });
            await RefreshMessagesAsync(cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void ClearLogs() => RunOnUi(() => Logs.Clear());

    public void ReportError(string summary, Exception exception) =>
        AddLog("Error", summary, exception.ToString());

    public void AddLog(string category, string summary, string detail) =>
        AddLog(new LogEntryItem(DateTimeOffset.UtcNow, category, summary, detail));

    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            AddLog("Lifecycle", "Stopping", "Closing protocol transports and application storage.");
            Task[] outstandingRefreshes;
            lock (_refreshTasksGate)
            {
                _disposed = true;
                Store.Changed -= OnStoreChanged;
                _refreshCancellation.Cancel();
                outstandingRefreshes = [.. _outstandingRefreshes];
            }

            await AwaitRefreshesAsync(outstandingRefreshes);
            await DisconnectAsync();
            await Platform.DisposeAsync();
            Assets.Dispose();
            await Store.DisposeAsync();
            _initializeGate.Dispose();
            _refreshGate.Dispose();
            _refreshCancellation.Dispose();
            AddLog("Lifecycle", "Stopped", "Application resources have been released.");
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private async Task ClearSelectedChatAsync()
    {
        SelectedChat = null;
        SelectedChatTitle = "Choose a conversation";
        SelectedChatSubtitle = "Select a conversation from the sidebar.";
        await RunOnUiAsync(Messages.Clear);
        SelectedChatChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshMessagesAsync(CancellationToken cancellationToken = default)
    {
        var chat = SelectedChat;
        var persona = CurrentPersona;
        if (chat is null || persona is null)
        {
            await RunOnUiAsync(Messages.Clear);
            return;
        }

        var records = await Store.GetMessagesAsync(chat, 500, cancellationToken);
        var users = await Store.GetAllUsersAsync(cancellationToken);
        var userMap = users.ToDictionary(user => user.Id, StringComparer.Ordinal);
        var viewItems = records.Select(message => new MessageItem(
            message,
            userMap.GetValueOrDefault(message.SenderId),
            message.SenderId == persona.Id));
        await RunOnUiAsync(() => Replace(Messages, viewItems));
    }

    private async Task RefreshSelectedGroupMembershipAsync(CancellationToken cancellationToken)
    {
        var chat = SelectedChat;
        var persona = CurrentPersona;
        if (chat?.Scene != ChatScene.Group || persona is null)
        {
            await RunOnUiAsync(() => CurrentPersonaBelongsToSelectedGroup = false);
            return;
        }

        var belongsToGroup = await Store.GetMemberAsync(chat.PeerId, persona.Id, cancellationToken) is not null;
        await RunOnUiAsync(() =>
        {
            if (SelectedChat?.Id == chat.Id && CurrentPersona?.Id == persona.Id)
            {
                CurrentPersonaBelongsToSelectedGroup = belongsToGroup;
            }
        });
    }

    private async Task<string> ResolveChatTitleAsync(
        Chat chat,
        string? privateCounterpartId,
        CancellationToken cancellationToken)
    {
        if (chat.Scene == ChatScene.Group)
        {
            return (await Store.GetGroupAsync(chat.PeerId, cancellationToken))?.Name ?? chat.PeerId;
        }

        var counterpartId = privateCounterpartId
            ?? throw new InvalidOperationException("A private conversation must have a current participant.");
        return (await Store.GetUserAsync(counterpartId, cancellationToken))?.DisplayName ?? counterpartId;
    }

    private void OnStoreChanged(object? sender, StoreChangedEventArgs args)
    {
        if (_disposed || _refreshCancellation.IsCancellationRequested)
        {
            return;
        }

        TrackRefresh();
    }

    private async Task RefreshAfterStoreChangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAllAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!_disposed)
        {
            ReportError("Could not refresh the workspace", exception);
        }
    }

    private void TrackRefresh()
    {
        Task refresh;
        lock (_refreshTasksGate)
        {
            if (_disposed)
            {
                return;
            }

            refresh = RefreshAfterStoreChangeAsync(_refreshCancellation.Token);
            _outstandingRefreshes.Add(refresh);
        }

        _ = refresh.ContinueWith(
            completed =>
            {
                lock (_refreshTasksGate)
                {
                    _outstandingRefreshes.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task AwaitRefreshesAsync(IEnumerable<Task> refreshes)
    {
        try
        {
            await Task.WhenAll(refreshes);
        }
        catch
        {
            // Store-change refreshes contain their own reporting path; shutdown must not race disposal.
        }
    }

    private static string ValidatePrivateChat(Chat chat, User persona, User bot)
    {
        if (!chat.Scene.IsPrivate())
        {
            return chat.PeerId;
        }

        if (chat.SelfId != bot.Id)
        {
            throw new InvalidOperationException("This private conversation belongs to a different bot account.");
        }

        if (chat.SelfId == chat.PeerId)
        {
            throw new InvalidOperationException("A private conversation must have two distinct participants.");
        }

        return chat.CounterpartId(persona.Id)
            ?? throw new InvalidOperationException("The active persona is not a participant in this private conversation.");
    }

    private static bool TryCreatePrivateChatCandidate(
        string targetId,
        User? persona,
        User bot,
        HashSet<string> friendIds,
        out Chat? chat)
    {
        chat = null;
        if (persona is null || targetId == persona.Id)
        {
            return false;
        }

        string peerId;
        if (persona.Id == bot.Id)
        {
            peerId = targetId;
        }
        else if (targetId == bot.Id)
        {
            peerId = persona.Id;
        }
        else
        {
            return false;
        }

        chat = new Chat(
            friendIds.Contains(peerId) ? ChatScene.Friend : ChatScene.Temp,
            peerId,
            bot.Id);
        return true;
    }

    private static string ResolvePrivateChatTitle(
        Chat chat,
        User? persona,
        Dictionary<string, User> users)
    {
        var counterpartId = persona is null ? null : chat.CounterpartId(persona.Id);
        return counterpartId is not null && users.TryGetValue(counterpartId, out var counterpart)
            ? counterpart.DisplayName
            : counterpartId ?? chat.PeerId;
    }

    private static bool IsChatCompatible(Chat? chat, User? persona, User? bot)
    {
        if (chat is null || persona is null || bot is null)
        {
            return false;
        }

        return !chat.Scene.IsPrivate()
            || (chat.SelfId == bot.Id
                && chat.SelfId != chat.PeerId
                && (persona.Id == chat.SelfId || persona.Id == chat.PeerId));
    }

    private void OnSessionStateChanged(object? sender, SessionState state) => RunOnUi(() =>
    {
        ConnectionStatus = state.Kind switch
        {
            SessionStateKind.Listening => $"Listening on {state.Port}",
            SessionStateKind.Ready => $"Ready on {state.Port}",
            SessionStateKind.Connecting => "Connecting…",
            SessionStateKind.Connected => "Connected",
            SessionStateKind.Failed => $"Failed: {state.Error}",
            _ => "Offline",
        };
        AddLog("Connection", state.Kind.ToString(), ConnectionStatus);
        RaisePropertyChanged(nameof(ConnectionButtonText));
        RaisePropertyChanged(nameof(CanCompose));
    });

    private void OnRoundTripTimeChanged(object? sender, RoundTripTimeState state) => RunOnUi(() =>
    {
        RoundTripText = state.Kind switch
        {
            RoundTripTimeKind.Measured when state.Duration is { } duration => $"RTT {duration.TotalMilliseconds:0} ms",
            RoundTripTimeKind.Measuring => "Measuring RTT…",
            RoundTripTimeKind.TimedOut => "RTT timed out",
            RoundTripTimeKind.Unsupported => "RTT not applicable",
            _ => "RTT unavailable",
        };
    });

    private void OnTrafficObserved(object? sender, TrafficEntry entry)
    {
        var detail = entry.Payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var protocol = Preferences.Protocol switch
        {
            ProtocolKind.OneBotV12 => "OneBot V12",
            ProtocolKind.Milky => "Milky",
            _ => "OneBot V11",
        };
        AddRawTraffic(new LogEntryItem(entry.Timestamp, "Protocol", entry.Summary, detail, entry.Direction,
            protocol, ActivityFilter.IsFailure(entry)));
    }

    private void OnOutboundDeliveryFailed(object? sender, Exception exception) =>
        ReportError("Outbound delivery failed", exception);

    // Retain the full current application session. Panels filter their own views, never this history.
    // Appending also avoids shifting every retained record for each new event.
    private void AddLog(LogEntryItem entry) => RunOnUi(() => Logs.Add(entry));

    private void AddRawTraffic(LogEntryItem entry) => RunOnUi(() => RawTraffic.Add(entry));

    private void DetachSession(ProtocolSession session)
    {
        session.StateChanged -= OnSessionStateChanged;
        session.RoundTripTimeChanged -= OnRoundTripTimeChanged;
        session.TrafficObserved -= OnTrafficObserved;
        session.OutboundDeliveryFailed -= OnOutboundDeliveryFailed;
    }

    private async Task<AppPreferences> LoadPreferencesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_preferencesPath))
        {
            return new AppPreferences { AccessToken = CreateAccessToken() };
        }

        try
        {
            await using var stream = File.OpenRead(_preferencesPath);
            var preferences = await JsonSerializer.DeserializeAsync<AppPreferences>(
                stream,
                AppPreferences.SerializerOptions,
                cancellationToken) ?? new AppPreferences();
            var token = ReadAccessToken();
            return preferences with
            {
                AccessToken = string.IsNullOrWhiteSpace(token) ? CreateAccessToken() : token,
            };
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            ReportError("Settings could not be loaded", exception);
            return new AppPreferences { AccessToken = CreateAccessToken() };
        }
    }

    private async Task SavePreferencesAsync(AppPreferences preferences, CancellationToken cancellationToken)
    {
        var temporary = $"{_preferencesPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    preferences,
                    AppPreferences.SerializerOptions,
                    cancellationToken);
            }

            File.Move(temporary, _preferencesPath, true);
            SaveAccessToken(preferences.AccessToken);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string ReadAccessToken()
    {
        try
        {
            var credential = new PasswordVault().Retrieve(CredentialResource, CredentialUser);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static void SaveAccessToken(string token)
    {
        var vault = new PasswordVault();
        try
        {
            var existing = vault.Retrieve(CredentialResource, CredentialUser);
            vault.Remove(existing);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }

        if (!string.IsNullOrEmpty(token))
        {
            vault.Add(new PasswordCredential(CredentialResource, CredentialUser, token));
        }
    }

    private static string CreateAccessToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _ = _dispatcher.TryEnqueue(() => action());
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }))
        {
            completion.SetException(new InvalidOperationException("The UI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
