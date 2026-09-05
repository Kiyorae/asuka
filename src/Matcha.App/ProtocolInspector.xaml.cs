using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Matcha.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Matcha.App;

public sealed partial class ProtocolInspector : UserControl, IDisposable
{
    private const int PageSize = 200;
    private readonly ObservableCollection<LogEntryItem> _entries = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refreshTimer;
    private AppEnvironment? _environment;
    private bool _active;
    private bool _disposed;
    private int _visibleLimit = PageSize;

    public ProtocolInspector()
    {
        InitializeComponent();
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(150);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += (_, _) => Refresh();
        EntryList.ItemsSource = _entries;
        CloseButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        SearchBox.TextChanged += (_, _) => FiltersChanged();
        TypeBox.SelectionChanged += (_, _) => FiltersChanged();
        ProtocolBox.SelectionChanged += (_, _) => FiltersChanged();
        ErrorsOnlyBox.Checked += (_, _) => FiltersChanged();
        ErrorsOnlyBox.Unchecked += (_, _) => FiltersChanged();
        EntryList.SelectionChanged += (_, _) => ShowDetail();
        MoreButton.Click += (_, _) => { _visibleLimit += PageSize; Refresh(); };
        CopyButton.Click += CopyPayload;
    }

    public event EventHandler? CloseRequested;

    public void Initialize(AppEnvironment environment)
    {
        if (ReferenceEquals(_environment, environment)) return;
        if (_environment is not null) _environment.RawTraffic.CollectionChanged -= TrafficChanged;
        _environment = environment;
        environment.RawTraffic.CollectionChanged += TrafficChanged;
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active) Refresh();
        else _refreshTimer.Stop();
    }

    public void Dispose()
    {
        _disposed = true;
        _refreshTimer.Stop();
        if (_environment is not null) _environment.RawTraffic.CollectionChanged -= TrafficChanged;
        _environment = null;
    }

    private void TrafficChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_active && !_disposed && !_refreshTimer.IsRunning) _refreshTimer.Start();
    }

    private void FiltersChanged()
    {
        _visibleLimit = PageSize;
        if (_active && !_refreshTimer.IsRunning) _refreshTimer.Start();
    }

    private void Refresh()
    {
        if (!_active || _disposed || _environment is null) return;
        TrafficDirection? direction = TypeBox.SelectedIndex switch
        {
            1 => TrafficDirection.InboundCall,
            2 => TrafficDirection.Reply,
            3 => TrafficDirection.OutboundEvent,
            _ => null,
        };
        var protocol = ProtocolBox.SelectedIndex > 0 ? ProtocolBox.SelectedItem as string : null;
        var filtered = ActivityFilter.ProtocolEntries(_environment.RawTraffic, SearchBox.Text, direction,
            protocol, ErrorsOnlyBox.IsChecked == true).OrderByDescending(entry => entry.Timestamp).ToList();
        var selected = EntryList.SelectedItem as LogEntryItem;
        // Keep an inspected entry present even when newer traffic pushes it beyond the current page.
        var selectedIndex = selected is null ? -1 : filtered.IndexOf(selected);
        var visible = filtered.Take(Math.Max(_visibleLimit, selectedIndex + 1)).ToList();
        CollectionSync.Apply(_entries, visible);
        EntryList.SelectedItem = selected is not null && visible.Contains(selected) ? selected : visible.FirstOrDefault();
        ShowDetail();
        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var hasTraffic = _environment.RawTraffic.Count > 0;
        EmptyTitle.Text = hasTraffic ? "No matching entries" : "No protocol traffic yet";
        EmptyDescription.Text = hasTraffic ? "Try another search or relax the filters." : "Connect a bot to inspect its requests and events.";
        CountText.Text = $"{visible.Count} shown · {filtered.Count} matching · {_environment.RawTraffic.Count} total";
        MoreButton.Visibility = visible.Count < filtered.Count ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowDetail()
    {
        var entry = EntryList.SelectedItem as LogEntryItem;
        DetailTitle.Text = entry?.Summary ?? "Payload";
        DetailMetadata.Text = entry?.Metadata ?? "Select an entry to inspect its JSON";
        // Avoid resetting text selection and the JSON scroll position on every incoming event.
        if (DetailBox.Text != (entry?.Detail ?? string.Empty)) DetailBox.Text = entry?.Detail ?? string.Empty;
        CopyButton.IsEnabled = entry is not null;
    }

    private void CopyPayload(object sender, RoutedEventArgs args)
    {
        if (EntryList.SelectedItem is not LogEntryItem entry) return;
        try
        {
            var package = new DataPackage();
            package.SetText(entry.Detail);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception exception) { _environment?.ReportError("Protocol payload copy failed", exception); }
    }
}
