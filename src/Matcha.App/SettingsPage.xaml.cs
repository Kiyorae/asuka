using Matcha.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Matcha.App;

/// <summary>
/// An in-place editor for application settings. The hosting page owns its title and navigation.
/// </summary>
public sealed partial class SettingsPage : UserControl, IDisposable
{
    private static readonly TransportMode[] OneBotTransportModes =
    [
        TransportMode.WebSocketServer,
        TransportMode.WebSocketClient,
    ];

    private AppEnvironment? _environment;
    private Window? _owner;
    private bool _initialized;
    private bool _disposed;
    private bool _isLoadingDraft;
    private bool _isUpdatingCompatibility;
    private bool _isSaving;
    private TransportMode _lastOneBotTransport = TransportMode.WebSocketServer;

    public SettingsPage()
    {
        InitializeComponent();
        WireUiEvents();
        InstallKeyboardAccelerators();
        ProtocolBox.ItemsSource = Enum.GetValues<ProtocolKind>();
        // Keep this source stable. Rebinding it during protocol selection forces WinUI to
        // recreate the popup and can briefly put the pointer into its busy state.
        TransportBox.ItemsSource = OneBotTransportModes;
        ThemeBox.ItemsSource = Enum.GetValues<AppThemePreference>();
    }

    /// <summary>Associates the editor with its current application environment and host window.</summary>
    public void Initialize(AppEnvironment environment, Window owner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(owner);

        if (_initialized)
        {
            if (ReferenceEquals(_environment, environment) && ReferenceEquals(_owner, owner))
            {
                LoadDraft();
                return;
            }

            throw new InvalidOperationException("SettingsPage is already initialized for a different host.");
        }

        _environment = environment;
        _owner = owner;
        _initialized = true;
        RootGrid.RequestedTheme = WindowChrome.ToElementTheme(environment.Preferences.Theme);
        ActivePersonaBox.ItemsSource = environment.Personas;
        BotPersonaBox.ItemsSource = environment.Personas;
        LoadDraft();
    }

    /// <summary>Reloads controls from the saved application preferences, discarding unsaved edits.</summary>
    public void LoadDraft()
    {
        if (_isSaving)
        {
            return;
        }

        LoadDraftCore();
    }

