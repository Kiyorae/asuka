using Asuka.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Asuka.App;

public sealed partial class MainWindow
{
    private void AddAnonymousModeration(MenuFlyout flyout, MessageItem item)
    {
        if (item.Message.Anonymous is null || !_environment.Capabilities.SupportsAnonymous(item.Message.Scene)) return;
        var ban = new MenuFlyoutItem { Text = "Mute anonymous sender…", IsEnabled = false };
        flyout.Items.Add(ban);
        flyout.Opening += async (_, _) =>
        {
            ban.IsEnabled = false;
            var actorId = _environment.CurrentPersona?.Id;
            try
            {
                var member = actorId is null ? null : await _environment.Store.GetMemberAsync(item.Message.PeerId, actorId);
                ban.IsEnabled = !_closed && _environment.CurrentPersona?.Id == actorId
                    && item.Message.Chat == _environment.SelectedChat && _environment.BotPersona?.Id == item.Message.SelfId
                    && _environment.Capabilities.SupportsAnonymous(item.Message.Scene)
                    && member?.Role is GroupRole.Admin or GroupRole.Owner;
            }
            catch (Exception error) { if (!_closed) ShowError(error); }
        };
        ban.Click += async (_, _) => await MuteAnonymousSenderAsync(item);
    }

    private async Task MuteAnonymousSenderAsync(MessageItem item)
    {
        if (item.Message.Anonymous is not { } anonymous || !_environment.Capabilities.SupportsAnonymous(item.Message.Scene)
            || item.Message.Chat != _environment.SelectedChat || !TryEnterDialog()) return;
        using var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        void OnOwnerClosed(object sender, WindowEventArgs args) => lifetime.Cancel();
        Closed += OnOwnerClosed;
        var actorId = _environment.CurrentPersona?.Id;
        try
        {
            var duration = new NumberBox
            {
                Header = "Duration in seconds",
                Value = 1800,
                Minimum = 1,
                Maximum = int.MaxValue,
                SmallChange = 60,
                LargeChange = 3600,
            };
            var content = new StackPanel { Spacing = 12, MinWidth = 320 };
            content.Children.Add(new TextBlock { Text = anonymous.Name, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(duration);
            content.Children.Add(new TextBlock
            {
                Text = "This mute applies to the anonymous identity. It cannot be shortened or canceled; it expires after its duration.",
                TextWrapping = TextWrapping.Wrap,
            });
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Mute anonymous sender",
                Content = content,
                PrimaryButtonText = "Mute",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            token.ThrowIfCancellationRequested();
            if (_closed || _environment.CurrentPersona?.Id != actorId || actorId is null
                || _environment.BotPersona?.Id != item.Message.SelfId || _environment.SelectedChat != item.Message.Chat
                || !_environment.Capabilities.SupportsAnonymous(item.Message.Scene))
                throw new InvalidOperationException("The account, protocol or conversation changed. Reopen the message menu.");
            if (!double.IsFinite(duration.Value) || duration.Value < 1 || duration.Value > int.MaxValue)
                throw new InvalidOperationException("Enter a positive mute duration.");
            await _environment.Platform.BanAnonymousAsync(item.Message.PeerId, anonymous.Flag, actorId,
                TimeSpan.FromSeconds(duration.Value), token);
            if (!_closed)
            {
                ErrorInfoBar.Severity = InfoBarSeverity.Success;
                ErrorInfoBar.Message = $"Muted {anonymous.Name}";
                ErrorInfoBar.IsOpen = true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) ShowError(error); }
        finally { Closed -= OnOwnerClosed; ExitDialog(); }
    }
}
