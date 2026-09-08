using Asuka.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Asuka.App;

public sealed partial class MainWindow
{
    private void AddConversationActions(MenuFlyout flyout, ConversationItem item)
    {
        if (_environment.Capabilities.GroupContent && item.Chat.Scene == ChatScene.Group)
        {
            var content = new MenuFlyoutItem { Text = "Announcements & essence" };
            content.Click += async (_, _) => await ShowGroupContentAsync(item.Chat);
            flyout.Items.Add(content);
        }

        if (_environment.Capabilities.ProfileLikes && item.Chat.Scene == ChatScene.Friend)
        {
            var like = new MenuFlyoutItem { Text = "Like profile" };
            like.Click += async (_, _) =>
            {
                try
                {
                    if (!_environment.Capabilities.ProfileLikes || _environment.BotPersona?.Id != item.Chat.SelfId)
                        throw new InvalidOperationException("The protocol or bot account changed.");
                    var actor = _environment.CurrentPersona ?? throw new InvalidOperationException("No sending identity is selected.");
                    var peer = item.Chat.CounterpartId(actor.Id) ?? throw new InvalidOperationException("Select a conversation participant.");
                    if (_environment.Preferences.Protocol == Asuka.Protocols.ProtocolKind.OneBotV11)
                        await _environment.Platform.SendProfileLikeAsync(peer, actor.Id, 1, 10);
                    else await _environment.Platform.SendProfileLikeAsync(peer, actor.Id);
                    ErrorInfoBar.Severity = InfoBarSeverity.Success;
                    ErrorInfoBar.Message = "Profile liked";
                    ErrorInfoBar.IsOpen = true;
                }
                catch (Exception exception) { ShowError(exception); }
            };
            flyout.Items.Add(like);
        }

        if (_environment.Capabilities.PeerPins)
        {
            var pin = new MenuFlyoutItem { Text = item.IsPinned ? "Unpin conversation" : "Pin conversation" };
            pin.Click += async (_, _) =>
            {
                try { await _environment.SetPinnedAsync(item.Chat, !item.IsPinned); }
                catch (Exception exception) { ShowError(exception); }
            };
            flyout.Items.Add(pin);
        }

        if (_environment.Capabilities.ReadReceipts)
        {
            var read = new MenuFlyoutItem { Text = "Mark as read" };
            read.Click += async (_, _) =>
            {
                try { await _environment.MarkReadAsync(item.Chat); }
                catch (Exception exception) { ShowError(exception); }
            };
            flyout.Items.Add(read);
        }

        if (_environment.Capabilities.SupportsSharedFileUploads(item.Chat.Scene))
        {
            var upload = new MenuFlyoutItem { Text = "Upload file" };
            upload.Click += async (_, _) => await UploadSharedFileAsync(item.Chat);
            flyout.Items.Add(upload);
        }

        if (_environment.Capabilities.SharedFiles && item.Chat.Scene is ChatScene.Friend or ChatScene.Group)
        {
            if (item.Chat.Scene == ChatScene.Group)
            {
                var files = new MenuFlyoutItem { Text = "Group files" };
                files.Click += async (_, _) => await ShowGroupFilesAsync(item.Chat);
                flyout.Items.Add(files);
            }
            else
            {
                var files = new MenuFlyoutItem { Text = "Shared files" };
                files.Click += async (_, _) => await ShowPrivateFilesAsync(item.Chat);
                flyout.Items.Add(files);
            }
        }
    }

