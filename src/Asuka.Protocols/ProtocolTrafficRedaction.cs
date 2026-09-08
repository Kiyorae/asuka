using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Asuka.Protocols;

internal static partial class ProtocolTrafficRedaction
{
    internal static TrafficEntry Redact(TrafficEntry entry)
    {
        // A malformed raw frame cannot safely be interpreted as credential fields.
        var payload = entry.Summary == "Unparseable frame" && entry.Payload is JsonValue
            ? JsonValue.Create("[unparseable frame omitted]")!
            : RedactNode(entry.Payload)!;
        return entry with { Summary = RedactText(entry.Summary), Payload = payload };
    }

    private static JsonNode? RedactNode(JsonNode? node)
    {
        if (node is JsonObject original)
        {
            var result = new JsonObject();
            foreach (var (key, value) in original)
            {
                result[key] = IsSecretKey(key)
                    ? JsonValue.Create(key.Equals("download_token", StringComparison.OrdinalIgnoreCase)
                        && value is JsonValue scalar && scalar.TryGetValue<string>(out var token)
                            ? RedactGrant(token) : "[redacted]")
                    : RedactNode(value);
            }
            return result;
        }
        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array) result.Add(RedactNode(item));
            return result;
        }
        return node is JsonValue valueNode && valueNode.TryGetValue<string>(out var text)
            ? JsonValue.Create(RedactText(text))
            : node?.DeepClone();
    }

    private static string RedactText(string text)
    {
        try
        {
            return QueryValuePattern().Replace(text, static match =>
            {
                var key = Uri.UnescapeDataString(match.Groups["key"].Value.Replace('+', ' '));
                if (!IsSecretKey(key)) return match.Value;
                var redacted = key.Equals("download_token", StringComparison.OrdinalIgnoreCase)
                    ? RedactGrant(Uri.UnescapeDataString(match.Groups["value"].Value))
                    : "[redacted]";
                return match.Groups["prefix"].Value + match.Groups["key"].Value + "=" + Uri.EscapeDataString(redacted);
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return "[diagnostic content omitted]";
        }
        catch (UriFormatException)
        {
            return "[diagnostic content omitted]";
        }
    }

    private static bool IsSecretKey(string key) =>
        key.Equals("download_token", StringComparison.OrdinalIgnoreCase)
        || key.Equals("access_token", StringComparison.OrdinalIgnoreCase)
        || key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
        || key.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase)
        || key.Equals("cookie", StringComparison.OrdinalIgnoreCase)
        || key.Equals("cookies", StringComparison.OrdinalIgnoreCase)
        || key.Equals("csrf_token", StringComparison.OrdinalIgnoreCase)
        || key.Equals("token", StringComparison.OrdinalIgnoreCase)
        || key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase);

    private static string RedactGrant(string? token)
    {
        if (token is { Length: >= 5 and <= 64 } && token.StartsWith("v1.", StringComparison.Ordinal))
        {
            var separator = token.IndexOf('.', 3);
            if (separator is >= 4 and <= 15 && token.AsSpan(3, separator - 3).IndexOfAnyExceptInRange('0', '9') < 0)
                return token[..(separator + 1)] + "[redacted]";
        }
        return "[redacted]";
    }

    [GeneratedRegex("(?<prefix>[?&])(?<key>[^=&?#\\s\"']{1,128})=(?<value>[^&#\\s\"']*)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 100)]
    private static partial Regex QueryValuePattern();
}
