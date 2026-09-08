using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.App;

public sealed partial class AppEnvironment
{
    private readonly DemoLaunchOptions _launchOptions;
    private readonly SemaphoreSlim _showcaseGate = new(1, 1);
    private readonly CancellationTokenSource _showcaseLifetime = new();
    private ClientShowcase? _showcase;
    private ProtocolKind _showcaseProtocol;
    private bool _showcaseStopped;

    public bool IsShowcaseMode => _launchOptions.Enabled;
    public TimeSpan ShowcaseInterval => TimeSpan.FromSeconds(_launchOptions.IntervalSeconds);

    private async Task InitializeShowcaseAsync(CancellationToken cancellationToken)
    {
        Preferences = new AppPreferences
        {
            Protocol = _launchOptions.Protocol,
            ActiveUserId = ClientShowcase.AliceId,
            BotUserId = ClientShowcase.BotId,
            AccessToken = CreateAccessToken(),
        };
        async Task<Asset> ImportAsync(string fileName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Showcase", fileName);
            await using var source = File.OpenRead(path);
            var asset = await Assets.StoreAsync(source, fileName, AssetStore.MimeTypeForFileName(fileName), cancellationToken);
            await Platform.SaveAssetAsync(asset, cancellationToken);
            return asset;
        }

        var video = File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Showcase", "logo-spin.mp4"))
            ? "logo-spin.mp4" : "logo-spin.webm";
        var media = new ShowcaseAssets(await ImportAsync("logo.png"), await ImportAsync("logo-spin.gif"),
            await ImportAsync("voice.silk"), await ImportAsync(video), await ImportAsync("notes.txt"), await ImportAsync("voice.wav"));
        _showcase = new ClientShowcase(Platform, media);
        await _showcase.EnsureSeededAsync(cancellationToken);
        await _showcase.ResetAsync(cancellationToken);
        await Platform.SetAvatarAsync(ClientShowcase.BotId, new Uri(Assets.LocationOf(media.Logo.Id)).AbsoluteUri, cancellationToken);
        CurrentPersona = await Store.GetUserAsync(ClientShowcase.AliceId, cancellationToken);
        BotPersona = await Store.GetUserAsync(ClientShowcase.BotId, cancellationToken);
        Platform.SetRegisteredBot(ClientShowcase.BotId);
        _showcaseProtocol = Preferences.Protocol;
        await RefreshAllAsync(cancellationToken);
        await SelectChatAsync(ClientShowcase.GroupChat, "Asuka showcase", cancellationToken);
        ConnectionStatus = "Demo · local playback";
        RoundTripText = "Local demo · group and private messages";
        AddLog("Demo", "Ready", "Isolated showcase workspace. No protocol listener or remote connection is started.");
    }

    public async Task<ShowcaseStep?> AdvanceShowcaseAsync(CancellationToken cancellationToken = default)
    {
        if (!IsShowcaseMode || _showcase is null || _disposed || _showcaseStopped) return null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _showcaseLifetime.Token);
        await _showcaseGate.WaitAsync(cancellation.Token);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (BotPersona?.Id != ClientShowcase.BotId)
                throw new InvalidOperationException("Select the demo bot account before resuming playback.");
            if (_showcaseProtocol != Preferences.Protocol)
            {
                await _showcase.ResetAsync(cancellation.Token);
                _showcaseProtocol = Preferences.Protocol;
            }
            var step = await _showcase.StepAsync(_showcaseProtocol, cancellation.Token);
            await RefreshAllAsync(cancellation.Token);
            return step;
        }
        finally { _showcaseGate.Release(); }
    }

    public async Task SelectShowcaseChatAsync(bool group, CancellationToken cancellationToken = default)
    {
        if (!IsShowcaseMode) return;
        if (BotPersona?.Id != ClientShowcase.BotId || CurrentPersona?.Id is not (ClientShowcase.AliceId or ClientShowcase.BotId))
            await ApplyPreferencesAsync(Preferences with { ActiveUserId = ClientShowcase.AliceId, BotUserId = ClientShowcase.BotId }, cancellationToken);
        var chat = group ? ClientShowcase.GroupChat : ClientShowcase.PrivateChat;
        await SelectChatAsync(chat, group ? "Asuka showcase" : null, cancellationToken);
    }

    private async Task StopShowcaseAsync()
    {
        _showcaseStopped = true;
        _showcaseLifetime.Cancel();
        await _showcaseGate.WaitAsync();
        _showcaseGate.Release();
        _showcaseLifetime.Dispose();
        _showcaseGate.Dispose();
    }
}