    private async Task UploadSharedFileAsync(Chat chat)
    {
        if (!_environment.Capabilities.SupportsSharedFileUploads(chat.Scene) || !TryEnterDialog()) return;
        try
        {
            if (_environment.BotPersona?.Id != chat.SelfId)
                throw new InvalidOperationException("The bot account for this conversation changed.");
            var actorId = _environment.CurrentPersona?.Id ?? throw new InvalidOperationException("No sending identity is selected.");
            var protocol = _environment.Preferences.Protocol;
            var selectedChat = _environment.SelectedChat;
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFileAsync() is { } file)
            {
                if (_closed || _environment.CurrentPersona?.Id != actorId || _environment.Preferences.Protocol != protocol
                    || _environment.BotPersona?.Id != chat.SelfId || _environment.SelectedChat != selectedChat
                    || !_environment.Capabilities.SupportsSharedFileUploads(chat.Scene))
                    throw new InvalidOperationException("The conversation, sending identity or protocol changed while choosing a file.");
                var shared = await _environment.UploadSharedFileAsync(chat, file);
                if (_closed) return;
                ErrorInfoBar.Severity = InfoBarSeverity.Success;
                ErrorInfoBar.Message = $"Uploaded {shared.Name}";
                ErrorInfoBar.IsOpen = true;
            }
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }

    private async Task ShowGroupFilesAsync(Chat chat)
    {
        if (!_environment.Capabilities.SharedFiles || !TryEnterDialog()) return;
        try
        {
            var actor = _environment.CurrentPersona ?? throw new InvalidOperationException("No sending identity is selected.");
            var group = await _environment.Store.GetGroupAsync(chat.PeerId)
                ?? throw new InvalidOperationException("This group no longer exists.");
            using var dialog = new GroupFilesDialog(_environment, group, actor, this) { XamlRoot = RootGrid.XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }

    private async Task ShowPrivateFilesAsync(Chat chat)
    {
        if (!_environment.Capabilities.SharedFiles || !TryEnterDialog()) return;
        try
        {
            var actor = _environment.CurrentPersona ?? throw new InvalidOperationException("No sending identity is selected.");
            using var dialog = new PrivateFilesDialog(_environment, chat, actor, this) { XamlRoot = RootGrid.XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }

    private async Task ShowGroupContentAsync(Chat chat)
    {
        if (!_environment.Capabilities.GroupContent || !TryEnterDialog()) return;
        try
        {
            var actor = _environment.CurrentPersona ?? throw new InvalidOperationException("No sending identity is selected.");
            var group = await _environment.Store.GetGroupAsync(chat.PeerId)
                ?? throw new InvalidOperationException("This group no longer exists.");
            using var dialog = new GroupContentDialog(_environment, group, actor, this) { XamlRoot = RootGrid.XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }

    private async void EditProfile_Click(object sender, RoutedEventArgs args)
    {
        if (!_environment.Capabilities.ProfileEditing || !TryEnterDialog()) return;
        try
        {
            var user = _environment.CurrentPersona ?? throw new InvalidOperationException("No sending identity is selected.");
            var nickname = new TextBox { Header = "Display name", Text = user.Nickname };
            var bio = new TextBox { Header = "About me", Text = user.Sign, AcceptsReturn = true, MaxHeight = 150, TextWrapping = TextWrapping.Wrap };
            string? avatarPath = null;
            var avatar = new Button { Content = "Choose avatar" };
            avatar.Click += async (_, _) =>
            {
                try
                {
                    var picker = new FileOpenPicker();
                    foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" }) picker.FileTypeFilter.Add(extension);
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    if (await picker.PickSingleFileAsync() is { } selected)
                    {
                        var draft = await _environment.CreateAttachmentAsync(selected);
                        avatarPath = new Uri(_environment.Assets.LocationOf(draft.Asset.Id)).AbsoluteUri;
                        avatar.Content = selected.Name;
                    }
                }
                catch (Exception exception) { ShowError(exception); }
            };
            var content = new StackPanel { Spacing = 12, MinWidth = 320 };
            content.Children.Add(nickname);
            content.Children.Add(bio);
            content.Children.Add(avatar);
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Edit profile",
                Content = content,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (!_environment.Capabilities.ProfileEditing || _environment.CurrentPersona?.Id != user.Id)
                    throw new InvalidOperationException("The sending identity or protocol changed.");
                if (nickname.Text != user.Nickname) await _environment.Platform.SetNicknameAsync(user.Id, nickname.Text);
                if (bio.Text != user.Sign) await _environment.Platform.SetBioAsync(user.Id, bio.Text);
                if (avatarPath is not null) await _environment.Platform.SetAvatarAsync(user.Id, avatarPath);
            }
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }
}
