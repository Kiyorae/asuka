using Asuka.Core;
using Asuka.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Asuka.App;

public sealed partial class MainWindow
{
    private async void GroupNotifications_Click(object sender, RoutedEventArgs args)
    {
        if (!_environment.Capabilities.GroupNotifications || !TryEnterDialog()) return;
        try
        {
            using var dialog = new GroupNotificationsDialog(_environment, this) { XamlRoot = RootGrid.XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }

    private async void SimulateRequest_Click(object sender, RoutedEventArgs args)
    {
        if (!_environment.Capabilities.Requests || !TryEnterDialog()) return;
        try
        {
            using var dialog = new RequestSimulatorDialog(_environment, this) { XamlRoot = RootGrid.XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }

    private async void IgnoreRequest_Click(object sender, RoutedEventArgs args)
    {
        if (_environment.Preferences.Protocol != ProtocolKind.Milky
            || (sender as FrameworkElement)?.DataContext is not PendingRequestItem item
            || !_resolvingRequestFlags.Add(item.Request.Id)) return;
        try
        {
            if (_environment.BotPersona?.Id != item.Request.SelfId)
                throw new InvalidOperationException("The bot account for this request changed.");
            await _environment.Platform.IgnoreRequestAsync(item.Request.Flag, item.Request.SelfId, item.Request.Id);
        }
        catch (Exception exception) { ShowError(exception); }
        finally { _resolvingRequestFlags.Remove(item.Request.Id); }
    }

    private async void RequestHistory_Click(object sender, RoutedEventArgs args)
    {
        if (!_environment.Capabilities.Requests || !TryEnterDialog()) return;
        try
        {
            var selfId = _environment.BotPersona?.Id ?? throw new InvalidOperationException("No bot account is selected.");
            var records = await _environment.Store.GetRequestsAsync(selfId);
            if (_environment.BotPersona?.Id != selfId) throw new InvalidOperationException("The bot account changed.");
            var users = _environment.Personas.ToDictionary(persona => persona.Id, persona => persona.User, StringComparer.Ordinal);
            var groups = _environment.Groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
            var filter = new ComboBox { Header = "Status", ItemsSource = new[] { "All", "Pending", "Accepted", "Rejected", "Ignored" }, SelectedIndex = 0 };
            var list = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 420 };
            var pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var previous = new Button { Content = "Previous" };
            var next = new Button { Content = "Next" };
            var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            pager.Children.Add(previous);
            pager.Children.Add(pageLabel);
            pager.Children.Add(next);
            var page = 0;
            void Refresh()
            {
                var selected = records.Where(request => filter.SelectedIndex switch
                {
                    1 => request.Resolution is null,
                    2 => request.Resolution?.Status == RequestResolutionStatus.Accepted,
                    3 => request.Resolution?.Status == RequestResolutionStatus.Rejected,
                    4 => request.Resolution?.Status == RequestResolutionStatus.Ignored,
                    _ => true,
                }).OrderByDescending(request => request.Time).ThenByDescending(request => request.NotificationSequence).ToArray();
                var pages = Math.Max(1, (selected.Length + 49) / 50);
                page = Math.Clamp(page, 0, pages - 1);
                previous.IsEnabled = page > 0;
                next.IsEnabled = page + 1 < pages;
                pageLabel.Text = $"Page {page + 1} of {pages} · {selected.Length} requests";
                list.Items.Clear();
                foreach (var request in selected.Skip(page * 50).Take(50))
                {
                    var item = new PendingRequestItem(request, users.GetValueOrDefault(request.RequesterId),
                        request.GroupId is null ? null : groups.GetValueOrDefault(request.GroupId),
                        request.TargetUserId is null ? null : users.GetValueOrDefault(request.TargetUserId));
                    var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 5, 0, 5) };
                    row.Children.Add(new TextBlock { Text = item.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                    row.Children.Add(new TextBlock
                    {
                        Text = $"{request.Resolution?.Status.ToString() ?? "Pending"} · {request.Time.ToLocalTime():g}",
                        Opacity = 0.7,
                    });
                    row.Children.Add(new TextBlock { Text = item.Detail, TextWrapping = TextWrapping.Wrap });
                    if (!string.IsNullOrWhiteSpace(request.Resolution?.Reason))
                        row.Children.Add(new TextBlock { Text = request.Resolution.Reason, TextWrapping = TextWrapping.Wrap });
                    list.Items.Add(new ListViewItem { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
            }
            filter.SelectionChanged += (_, _) => { page = 0; Refresh(); };
            previous.Click += (_, _) => { page--; Refresh(); };
            next.Click += (_, _) => { page++; Refresh(); };
            var content = new StackPanel { MinWidth = 520, Spacing = 12 };
            content.Children.Add(filter);
            content.Children.Add(list);
            content.Children.Add(pager);
            Refresh();
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "Request history", Content = content, CloseButtonText = "Done" };
            dialog.Resources["ContentDialogMaxWidth"] = 620d;
            await dialog.ShowAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { ExitDialog(); }
    }
}