    private void LoadDraftCore()
    {
        var environment = RequireEnvironment();
        var draft = environment.Preferences;
        _isLoadingDraft = true;
        try
        {
            _lastOneBotTransport = draft.Transport is TransportMode.WebSocketServer or TransportMode.WebSocketClient
                ? draft.Transport
                : TransportMode.WebSocketServer;
            ProtocolBox.SelectedItem = draft.Protocol;
            TransportBox.SelectedItem = draft.Protocol == ProtocolKind.Milky ? null : _lastOneBotTransport;
            HostBox.Text = draft.Host;
            PortBox.Value = draft.Port;
            PathBox.Text = draft.Path;
            AdvertisedHostBox.Text = draft.AdvertisedHost ?? string.Empty;
            TokenBox.Password = draft.AccessToken;
            WebhookBox.Text = draft.WebhookUrls;
            ReconnectSwitch.IsOn = draft.AutoReconnect;
            ReconnectBox.Value = draft.ReconnectSeconds;
            SelfEventsSwitch.IsOn = draft.PostSelfEvents;
            ActivePersonaBox.SelectedItem = environment.Personas.FirstOrDefault(item =>
                item.Id == (draft.ActiveUserId ?? draft.PersonaId));
            BotPersonaBox.SelectedItem = environment.Personas.FirstOrDefault(item =>
                item.Id == (draft.BotUserId ?? draft.PersonaId));
            ThemeBox.SelectedItem = draft.Theme;
            ValidationInfoBar.IsOpen = false;
            UpdateCompatibility();
            UpdateEndpointPreview();
        }
        finally
        {
            _isLoadingDraft = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initialized = false;
        _environment = null;
        _owner = null;
    }

    private void WireUiEvents()
    {
        ProtocolBox.SelectionChanged += ProtocolBox_SelectionChanged;
        TransportBox.SelectionChanged += TransportBox_SelectionChanged;
        HostBox.TextChanged += ConnectionTextBox_TextChanged;
        PathBox.TextChanged += ConnectionTextBox_TextChanged;
        AdvertisedHostBox.TextChanged += ConnectionTextBox_TextChanged;
        PortBox.ValueChanged += PortBox_ValueChanged;
        OpenDataFolderButton.Click += OpenDataFolder_Click;
        ResetButton.Click += Reset_Click;
        SaveButton.Click += Save_Click;
    }

    private void InstallKeyboardAccelerators()
    {
        var save = new KeyboardAccelerator { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control };
        save.Invoked += (_, args) =>
        {
            if (Visibility != Visibility.Visible || !_initialized || _disposed)
            {
                return;
            }

            args.Handled = true;
            if (!_isSaving)
            {
                Save_Click(this, new RoutedEventArgs());
            }
        };
        RootGrid.KeyboardAccelerators.Add(save);

        var reset = new KeyboardAccelerator { Key = VirtualKey.Escape };
        reset.Invoked += (_, args) =>
        {
            if (Visibility != Visibility.Visible || !_initialized || _disposed)
            {
                return;
            }

            args.Handled = true;
            if (!_isSaving)
            {
                LoadDraft();
            }
        };
        RootGrid.KeyboardAccelerators.Add(reset);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving)
        {
            return;
        }

        _isSaving = true;
        SettingsEditor.IsEnabled = false;
        SaveButton.IsEnabled = false;
        ResetButton.IsEnabled = false;
        var originalSaveContent = SaveButton.Content;
        SaveButton.Content = "Saving…";
        try
        {
            var environment = RequireEnvironment();
            ValidationInfoBar.IsOpen = false;
            var protocol = ProtocolBox.SelectedItem is ProtocolKind selectedProtocol
                ? selectedProtocol
                : ProtocolKind.OneBotV11;
            var transport = TransportBox.SelectedItem is TransportMode selectedTransport
                ? selectedTransport
                : TransportMode.WebSocketServer;
            var port = double.IsFinite(PortBox.Value) && PortBox.Value is >= 1 and <= 65535
                ? (ushort)PortBox.Value
                : throw new InvalidOperationException("Port must be between 1 and 65535.");
            var reconnect = double.IsFinite(ReconnectBox.Value) && ReconnectBox.Value is >= 1 and <= 60
                ? (int)ReconnectBox.Value
                : throw new InvalidOperationException("Reconnect delay must be between 1 and 60 seconds.");
            var activePersona = ActivePersonaBox.SelectedItem as PersonaItem
                ?? throw new InvalidOperationException("Choose a sending identity.");
            var botPersona = BotPersonaBox.SelectedItem as PersonaItem
                ?? throw new InvalidOperationException("Choose a bot account.");
            var theme = ThemeBox.SelectedItem is AppThemePreference selectedTheme
                ? selectedTheme
                : AppThemePreference.System;
            var preferences = new AppPreferences
            {
                Protocol = protocol,
                Transport = protocol == ProtocolKind.Milky
                    ? TransportMode.MilkyService
                    : transport is TransportMode.WebSocketServer or TransportMode.WebSocketClient
                        ? transport
                        : _lastOneBotTransport,
                Host = HostBox.Text,
                Port = port,
                Path = PathBox.Text,
                AdvertisedHost = string.IsNullOrWhiteSpace(AdvertisedHostBox.Text) ? null : AdvertisedHostBox.Text.Trim(),
                AccessToken = TokenBox.Password,
                WebhookUrls = WebhookBox.Text,
                AutoReconnect = ReconnectSwitch.IsOn,
                ReconnectSeconds = reconnect,
                PostSelfEvents = SelfEventsSwitch.IsOn,
                ActiveUserId = activePersona.Id,
                BotUserId = botPersona.Id,
                Theme = theme,
            };
            await environment.ApplyPreferencesAsync(preferences);
            LoadDraftCore();
            RootGrid.RequestedTheme = WindowChrome.ToElementTheme(environment.Preferences.Theme);
            ValidationInfoBar.Title = "Settings saved";
            ValidationInfoBar.Message = "Your changes are now active.";
            ValidationInfoBar.Severity = InfoBarSeverity.Success;
            ValidationInfoBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            ValidationInfoBar.Title = "Settings couldn't be saved";
            ValidationInfoBar.Message = exception.Message;
            ValidationInfoBar.Severity = InfoBarSeverity.Error;
            ValidationInfoBar.IsOpen = true;
            _environment?.ReportError("Settings save failed", exception);
        }
        finally
        {
            SaveButton.Content = originalSaveContent;
            SettingsEditor.IsEnabled = true;
            SaveButton.IsEnabled = true;
            ResetButton.IsEnabled = true;
            _isSaving = false;
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => LoadDraft();

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingDraft || _isUpdatingCompatibility)
        {
            return;
        }

        UpdateCompatibility();
        UpdateEndpointPreview();
    }

