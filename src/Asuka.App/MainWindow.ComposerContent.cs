using Asuka.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Asuka.App;

public sealed partial class MainWindow
{
    private void RemoveRichContent_Click(object sender, RoutedEventArgs args)
    {
        var selected = RichContentList.SelectedItem as MessageSegment ?? _richContent.LastOrDefault();
        if (selected is not null) _richContent.Remove(selected);
        SaveActiveDraft();
        UpdateChatState();
    }

    private async void FaceButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_environment.Capabilities.Faces || !TryEnterDialog()) return;
        var context = _activeDraftKey;
        var chatId = _environment.SelectedChat?.Id;
        try
        {
            var choices = new[] { new FaceChoice("14", "Smile"), new FaceChoice("76", "Thumbs up"),
                new FaceChoice("66", "Heart"), new FaceChoice("4", "Cool"), new FaceChoice("5", "Tears") };
            var choice = new ComboBox { Header = "QQ emoji", ItemsSource = choices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var custom = new TextBox { Header = "Custom QQ face ID", PlaceholderText = "Optional" };
            var large = new CheckBox { Content = "Large emoji", Visibility = _environment.Preferences.Protocol == Asuka.Protocols.ProtocolKind.Milky ? Visibility.Visible : Visibility.Collapsed };
            var panel = new StackPanel { Spacing = 12, MinWidth = 300 };
            panel.Children.Add(choice);
            panel.Children.Add(new Expander { Header = "Custom emoji", Content = custom });
            panel.Children.Add(large);
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Add QQ emoji",
                Content = panel,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && context is not null && chatId is not null)
            {
                EnsureDraftContextIsCurrent(context, chatId);
                var selected = (FaceChoice)choice.SelectedItem;
                var id = string.IsNullOrWhiteSpace(custom.Text) ? selected.Id : custom.Text.Trim();
                if (id.Length > 32 || !id.All(char.IsAsciiDigit)) throw new InvalidOperationException("QQ face IDs contain digits only.");
                _richContent.Add(new FaceSegment(id, id == selected.Id ? selected.Name : null, large.IsChecked == true));
                SaveActiveDraft();
                UpdateChatState();
            }
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }

    private async void LocationButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_environment.Capabilities.Locations || !TryEnterDialog()) return;
        var context = _activeDraftKey;
        var chatId = _environment.SelectedChat?.Id;
        try
        {
            var title = new TextBox { Header = "Place name" };
            var address = new TextBox { Header = "Address or description" };
            var latitude = new NumberBox { Header = "Latitude", Minimum = -90, Maximum = 90, Value = 0 };
            var longitude = new NumberBox { Header = "Longitude", Minimum = -180, Maximum = 180, Value = 0 };
            var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
            foreach (var control in new UIElement[] { title, address, latitude, longitude }) panel.Children.Add(control);
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Add location",
                Content = panel,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && context is not null && chatId is not null)
            {
                EnsureDraftContextIsCurrent(context, chatId);
                if (!double.IsFinite(latitude.Value) || !double.IsFinite(longitude.Value)
                    || Math.Abs(latitude.Value) > 90 || Math.Abs(longitude.Value) > 180)
                    throw new InvalidOperationException("Enter valid latitude and longitude values.");
                _richContent.Add(new LocationSegment(latitude.Value, longitude.Value, title.Text.Trim(), address.Text.Trim()));
                SaveActiveDraft();
                UpdateChatState();
            }
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }

    private async void AudioFileButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_environment.Capabilities.Audio || !TryEnterDialog()) return;
        var context = _activeDraftKey;
        var chatId = _environment.SelectedChat?.Id;
        try
        {
            var picker = new FileOpenPicker();
            foreach (var extension in new[] { ".mp3", ".wav", ".m4a", ".ogg", ".flac" }) picker.FileTypeFilter.Add(extension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFileAsync() is { } file && context is not null && chatId is not null)
            {
                EnsureDraftContextIsCurrent(context, chatId);
                BeginAttachmentImport(context);
                try
                {
                    var draft = await _environment.CreateAttachmentAsync(file);
                    EnsureDraftContextIsCurrent(context, chatId);
                    _richContent.Add(new AudioSegment(draft.Asset));
                    SaveActiveDraft();
                }
                finally { EndAttachmentImport(context); }
            }
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }

    private async Task ShowForwardDialogAsync(MessageItem item)
    {
        if (!_environment.Capabilities.ForwardedMessages || !TryEnterDialog()) return;
        var actorId = _environment.CurrentPersona?.Id;
        var protocol = _environment.Preferences.Protocol;
        try
        {
            var targets = new ComboBox
            {
                Header = "Send to",
                ItemsSource = _environment.Conversations.ToArray(),
                DisplayMemberPath = nameof(ConversationItem.Title),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedItem = CurrentConversation(),
            };
            var panel = new StackPanel { MinWidth = 340, Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = $"{item.Sender}: {item.Text}", TextWrapping = TextWrapping.Wrap, MaxHeight = 120 });
            panel.Children.Add(targets);
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Forward message",
                Content = panel,
                PrimaryButtonText = "Forward",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (_environment.CurrentPersona?.Id != actorId || _environment.Preferences.Protocol != protocol)
                    throw new InvalidOperationException("The sending identity or protocol changed.");
                if (targets.SelectedItem is not ConversationItem destination) throw new InvalidOperationException("Choose a destination.");
                await _environment.ForwardMessageAsync(item.Id, destination.Chat);
            }
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }

    private sealed record FaceChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
