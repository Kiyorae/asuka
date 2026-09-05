using Matcha.Protocols;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Matcha.App;

internal static class ActivityFilter
{
    public static bool IsFailure(TrafficEntry entry) => entry.Summary == "Unparseable frame"
        || (entry.Direction == TrafficDirection.Reply && entry.Payload is JsonObject payload
            && (string.Equals(payload["status"]?.ToString(), "failed", StringComparison.OrdinalIgnoreCase)
                || (int.TryParse(payload["retcode"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && code != 0)));

    public static IEnumerable<LogEntryItem> ProtocolEntries(
        IEnumerable<LogEntryItem> entries,
        string query,
        TrafficDirection? direction = null,
        string? protocol = null,
        bool errorsOnly = false)
    {
        var terms = SearchTerms(query);
        return entries.Where(entry => entry.Direction is not null
            && (direction is null || entry.Direction == direction)
            && (protocol is null || entry.Protocol == protocol)
            && (!errorsOnly || entry.IsError)
            && Matches(entry, terms));
    }

    public static string[] SearchTerms(string query) => query.Split(
        (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool Matches(LogEntryItem entry, IReadOnlyList<string> terms) =>
        terms.All(term => entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase));
}
