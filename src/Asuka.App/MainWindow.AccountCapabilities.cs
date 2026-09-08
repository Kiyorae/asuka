using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Asuka.App;

public sealed partial class MainWindow
{
    private async void AccountCredentials_Click(object sender, RoutedEventArgs args)
    {
        if (!_environment.Capabilities.AccountCredentials || _environment.IsShowcaseMode
            || _environment.BotPersona is null || !TryEnterDialog()) return;
        try
        {
            using var dialog = new AccountCredentialsDialog(_environment, this) { XamlRoot = RootGrid.XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { ExitDialog(); }
    }

    private async void CustomFaces_Click(object sender, RoutedEventArgs args)
    {
        if (!_environment.Capabilities.CustomFaces || !TryEnterDialog()) return;
        var context = _activeDraftKey;
        var chatId = _environment.SelectedChat?.Id;
        var selecting = ReferenceEquals(sender, CustomFacesButton);
        try
        {
            using var dialog = new CustomFacesDialog(_environment, this, allowSelection: selecting)
            {
                XamlRoot = RootGrid.XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && selecting
                && dialog.SelectedImage is { } image && context is not null && chatId is not null)
            {
                EnsureDraftContextIsCurrent(context, chatId);
                _richContent.Add(image);
                SaveActiveDraft();
                UpdateChatState();
            }
        }
        catch (Exception error) { ShowError(error); }
        finally { ExitDialog(); }
    }

    private async void ToggleBotPresence_Click(object sender, RoutedEventArgs args)
    {
        if (_environment.IsShowcaseMode || _environment.BotPersona is not { } bot || !TryEnterDialog()) return;
        var wasOnline = _environment.Platform.IsBotOnline(bot.Id);
        try
        {
            var reason = string.Empty;
            if (wasOnline)
            {
                var input = new TextBox
                {
                    Header = "Offline reason",
                    Text = "Disconnected from QQ",
                    MaxLength = 512,
                    TextWrapping = TextWrapping.Wrap,
                    MinWidth = 300,
                };
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = $"Set {bot.DisplayName} offline",
                    Content = input,
                    PrimaryButtonText = "Set offline",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                reason = input.Text;
            }
            if (_environment.BotPersona?.Id != bot.Id || _environment.Platform.IsBotOnline(bot.Id) != wasOnline)
                throw new InvalidOperationException("The bot account or its online status changed.");
            await _environment.Platform.SetBotPresenceAsync(bot.Id, !wasOnline, reason);
        }
        catch (Exception error) { ShowError(error); }
        finally { ExitDialog(); }
    }
}
