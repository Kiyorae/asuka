using Asuka.Core;
using Microsoft.UI.Xaml.Controls;

namespace Asuka.App;

public sealed partial class MainWindow
{
    private void AddGroupHonorActions(MenuFlyout flyout, Chat chat)
    {
        if (!_environment.Capabilities.GroupHonors || chat.Scene != ChatScene.Group) return;
        var honors = new MenuFlyoutItem { Text = "Group honors & notices" };
        honors.Click += async (_, _) =>
        {
            if (!_environment.Capabilities.GroupHonors || !TryEnterDialog()) return;
            try
            {
                if (_environment.BotPersona?.Id != chat.SelfId)
                    throw new InvalidOperationException("The bot account changed. Reopen this conversation.");
                using var dialog = new GroupHonorsDialog(_environment, chat, this) { XamlRoot = RootGrid.XamlRoot };
                await dialog.ShowAsync();
            }
            catch (Exception error) { ShowError(error); }
            finally { ExitDialog(); }
        };
        flyout.Items.Add(honors);
    }
}