    private void TransportBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingDraft || _isUpdatingCompatibility)
        {
            return;
        }

        if (ProtocolBox.SelectedItem is ProtocolKind.Milky)
        {
            return;
        }

        if (TransportBox.SelectedItem is TransportMode transport)
        {
            _lastOneBotTransport = transport;
        }

        UpdateEndpointPreview();
    }

    private void ConnectionTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateEndpointPreview();

    private void PortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateEndpointPreview();

    private void UpdateCompatibility()
    {
        if (_isUpdatingCompatibility || ProtocolBox.SelectedItem is not ProtocolKind protocol)
        {
            return;
        }

        _isUpdatingCompatibility = true;
        try
        {
            if (protocol == ProtocolKind.Milky)
            {
                if (TransportBox.SelectedItem is TransportMode mode)
                {
                    _lastOneBotTransport = mode;
                }

                TransportBox.SelectedItem = null;
                TransportBox.PlaceholderText = nameof(TransportMode.MilkyService);
                TransportBox.IsEnabled = false;
                PathBox.IsEnabled = false;
                WebhookBox.IsEnabled = true;
                return;
            }

            TransportBox.IsEnabled = true;
            TransportBox.PlaceholderText = "Choose a transport";
            PathBox.IsEnabled = true;
            WebhookBox.IsEnabled = false;
            if (TransportBox.SelectedItem is not TransportMode)
            {
                TransportBox.SelectedItem = _lastOneBotTransport;
            }
        }
        finally
        {
            _isUpdatingCompatibility = false;
        }
    }

    private void UpdateEndpointPreview()
    {
        var host = string.IsNullOrWhiteSpace(HostBox.Text) ? "127.0.0.1" : HostBox.Text.Trim();
        var port = double.IsFinite(PortBox.Value) && PortBox.Value is >= 1 and <= 65535 ? (int)PortBox.Value : 5700;
        var advertisedHost = string.IsNullOrWhiteSpace(AdvertisedHostBox.Text) ? host : AdvertisedHostBox.Text.Trim();
        if (ProtocolBox.SelectedItem is ProtocolKind.Milky)
        {
            EndpointPreview.Text = $"HTTP API: http://{host}:{port}/api/*    Events: ws://{host}:{port}/event\nMedia URLs: http://{advertisedHost}:{port}/assets/*";
            return;
        }

        var path = string.IsNullOrWhiteSpace(PathBox.Text)
            ? ProtocolBox.SelectedItem is ProtocolKind.OneBotV12 ? "/onebot/v12/ws" : "/onebot/v11/ws"
            : PathBox.Text.Trim();
        if (path.Length == 0 || path[0] != '/')
        {
            path = $"/{path}";
        }

        EndpointPreview.Text = $"WebSocket: ws://{host}:{port}{path}\nMedia URLs: http://{advertisedHost}:{port}/assets/*";
    }

    private async void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = RequireEnvironment();
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(
                Path.GetDirectoryName(environment.Store.DatabasePath)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (!await Launcher.LaunchFolderAsync(folder))
            {
                throw new InvalidOperationException("Windows could not open the Matcha data folder.");
            }
        }
        catch (Exception exception)
        {
            ValidationInfoBar.Title = "Data folder couldn't be opened";
            ValidationInfoBar.Message = exception.Message;
            ValidationInfoBar.Severity = InfoBarSeverity.Error;
            ValidationInfoBar.IsOpen = true;
            _environment?.ReportError("Data folder could not be opened", exception);
        }
    }

    private AppEnvironment RequireEnvironment() => _initialized && _environment is not null
        ? _environment
        : throw new InvalidOperationException("SettingsPage must be initialized before it is used.");
}
