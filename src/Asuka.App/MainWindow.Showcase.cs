using Asuka.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Asuka.App;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _showcaseTimer;
    private bool _showcaseTickRunning;
    private bool _showcasePlaying;
    private double _showcaseScrollHeight = -1;
    private string? _showcaseScrollChatId;

    private void StartShowcasePlayback()
    {
        if (!_environment.IsShowcaseMode || _closed) return;
        if (_showcaseTimer is null)
        {
            _showcaseTimer = RootGrid.DispatcherQueue.CreateTimer();
            _showcaseTimer.Interval = _environment.ShowcaseInterval;
            _showcaseTimer.IsRepeating = true;
            _showcaseTimer.Tick += ShowcaseTimer_Tick;
            MessageList.LayoutUpdated += ShowcaseLayoutUpdated;
        }
        if (_showcasePlaying) return;
        _showcasePlaying = true;
        _showcaseTimer.Start();
        UpdateShowcaseControls();
    }

    private void PauseShowcasePlayback()
    {
        _showcasePlaying = false;
        _showcaseTimer?.Stop();
        UpdateShowcaseControls();
    }

    private void ToggleShowcasePlayback()
    {
        if (_showcasePlaying) PauseShowcasePlayback();
        else StartShowcasePlayback();
    }

    private async void ShowcaseTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_closed || !_showcasePlaying || _showcaseTickRunning) return;
        _showcaseTickRunning = true;
        try
        {
            var step = await _environment.AdvanceShowcaseAsync();
            if (!_closed && _showcasePlaying && step?.HistoryCleared == true)
                ShowcaseStatusText.Text = "Demo · a new loop of messages";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception)
        {
            PauseShowcasePlayback();
            if (!_closed) ShowError(exception);
        }
        finally { _showcaseTickRunning = false; }
    }

    private void UpdateShowcaseControls()
    {
        if (!_environment.IsShowcaseMode || ShowcaseControls is null) return;
        ShowcaseControls.Visibility = Visibility.Visible;
        ShowcasePauseButton.Content = _showcasePlaying ? "Pause" : "Resume";
        ShowcaseStatusText.Text = $"Demo · {(_showcasePlaying ? "playing" : "paused")} · {_environment.ShowcaseInterval.TotalSeconds:0.#} s / message";
        ConnectionActionIcon.Glyph = _showcasePlaying ? "\uE769" : "\uE768";
        ConnectionStatusLabel.Text = _showcasePlaying ? "Demo · playing locally" : "Demo · paused";
        ToolTipService.SetToolTip(ConnectButton, _showcasePlaying ? "Pause demo" : "Resume demo");
        ToolTipService.SetToolTip(ConnectionDetailsPanel, "Local demo · no protocol network connection");
        AutomationProperties.SetName(ConnectButton, _showcasePlaying ? "Pause demo" : "Resume demo");
    }

    private void ShowcasePause_Click(object sender, RoutedEventArgs args) => ToggleShowcasePlayback();
    private async void ShowcaseGroup_Click(object sender, RoutedEventArgs args) => await SelectShowcaseChatAsync(true);
    private async void ShowcasePrivate_Click(object sender, RoutedEventArgs args) => await SelectShowcaseChatAsync(false);

    private async Task SelectShowcaseChatAsync(bool group)
    {
        try { await _environment.SelectShowcaseChatAsync(group); }
        catch (Exception exception) { if (!_closed) ShowError(exception); }
    }

    private void ShowcaseLayoutUpdated(object? sender, object args)
    {
        if (!_environment.IsShowcaseMode || _closed || ShowcaseFollowCheckBox.IsChecked != true
            || _navigation.CurrentPage != "messages") return;
        var scroll = FindMessageScrollViewer(MessageList);
        if (scroll is not null && (_showcaseScrollChatId != _environment.SelectedChat?.Id
            || Math.Abs(_showcaseScrollHeight - scroll.ScrollableHeight) > 0.5))
            FollowShowcaseMessages(scroll);
    }

    private void FollowShowcaseMessages(ScrollViewer? scroll = null)
    {
        if (_closed || ShowcaseFollowCheckBox.IsChecked != true || MessageList.Items.Count == 0) return;
        scroll ??= FindMessageScrollViewer(MessageList);
        if (scroll is null) return;
        _showcaseScrollChatId = _environment.SelectedChat?.Id;
        _showcaseScrollHeight = scroll.ScrollableHeight;
        _ = scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: false);
    }

    private static ScrollViewer? FindMessageScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindMessageScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        return null;
    }

    private void DisposeShowcasePlayback()
    {
        PauseShowcasePlayback();
        if (_showcaseTimer is not null) _showcaseTimer.Tick -= ShowcaseTimer_Tick;
        MessageList.LayoutUpdated -= ShowcaseLayoutUpdated;
    }
}
