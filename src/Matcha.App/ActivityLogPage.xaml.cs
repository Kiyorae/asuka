using System.Collections.Specialized;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Matcha.App;

/// <summary>
/// In-window activity console that combines application logs and raw protocol traffic.
/// </summary>
public sealed partial class ActivityLogPage : UserControl, IDisposable
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refreshTimer;
    private List<LogEntryItem> _filtered = [];
    private readonly ObservableCollection<LogEntryItem> _visible = [];
    private AppEnvironment? _environment;
    private Window? _owner;
    private bool _disposed;
    private bool _initialized;

    public ActivityLogPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _visible;
        _refreshTimer = RootGrid.DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(150);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += (_, _) => RefreshFilter();
        WireUiEvents();
        InstallKeyboardAccelerators();
    }

    /// <summary>Attaches the page to its application environment. Repeated calls are safe.</summary>
    public void Initialize(AppEnvironment environment, Window owner)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(owner);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized && ReferenceEquals(_environment, environment) && ReferenceEquals(_owner, owner))
        {
            Refresh();
            return;
        }

        DetachEnvironment();
        _environment = environment;
        _owner = owner;
        _initialized = true;
        RootGrid.RequestedTheme = WindowChrome.ToElementTheme(environment.Preferences.Theme);
        environment.Logs.CollectionChanged += Logs_CollectionChanged;
        environment.RawTraffic.CollectionChanged += RawTraffic_CollectionChanged;
        environment.PreferencesChanged += Environment_PreferencesChanged;
        Refresh();
    }

    /// <summary>Immediately refreshes the merged, filtered activity list.</summary>
    public void Refresh()
    {
        if (_initialized && !_disposed)
        {
            RefreshFilter();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        DetachEnvironment();
    }

    private void WireUiEvents()
    {
        CopySelectedButton.Click += CopySelected_Click;
        CopyFilteredButton.Click += CopyFiltered_Click;
        ExportButton.Click += Export_Click;
        ClearButton.Click += Clear_Click;
        SearchBox.TextChanged += SearchBox_TextChanged;
        CategoryBox.SelectionChanged += CategoryBox_SelectionChanged;
        LogList.SelectionChanged += LogList_SelectionChanged;
    }

    private void DetachEnvironment()
    {
        if (_environment is not null)
        {
            _environment.Logs.CollectionChanged -= Logs_CollectionChanged;
            _environment.RawTraffic.CollectionChanged -= RawTraffic_CollectionChanged;
            _environment.PreferencesChanged -= Environment_PreferencesChanged;
        }

        _environment = null;
        _owner = null;
        _initialized = false;
    }

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRefresh();

    private void RawTraffic_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        if (!_disposed && _initialized && Visibility == Visibility.Visible && !_refreshTimer.IsRunning)
        {
            _refreshTimer.Start();
        }
    }

    private void Environment_PreferencesChanged(object? sender, EventArgs e)
    {
        if (_environment is not null)
        {
            RootGrid.RequestedTheme = WindowChrome.ToElementTheme(_environment.Preferences.Theme);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private void RefreshFilter()
    {
        var environment = _environment;
        if (environment is null || SearchBox is null || CategoryBox is null)
        {
            return;
        }

        var allEntries = GetActivityEntries(environment);
        var protocolEntries = environment.RawTraffic.ToHashSet();
        var activeEntry = LogList.SelectedItem as LogEntryItem;
        var selectedEntries = LogList.SelectedItems.OfType<LogEntryItem>().ToArray();
        var query = SearchBox.Text.Trim();
        var category = CategoryBox.SelectedIndex;
        _filtered = allEntries.Where(entry =>
            (query.Length == 0 || entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            && (category == 0
                || (category == 1 && !protocolEntries.Contains(entry))
                || (category == 2 && protocolEntries.Contains(entry))
                || (category == 3 && entry.Category == "Connection")
                || (category == 4 && entry.Category == "Error")))
            .ToList();
        CollectionSync.Apply(_visible, _filtered);

        var retainedEntries = selectedEntries.Where(_filtered.Contains).ToArray();
        foreach (var entry in retainedEntries.Where(entry => !ReferenceEquals(entry, activeEntry)))
        {
            if (!LogList.SelectedItems.Contains(entry)) LogList.SelectedItems.Add(entry);
        }

        if (activeEntry is not null && retainedEntries.Contains(activeEntry))
        {
            if (!LogList.SelectedItems.Contains(activeEntry)) LogList.SelectedItems.Add(activeEntry);
        }
        else if (retainedEntries.Length == 0)
        {
            DetailHeader.Text = "Select a log entry";
            DetailBox.Text = string.Empty;
        }

        var protocolCount = allEntries.Count(protocolEntries.Contains);
        StatusText.Text = $"{_filtered.Count} shown · {allEntries.Count} total ({allEntries.Count - protocolCount} app · {protocolCount} protocol)";
    }

    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogList.SelectedItem is LogEntryItem selected)
        {
            DetailHeader.Text = selected.Header;
            DetailBox.Text = selected.Detail;
        }
        else
        {
            DetailHeader.Text = "Select a log entry";
            DetailBox.Text = string.Empty;
        }
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = LogList.SelectedItems.OfType<LogEntryItem>().ToArray();
        if (selected.Length > 0)
        {
            CopyText(FormatEntries(selected));
        }
    }

    private void CopyFiltered_Click(object sender, RoutedEventArgs e) => CopyText(FormatEntries(_filtered));

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var environment = _environment;
        var owner = _owner;
        if (environment is null || owner is null)
        {
            return;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"matcha-log-{DateTimeOffset.Now:yyyyMMdd-HHmmss}",
            };
            picker.FileTypeChoices.Add("Text log", [".log"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
            var file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                await Windows.Storage.FileIO.WriteTextAsync(file, FormatEntries(GetActivityEntries(environment)));
                StatusText.Text = $"Exported {file.Path}";
            }
        }
        catch (Exception exception)
        {
            ErrorInfoBar.Title = "Export failed";
            ErrorInfoBar.Message = exception.Message;
            ErrorInfoBar.IsOpen = true;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_environment is null)
        {
            return;
        }

        _environment.ClearLogs();
        _environment.RawTraffic.Clear();
        DetailHeader.Text = "Select a log entry";
        DetailBox.Text = string.Empty;
    }

    private void InstallKeyboardAccelerators()
    {
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, (_, _) => SearchBox.Focus(FocusState.Keyboard));
        AddAccelerator(VirtualKey.C, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, (_, _) => CopySelected_Click(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.C, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (_, _) => CopyFiltered_Click(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.E, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (_, _) => Export_Click(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.Delete, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (_, _) => Clear_Click(this, new RoutedEventArgs()));
    }

    private void AddAccelerator(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        TypedEventHandler<Microsoft.UI.Xaml.Input.KeyboardAccelerator, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += handler;
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private static string FormatEntries(IEnumerable<LogEntryItem> entries) => string.Join(
        System.Environment.NewLine + System.Environment.NewLine,
        entries.Select(entry => $"[{entry.Header}]{System.Environment.NewLine}{entry.Detail}"));

    private static List<LogEntryItem> GetActivityEntries(AppEnvironment environment) => environment.Logs
        .Concat(environment.RawTraffic)
        .OrderByDescending(entry => entry.Timestamp)
        .ToList();

    private void CopyText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception exception)
        {
            ErrorInfoBar.Title = "Clipboard operation failed";
            ErrorInfoBar.Message = exception.Message;
            ErrorInfoBar.IsOpen = true;
            _environment?.ReportError("Log copy failed", exception);
        }
    }
}
