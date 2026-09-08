using System.ComponentModel;
using System.Globalization;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Asuka.App;

/// <summary>Edits explicit local simulator responses, without performing a QQ login.</summary>
public sealed class AccountCredentialsDialog : ContentDialog, IDisposable
{
    private readonly AppEnvironment _environment;
    private readonly Window _owner;
    private readonly string? _selfId;
    private readonly ProtocolKind _protocol;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly ListView _domains = new() { Height = 150, SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox _domain = new() { Header = "Exact cookie domain", PlaceholderText = "qun.qq.com", MaxLength = 254 };
    private readonly PasswordBox _cookie = new() { Header = "Cookie value", MaxLength = AccountCredentials.MaximumCookieLength, PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly PasswordBox _csrf = new() { Header = "CSRF token", MaxLength = AccountCredentials.MaximumCsrfTokenLength, PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly CheckBox _defaultDomain = new() { Content = "Default cookies for OneBot V11 (empty domain)" };
    private readonly CheckBox _remember = new() { Content = "Remember on this device" };
    private readonly Button _add = new() { Content = "Add / update domain" };
    private readonly Button _remove = new() { Content = "Remove selected" };
    private readonly Button _clear = new() { Content = "Clear all" };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private bool _opened;
    private bool _disposed;
    private bool _busy;
    private bool _loaded;
    private bool _updatingEditor;
    private bool _cookieEditorDirty;

    public AccountCredentialsDialog(AppEnvironment environment, Window owner)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(owner);
        _environment = environment;
        _owner = owner;
        _selfId = environment.BotPersona?.Id;
        _protocol = environment.Preferences.Protocol;
        _token = _lifetime.Token;
        Title = "Account credentials";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Close;
        XamlRoot = (owner.Content as FrameworkElement)?.XamlRoot;
        RequestedTheme = WindowChrome.ToElementTheme(environment.Preferences.Theme);
        Resources["ContentDialogMaxWidth"] = 680d;
        _defaultDomain.Visibility = _protocol == ProtocolKind.OneBotV11 ? Visibility.Visible : Visibility.Collapsed;
        var root = new StackPanel { Width = 580, Spacing = 10 };
        root.Children.Add(_status);
        root.Children.Add(new TextBlock
        {
            Text = $"{environment.BotPersona?.DisplayName ?? "No bot selected"} · {_selfId ?? "—"}\nConfigure local simulator responses. This does not sign in to QQ.",
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(new TextBlock
        {
            Text = "Cookies are returned only for the exact configured domain; subdomains and other domains do not share values. Missing domains or a blank CSRF token leave the corresponding API unavailable.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        root.Children.Add(_domains);
        root.Children.Add(_summary);
        root.Children.Add(_domain);
        root.Children.Add(_defaultDomain);
        root.Children.Add(_cookie);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(_add);
        toolbar.Children.Add(_remove);
        toolbar.Children.Add(_clear);
        root.Children.Add(toolbar);
        root.Children.Add(_csrf);
        root.Children.Add(new TextBlock
        {
            Text = _protocol == ProtocolKind.OneBotV11
                ? "OneBot V11 requires a 32-bit integer CSRF token, for example 123456."
                : "Milky returns the CSRF token exactly as configured.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        root.Children.Add(_remember);
        root.Children.Add(new TextBlock
        {
            Text = "When enabled, a small snapshot is stored in Windows Credential Locker for this bot account and data folder. Windows may sync locker entries through your Microsoft account. Larger values can be used for this app session with Remember turned off. Save with Remember off removes any previously saved snapshot.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        Content = new ScrollViewer { Content = root, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(_domains, "Configured cookie domains; credential values are hidden");
        _domains.SelectionChanged += (_, _) => SelectDomain();
        // CSRF and Remember are read independently on Save; neither dirties the cookie editor.
        _domain.TextChanged += (_, _) => CookieEditorChanged();
        _cookie.PasswordChanged += (_, _) => CookieEditorChanged();
        _defaultDomain.Checked += (_, _) => CookieEditorChanged();
        _defaultDomain.Unchecked += (_, _) => CookieEditorChanged();
        _add.Click += (_, _) => StageEditorWithStatus();
        _remove.Click += (_, _) => RemoveSelected();
        _clear.Click += (_, _) => ClearDraft();
        PrimaryButtonClick += Save;
        CloseButtonClick += (_, _) => _lifetime.Cancel();
        Opened += async (_, _) => await OpenAsync();
        Closed += (_, _) => Dispose();
        UpdateActions();
    }

    private bool ContextMatches => !_environment.IsShowcaseMode && _environment.Capabilities.AccountCredentials
        && _selfId is not null && _environment.BotPersona?.Id == _selfId && _environment.Preferences.Protocol == _protocol;
    private string? SelectedDomain => (_domains.SelectedItem as DomainItem)?.Domain;

    private async Task OpenAsync()
    {
        _opened = true;
        _busy = true;
        _environment.PropertyChanged += EnvironmentPropertyChanged;
        _environment.PreferencesChanged += ContextChanged;
        _owner.Closed += OwnerClosed;
        UpdateActions();
        try
        {
            RequireContext();
            var settings = await _environment.LoadAccountCredentialsSettingsAsync(_selfId!, _protocol, _token);
            RequireContext();
            foreach (var entry in settings.Credentials.Cookies) _cookies.Add(entry.Key, entry.Value);
            _csrf.Password = settings.Credentials.CsrfToken ?? string.Empty;
            _remember.IsChecked = settings.Remembered;
            _loaded = true;
            RefreshDomains();
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception) { ShowStatus("Credentials could not be loaded", "Reopen this dialog to retry, or choose Clear all and Save to remove this account's runtime and saved credentials.", InfoBarSeverity.Error); }
        finally { _busy = false; UpdateActions(); }
    }

    private void CookieEditorChanged()
    {
        if (!_updatingEditor) _cookieEditorDirty = true;
        UpdateActions();
    }

    private void SelectDomain()
    {
        if (_busy || !ContextMatches || SelectedDomain is not { } domain) return;
        _updatingEditor = true;
        try
        {
            _domain.Text = domain;
            _defaultDomain.IsChecked = domain.Length == 0;
            _cookie.Password = _cookies[domain];
            _cookieEditorDirty = false;
        }
        finally { _updatingEditor = false; }
        UpdateActions();
    }

    private void StageEditorWithStatus()
    {
        if (_busy || !ContextMatches) return;
        try { StageEditor(); _status.IsOpen = false; }
        catch (ArgumentException error) { ShowStatus("Check the credential fields", error.Message, InfoBarSeverity.Warning); }
    }

    private void StageEditor()
    {
        RequireContext();
        var useDefault = _protocol == ProtocolKind.OneBotV11 && _defaultDomain.IsChecked == true;
        if (!useDefault && string.IsNullOrWhiteSpace(_domain.Text))
            throw new ArgumentException("Enter an exact DNS domain or select the OneBot V11 default scope.");
        var domain = useDefault ? string.Empty : AccountCredentials.NormalizeDomain(_domain.Text);
        var candidate = new Dictionary<string, string>(_cookies, StringComparer.Ordinal) { [domain] = _cookie.Password };
        var validated = new AccountCredentials(candidate, _csrf.Password.Length == 0 ? null : _csrf.Password);
        _cookies.Clear();
        foreach (var entry in validated.Cookies) _cookies.Add(entry.Key, entry.Value);
        ClearEditor();
        RefreshDomains();
    }

    private void RemoveSelected()
    {
        if (_busy || !ContextMatches || SelectedDomain is not { } domain) return;
        _cookies.Remove(domain);
        ClearEditor();
        RefreshDomains();
    }

    private void ClearDraft()
    {
        if (_busy || !ContextMatches) return;
        _cookies.Clear();
        _csrf.Password = string.Empty;
        _remember.IsChecked = false;
        _loaded = true;
        ClearEditor();
        RefreshDomains();
        ShowStatus("All credentials cleared from this draft", "Choose Save to remove this account's runtime and remembered credentials.", InfoBarSeverity.Informational);
    }

    private void ClearEditor()
    {
        _updatingEditor = true;
        try
        {
            _domain.Text = string.Empty;
            _cookie.Password = string.Empty;
            _defaultDomain.IsChecked = false;
            _cookieEditorDirty = false;
        }
        finally { _updatingEditor = false; }
    }

    private void RefreshDomains()
    {
        _domains.ItemsSource = _cookies.Keys.Where(domain => domain.Length != 0 || _protocol == ProtocolKind.OneBotV11)
            .Order(StringComparer.Ordinal).Select(domain => new DomainItem(domain)).ToArray();
        _summary.Text = $"{_cookies.Count} configured domain(s). Add or update entries, then Save to apply all changes together.";
        UpdateActions();
    }

    private async void Save(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_busy || !_loaded || !ContextMatches) return;
        var deferral = args.GetDeferral();
        _busy = true;
        UpdateActions();
        try
        {
            RequireContext();
            if (_cookieEditorDirty) StageEditor();
            var credentials = new AccountCredentials(_cookies, _csrf.Password.Length == 0 ? null : _csrf.Password);
            if (_protocol == ProtocolKind.OneBotV11 && credentials.CsrfToken is not null
                && !int.TryParse(credentials.CsrfToken, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                throw new ArgumentException("OneBot V11 requires a 32-bit integer CSRF token. Use a compatible value or leave the token blank.");
            await _environment.SaveAccountCredentialsAsync(_selfId!, _protocol, credentials, _remember.IsChecked == true, _token);
            RequireContext();
            args.Cancel = false;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (ArgumentException error) { ShowStatus("Check the credential fields", error.Message, InfoBarSeverity.Warning); }
        catch (InvalidOperationException error) { ShowStatus("Credentials were not saved", error.Message, InfoBarSeverity.Error); }
        catch (Exception) { ShowStatus("Credentials were not saved", "The local credential update failed. Reopen this dialog to retry.", InfoBarSeverity.Error); }
        finally { _busy = false; UpdateActions(); deferral.Complete(); }
    }

    private void EnvironmentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppEnvironment.BotPersona) or nameof(AppEnvironment.Preferences)) ContextChanged(sender, EventArgs.Empty);
    }

    private void ContextChanged(object? sender, EventArgs args)
    {
        if (_disposed || ContextMatches) return;
        _lifetime.Cancel();
        _cookies.Clear();
        ClearEditor();
        _csrf.Password = string.Empty;
        _domains.ItemsSource = null;
        ShowStatus("Account credentials unavailable", "The bot account or protocol changed. Reopen this dialog to continue.", InfoBarSeverity.Warning);
        UpdateActions();
    }

    private void RequireContext()
    {
        _token.ThrowIfCancellationRequested();
        if (!_opened || !ContextMatches) throw new InvalidOperationException("The bot account or protocol changed. Reopen Account credentials to continue.");
    }

    private void UpdateActions()
    {
        var available = _opened && !_busy && !_disposed && ContextMatches && !_token.IsCancellationRequested;
        var enabled = available && _loaded;
        IsPrimaryButtonEnabled = enabled;
        _domains.IsEnabled = enabled;
        _domain.IsEnabled = enabled && _defaultDomain.IsChecked != true;
        _cookie.IsEnabled = enabled;
        _csrf.IsEnabled = enabled;
        _defaultDomain.IsEnabled = enabled;
        _remember.IsEnabled = enabled;
        _add.IsEnabled = enabled;
        _remove.IsEnabled = enabled && SelectedDomain is not null;
        _clear.IsEnabled = available;
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        if (!_opened || _disposed) return;
        _status.Title = title;
        _status.Message = message;
        _status.Severity = severity;
        _status.IsOpen = true;
    }

    private void OwnerClosed(object sender, WindowEventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _opened = false;
        _environment.PropertyChanged -= EnvironmentPropertyChanged;
        _environment.PreferencesChanged -= ContextChanged;
        _owner.Closed -= OwnerClosed;
        _lifetime.Cancel();
        _cookies.Clear();
        _cookie.Password = string.Empty;
        _csrf.Password = string.Empty;
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record DomainItem(string Domain)
    {
        public override string ToString() => Domain.Length == 0 ? "Default cookies (OneBot V11)" : Domain;
    }
}
