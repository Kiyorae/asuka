using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Asuka.App;

public sealed partial class MainWindow
{
    private bool _cleaningCache;

    private void UpdateMaintenanceControls()
    {
        CacheCleanupButton.Visibility = _environment.Capabilities.CacheCleanup ? Visibility.Visible : Visibility.Collapsed;
        RestartProtocolButton.Visibility = _environment.Capabilities.ImplementationRestart ? Visibility.Visible : Visibility.Collapsed;
        CacheCleanupButton.IsEnabled = !_closed && !_cleaningCache;
        RestartProtocolButton.IsEnabled = !_closed && _environment.CanRestartProtocol;
    }

    private void RestartProtocol_Click(object sender, RoutedEventArgs args)
    {
        if (_closed || !_environment.CanRestartProtocol) return;
        try
        {
            _environment.RestartProtocol();
            ErrorInfoBar.Severity = InfoBarSeverity.Informational;
            ErrorInfoBar.Message = "Restarting the OneBot V11 service…";
            ErrorInfoBar.IsOpen = true;
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void CacheCleanup_Click(object sender, RoutedEventArgs args)
    {
        if (_closed || _cleaningCache || !_environment.Capabilities.CacheCleanup) return;
        _cleaningCache = true;
        UpdateMaintenanceControls();
        using var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        void OnOwnerClosed(object sender, WindowEventArgs args) => lifetime.Cancel();
        Closed += OnOwnerClosed;
        try
        {
            var result = await _environment.CleanMediaCacheAsync(token);
            if (_closed) return;
            ErrorInfoBar.Severity = InfoBarSeverity.Success;
            ErrorInfoBar.Message = $"Cleared {result.DeletedFiles} cached files ({result.DeletedBytes / 1048576d:0.##} MB)."
                + (result.SkippedFiles > 0 ? $" Skipped {result.SkippedFiles} entries that are in use or were kept for safety." : string.Empty);
            ErrorInfoBar.IsOpen = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) ShowError(error); }
        finally
        {
            Closed -= OnOwnerClosed;
            _cleaningCache = false;
            if (!_closed) UpdateMaintenanceControls();
        }
    }
}
