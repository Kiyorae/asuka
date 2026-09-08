using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Asuka.App;

internal static class MessageTextRenderer
{
    internal static UIElement Render(string text, Brush? foreground = null)
    {
        var blocks = MessageTextBlocks.Parse(text);
        if (blocks.Count == 1 && !blocks[0].IsCode) return PlainText(blocks[0].Text, foreground);
        var content = new StackPanel { Spacing = 6 };
        foreach (var block in blocks) content.Children.Add(block.IsCode ? CodeBlock(block, foreground) : PlainText(block.Text, foreground));
        return content;
    }

    private static TextBlock PlainText(string text, Brush? foreground)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        if (foreground is not null) block.Foreground = foreground;
        return block;
    }

    private static Border CodeBlock(MessageTextBlock block, Brush? foreground)
    {
        var code = new TextBlock
        {
            Text = block.Text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
        };
        if (foreground is not null) code.Foreground = foreground;
        AutomationProperties.SetName(code, block.Language is null ? "Code block" : $"{block.Language} code block");
        var scroller = new ScrollViewer
        {
            Content = code,
            MaxHeight = 320,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = true,
        };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = block.Language ?? "Code",
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = foreground is null ? 0.72 : 1,
        });
        var copy = new Button { Content = "Copy code", IsEnabled = block.Text.Length > 0, Padding = new Thickness(8, 3, 8, 3) };
        AutomationProperties.SetName(copy, "Copy code");
        copy.Click += (_, _) =>
        {
            try
            {
                var package = new DataPackage();
                package.SetText(block.Text);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                copy.Content = "Copied";
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                copy.Content = "Copy failed";
            }
        };
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(header);
        body.Children.Add(scroller);
        return new Border
        {
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128)),
            Child = body,
        };
    }
}
