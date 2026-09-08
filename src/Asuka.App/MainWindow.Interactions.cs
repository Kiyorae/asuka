using Asuka.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Asuka.App;

public sealed partial class MainWindow
{
    private void ReplyBanner_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason != InfoBarCloseReason.CloseButton) return;
        _reply = null;
        SaveActiveDraft();
    }

    private void AddMessageInteractions(MenuFlyout flyout, MessageItem item)
    {
        if (item.Message.IsRecalled)
        {
            return;
        }

        var reply = new MenuFlyoutItem { Text = "Reply", IsEnabled = _environment.CanCompose };
        flyout.Opening += (_, _) => reply.IsEnabled = _environment.CanCompose && item.Message.Chat == _environment.SelectedChat
            && (item.Message.Anonymous is null || _environment.Capabilities.SupportsAnonymous(item.Message.Scene));
        reply.Click += (_, _) =>
        {
            if (item.Message.Chat != _environment.SelectedChat
                || (item.Message.Anonymous is not null && !_environment.Capabilities.SupportsAnonymous(item.Message.Scene))) return;
            _reply = new ReplyDraft(item.Id, item.DisplaySenderId, item.Sender, item.Text);
            SaveActiveDraft();
            UpdateChatState();
            ComposerTextBox.Focus(FocusState.Programmatic);
        };
        flyout.Items.Add(reply);
        if (item.Message.Anonymous is null && _environment.Capabilities.GroupContent && item.Message.Scene == ChatScene.Group)
        {
            var essence = new MenuFlyoutItem { Text = "Mark as essence", IsEnabled = false };
            var isEssence = false;
            flyout.Opening += async (_, _) =>
            {
                try
                {
                    var actorId = _environment.CurrentPersona?.Id;
                    var actor = actorId is null ? null : await _environment.Store.GetMemberAsync(item.Message.PeerId, actorId);
                    isEssence = await _environment.Store.IsGroupEssenceMessageAsync(item.Id);
                    essence.Text = isEssence ? "Remove from essence" : "Mark as essence";
                    essence.IsEnabled = actor?.Role > GroupRole.Member && _environment.Capabilities.GroupContent;
                }
                catch (Exception exception) { ShowError(exception); }
            };
            essence.Click += async (_, _) =>
            {
                try
                {
                    var actorId = _environment.CurrentPersona?.Id ?? throw new InvalidOperationException("No sending identity is selected.");
                    await _environment.Platform.SetGroupEssenceMessageAsync(item.Message.PeerId, item.Message.Seq,
                        item.Message.SelfId, actorId, !isEssence);
                }
                catch (Exception exception) { ShowError(exception); }
            };
            flyout.Items.Add(essence);
        }
        if (_environment.Capabilities.ForwardedMessages)
        {
            var forward = new MenuFlyoutItem { Text = "Forward to…" };
            flyout.Opening += (_, _) => forward.IsEnabled = _environment.CanCompose;
            forward.Click += async (_, _) => await ShowForwardDialogAsync(item);
            flyout.Items.Add(forward);
        }
        if (item.Message.Anonymous is null && _environment.Capabilities.SupportsReactions(item.Message.Scene))
        {
            var react = new MenuFlyoutItem { Text = "Add reaction" };
            react.Click += async (_, _) => await ShowReactionPickerAsync(item);
            flyout.Items.Add(react);
        }

        AddAnonymousModeration(flyout, item);
        if (item.Message.Anonymous is null && _environment.Capabilities.SupportsNudges(item.Message.Scene)
            && (item.Message.Scene == ChatScene.Group || item.Message.SenderId != _environment.CurrentPersona?.Id))
        {
            var nudge = new MenuFlyoutItem { Text = $"Nudge {item.Sender}" };
            flyout.Opening += (_, _) => nudge.IsEnabled = item.Message.Chat == _environment.SelectedChat
                && _environment.BotPersona?.Id == item.Message.SelfId
                && _environment.Capabilities.SupportsNudges(item.Message.Scene);
            nudge.Click += async (_, _) =>
            {
                try
                {
                    var chat = item.Message.Chat;
                    if (chat != _environment.SelectedChat || !_environment.Capabilities.SupportsNudges(chat.Scene))
                    {
                        return;
                    }

                    await _environment.NudgeAsync(chat, item.Message.SenderId);
                    if (_closed) return;
                    ErrorInfoBar.Severity = InfoBarSeverity.Success;
                    ErrorInfoBar.Message = $"Nudged {item.Sender}";
                    ErrorInfoBar.IsOpen = true;
                }
                catch (Exception exception) { ShowError(exception); }
            };
            flyout.Items.Add(nudge);
        }
    }

    private Button CreateReplyPreview(ReplySegment reply, Brush? foreground = null)
    {
        var target = _environment.Messages.FirstOrDefault(item => item.Id == reply.MessageId);
        var heading = new TextBlock
        {
            Text = target is null ? "Reply" : $"Replying to {target.Sender}",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = new FontIcon { Glyph = "\uE97A", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(icon);
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        var preview = new TextBlock
        {
            Text = target?.Text ?? "Original message is not loaded.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            FontStyle = target is null || target.Message.IsRecalled ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
        };
        var content = new StackPanel { Spacing = 4, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(header);
        content.Children.Add(preview);
        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 8, 10, 8),
            Child = content,
        };
        var button = new Button
        {
            Content = card,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 5),
            CornerRadius = new CornerRadius(6),
            IsEnabled = target is not null,
        };
        void UpdateColors()
        {
            var dark = button.ActualTheme == ElementTheme.Dark;
            var outgoing = foreground is not null;
            card.Background = new SolidColorBrush(outgoing
                ? Windows.UI.Color.FromArgb(255, 17, 77, 170)
                : dark ? Windows.UI.Color.FromArgb(255, 29, 33, 40) : Windows.UI.Color.FromArgb(255, 246, 247, 249));
            card.BorderBrush = new SolidColorBrush(outgoing || dark
                ? Windows.UI.Color.FromArgb(255, 177, 211, 255) : Windows.UI.Color.FromArgb(255, 55, 112, 191));
            heading.Foreground = foreground ?? new SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(255, 185, 216, 255) : Windows.UI.Color.FromArgb(255, 37, 77, 133));
            icon.Foreground = heading.Foreground;
            preview.Foreground = new SolidColorBrush(outgoing
                ? Windows.UI.Color.FromArgb(255, 224, 236, 255)
                : dark ? Windows.UI.Color.FromArgb(255, 220, 225, 232) : Windows.UI.Color.FromArgb(255, 65, 72, 82));
        }
        button.ActualThemeChanged += (_, _) => UpdateColors();
        UpdateColors();
        ToolTipService.SetToolTip(button, target is null ? "The original message is outside the loaded history." : "Jump to the original message");
        AutomationProperties.SetName(button, $"{heading.Text}. {preview.Text}");
        button.Click += (_, _) =>
        {
            var container = MessageList.Items.OfType<ListViewItem>()
                .FirstOrDefault(candidate => candidate.Tag is MessageItem item && item.Id == reply.MessageId);
            if (container is not null)
            {
                MessageList.SelectedItem = container;
                MessageList.ScrollIntoView(container);
            }
        };
        return button;
    }

    private VariableSizedWrapGrid CreateReactionBar(MessageItem item)
    {
        var bar = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            MaximumRowsOrColumns = 7,
            ItemWidth = 88,
            ItemHeight = 36,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var group in item.Reactions.GroupBy(reaction => (reaction.ReactionType, reaction.Reaction)))
        {
            var selected = group.Any(reaction => reaction.UserId == _environment.CurrentPersona?.Id);
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = $"{ReactionLabel(group.Key.Reaction, group.Key.ReactionType)} {group.Count()}",
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 66,
                },
                Padding = new Thickness(8, 3, 8, 3),
                BorderThickness = new Thickness(selected ? 2 : 1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 0, 4, 4),
            };
            var names = group.Select(reaction => _environment.Personas.FirstOrDefault(persona => persona.Id == reaction.UserId)?.DisplayName ?? reaction.UserId);
            ToolTipService.SetToolTip(button, string.Join(", ", names));
            AutomationProperties.SetName(button, $"{(selected ? "Remove" : "Add")} reaction {group.Key.Reaction}, {group.Count()} people");
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await _environment.ReactAsync(item.Id, group.Key.Reaction, group.Key.ReactionType, !selected); }
                catch (Exception exception) { ShowError(exception); }
                finally { button.IsEnabled = true; }
            };
            bar.Children.Add(button);
        }

        return bar;
    }

    private async Task ShowReactionPickerAsync(MessageItem item)
    {
        if (!TryEnterDialog()) return;
        try
        {
            var type = new ComboBox { Header = "Reaction type", ItemsSource = new[] { "QQ face", "Unicode emoji" }, SelectedIndex = 0 };
            var value = new TextBox { Header = "Reaction ID", Text = "76", PlaceholderText = "QQ face ID or Unicode code point" };
            var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var (id, label) in new[] { ("76", "👍"), ("14", "🙂"), ("66", "❤"), ("4", "😎"), ("5", "😭") })
            {
                var button = new Button { Content = label };
                button.Click += (_, _) => { type.SelectedIndex = 0; value.Text = id; };
                choices.Children.Add(button);
            }

            var panel = new StackPanel { Spacing = 12, MinWidth = 300 };
            panel.Children.Add(choices);
            panel.Children.Add(type);
            panel.Children.Add(value);
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Add reaction",
                Content = panel,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var reaction = value.Text.Trim();
                if (reaction.Length == 0) throw new InvalidOperationException("Enter a reaction ID.");
                await _environment.ReactAsync(item.Id, reaction, type.SelectedIndex == 0 ? "face" : "emoji", true);
            }
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }

    private static string ReactionLabel(string reaction, string type)
    {
        if (type == "face")
        {
            return reaction switch { "76" => "👍", "14" => "🙂", "66" => "❤", "4" => "😎", "5" => "😭", _ => $"Face {reaction}" };
        }

        return int.TryParse(reaction, out var codePoint) && System.Text.Rune.IsValid(codePoint)
            ? new System.Text.Rune(codePoint).ToString() : reaction;
    }

}
