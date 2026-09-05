using System.Globalization;
using Matcha.Protocols;

namespace Matcha.App;

public sealed class LogEntryItem(
    DateTimeOffset timestamp,
    string category,
    string summary,
    string detail,
    TrafficDirection? direction = null,
    string? protocol = null,
    bool isError = false)
{
    private string? _searchText;
    public DateTimeOffset Timestamp { get; } = timestamp;
    public string Category { get; } = category;
    public string Summary { get; } = summary;
    public string Detail { get; } = detail;
    public TrafficDirection? Direction { get; } = direction;
    public string? Protocol { get; } = protocol;
    public bool IsError { get; } = isError || category == "Error";
    public string TimeText { get; } = timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);
    public string Kind => Direction switch
    {
        TrafficDirection.InboundCall => "Request",
        TrafficDirection.Reply => "Response",
        TrafficDirection.OutboundEvent => "Event",
        _ => Category,
    };
    public string Header => Direction is null ? $"{TimeText}  {Category}  {Summary}" : $"{TimeText}  {Protocol}  {Kind}  {Summary}";
    public string Metadata => (Direction is null ? $"{TimeText} · {Category}" : $"{TimeText} · {Protocol} · {Kind}") + (IsError ? " · Error" : string.Empty);
    public string Glyph => IsError ? "\uE783" : Direction switch
    {
        TrafficDirection.InboundCall => "\uE72A",
        TrafficDirection.OutboundEvent => "\uE72B",
        TrafficDirection.Reply => "\uE8FB",
        _ => "\uE946",
    };
    public string SearchText => _searchText ??= $"{Header}\n{Detail}";
}
