using Asuka.Protocols;

namespace Asuka.App;

public sealed partial class AppEnvironment
{
    private DemoLaunchOptions? _savedDemoMode;

    public bool DemoModeEnabledForNextLaunch => _savedDemoMode?.Enabled ?? IsShowcaseMode;

    internal DemoLaunchOptions DemoModeForNextLaunch => _savedDemoMode ?? _launchOptions;

    public async Task SaveDemoModeAsync(bool enabled, ProtocolKind protocol, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var options = new DemoLaunchOptions(enabled, protocol, _launchOptions.IntervalSeconds);
        var settings = new DemoModeSettingsStore(AppStoragePaths.GetStartupSettingsPath());
        await settings.SaveAsync(options, cancellationToken);
        if (_disposed) return;
        _savedDemoMode = options;
        RaisePropertyChanged(nameof(DemoModeEnabledForNextLaunch));
    }
}
