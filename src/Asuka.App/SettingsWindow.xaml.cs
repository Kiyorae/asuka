using Asuka.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Asuka.App;

public sealed partial class SettingsWindow : Window
{
    private readonly AppEnvironment _environment;

    public SettingsWindow(AppEnvironment environment)
    {
        _environment = environment;
        InitializeComponent();
        Title = "Asuka Settings";
        WindowChrome.Configure(this, 760, 650);
        RootGrid.RequestedTheme = WindowChrome.ToElementTheme(environment.Preferences.Theme);
        WireUiEvents();
        InstallKeyboardAccelerators();
        ProtocolBox.ItemsSource = Enum.GetValues<ProtocolKind>();
        ThemeBox.ItemsSource = Enum.GetValues<AppThemePreference>();
        ActivePersonaBox.ItemsSource = environment.Personas;
        BotPersonaBox.ItemsSource = environment.Personas;
        LoadDraft();
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
        CancelButton.Click += Cancel_Click;
        SaveButton.Click += Save_Click;
    }

    private void InstallKeyboardAccelerators()
    {
        var save = new KeyboardAccelerator
        {
            Key = VirtualKey.S,
            Modifiers = VirtualKeyModifiers.Control,
        };
        save.Invoked += (_, args) =>
        {
            args.Handled = true;
            Save_Click(this, new RoutedEventArgs());
        };
        RootGrid.KeyboardAccelerators.Add(save);

        var cancel = new KeyboardAccelerator { Key = VirtualKey.Escape };
        cancel.Invoked += (_, args) =>
        {
            args.Handled = true;
            Close();
        };
        RootGrid.KeyboardAccelerators.Add(cancel);
    }

    private void LoadDraft()
    {
        var draft = _environment.Preferences;
        ProtocolBox.SelectedItem = draft.Protocol;
        TransportBox.SelectedItem = draft.Protocol == ProtocolKind.Milky ? TransportMode.MilkyService : draft.Transport;
        HostBox.Text = draft.Host;
        PortBox.Value = draft.Port;
        PathBox.Text = draft.Path;
        AdvertisedHostBox.Text = draft.AdvertisedHost ?? string.Empty;
        TokenBox.Password = draft.AccessToken;
        WebhookBox.Text = draft.WebhookUrls;
        ReconnectSwitch.IsOn = draft.AutoReconnect;
        ReconnectBox.Value = draft.ReconnectSeconds;
        SelfEventsSwitch.IsOn = draft.PostSelfEvents;
        ActivePersonaBox.SelectedItem = _environment.Personas.FirstOrDefault(item =>
            item.Id == (draft.ActiveUserId ?? draft.PersonaId));
        BotPersonaBox.SelectedItem = _environment.Personas.FirstOrDefault(item =>
            item.Id == (draft.BotUserId ?? draft.PersonaId));
        ThemeBox.SelectedItem = draft.Theme;
        UpdateCompatibility();
        UpdateEndpointPreview();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
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
                Transport = protocol == ProtocolKind.Milky ? TransportMode.MilkyService : transport,
                Host = HostBox.Text,
                Port = port,
                Path = PathBox.Text,
                AdvertisedHost = string.IsNullOrWhiteSpace(AdvertisedHostBox.Text)
                    ? null
                    : AdvertisedHostBox.Text.Trim(),
                AccessToken = TokenBox.Password,
                WebhookUrls = WebhookBox.Text,
                AutoReconnect = ReconnectSwitch.IsOn,
                ReconnectSeconds = reconnect,
                PostSelfEvents = SelfEventsSwitch.IsOn,
                ActiveUserId = activePersona.Id,
                BotUserId = botPersona.Id,
                Theme = theme,
            };
            await _environment.ApplyPreferencesAsync(preferences);
            Close();
        }
        catch (Exception exception)
        {
            ValidationInfoBar.Message = exception.Message;
            ValidationInfoBar.IsOpen = true;
            _environment.ReportError("Settings save failed", exception);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCompatibility();
        UpdateEndpointPreview();
    }

    private void TransportBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEndpointPreview();

    private void ConnectionTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateEndpointPreview();

    private void PortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateEndpointPreview();

    private void UpdateCompatibility()
    {
        if (ProtocolBox.SelectedItem is not ProtocolKind protocol)
        {
            return;
        }

        if (protocol == ProtocolKind.Milky)
        {
            TransportBox.ItemsSource = new[] { TransportMode.MilkyService };
            TransportBox.SelectedItem = TransportMode.MilkyService;
            TransportBox.IsEnabled = false;
            PathBox.IsEnabled = false;
            WebhookBox.IsEnabled = true;
        }
        else
        {
            var selected = TransportBox.SelectedItem is TransportMode mode
                && mode != TransportMode.MilkyService
                ? mode
                : TransportMode.WebSocketServer;
            TransportBox.ItemsSource = new[]
            {
                TransportMode.WebSocketServer,
                TransportMode.WebSocketClient,
            };
            TransportBox.IsEnabled = true;
            PathBox.IsEnabled = true;
            WebhookBox.IsEnabled = false;
            TransportBox.SelectedItem = selected;
        }
    }

    private void UpdateEndpointPreview()
    {
        if (EndpointPreview is null || HostBox is null || PortBox is null)
        {
            return;
        }

        var host = string.IsNullOrWhiteSpace(HostBox.Text) ? "127.0.0.1" : HostBox.Text.Trim();
        var port = double.IsFinite(PortBox.Value) && PortBox.Value is >= 1 and <= 65535
            ? (int)PortBox.Value
            : 5700;
        var advertisedHost = string.IsNullOrWhiteSpace(AdvertisedHostBox.Text)
            ? host
            : AdvertisedHostBox.Text.Trim();
        if (ProtocolBox.SelectedItem is ProtocolKind.Milky)
        {
            EndpointPreview.Text =
                $"HTTP API: http://{host}:{port}/api/*    Events: ws://{host}:{port}/event\n"
                + $"Media URLs: http://{advertisedHost}:{port}/assets/*";
            return;
        }

        var path = string.IsNullOrWhiteSpace(PathBox.Text)
            ? ProtocolBox.SelectedItem is ProtocolKind.OneBotV12 ? "/onebot/v12/ws" : "/onebot/v11/ws"
            : PathBox.Text.Trim();
        if (path.Length == 0 || path[0] != '/')
        {
            path = $"/{path}";
        }

        EndpointPreview.Text =
            $"WebSocket: ws://{host}:{port}{path}\n"
            + $"Media URLs: http://{advertisedHost}:{port}/assets/*";
    }

    private async void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(
                Path.GetDirectoryName(_environment.Store.DatabasePath)
                ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData));
            if (!await Launcher.LaunchFolderAsync(folder))
            {
                throw new InvalidOperationException("Windows could not open the Asuka data folder.");
            }
        }
        catch (Exception exception)
        {
            ValidationInfoBar.Message = exception.Message;
            ValidationInfoBar.IsOpen = true;
            _environment.ReportError("Data folder could not be opened", exception);
        }
    }
}
