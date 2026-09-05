using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Matcha.Protocols;

internal static class JsonExtensions
{
    private const double MaxSafeJsonInteger = 9_007_199_254_740_992d;
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    internal static string? GetFlexibleString(this JsonObject value, string key)
    {
        var node = value[key];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (scalar.TryGetValue<long>(out var integer))
            {
                return integer.ToString(CultureInfo.InvariantCulture);
            }

            if (scalar.TryGetValue<double>(out var number))
            {
                return number.ToString("0.################", CultureInfo.InvariantCulture);
            }

            if (scalar.TryGetValue<bool>(out var boolean))
            {
                return boolean ? "true" : "false";
            }
        }

        return null;
    }

    internal static long? GetFlexibleInt64(this JsonObject value, string key)
    {
        var node = value[key];
        if (node is not JsonValue scalar)
        {
            return null;
        }

        if (scalar.TryGetValue<long>(out var integer))
        {
            return Math.Abs((double)integer) <= MaxSafeJsonInteger ? integer : null;
        }

        if (scalar.TryGetValue<double>(out var number)
            && number is >= -MaxSafeJsonInteger and <= MaxSafeJsonInteger
            && Math.Truncate(number) == number)
        {
            return checked((long)number);
        }


        if (scalar.TryGetValue<bool>(out var boolean))
        {
            return boolean ? 1 : 0;
        }

        return scalar.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)
            ? integer
            : null;
    }

    internal static bool? GetFlexibleBoolean(this JsonObject value, string key)
    {
        var node = value[key];
        if (node is not JsonValue scalar)
        {
            return null;
        }

        if (scalar.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }

        if (scalar.TryGetValue<long>(out var integer))
        {
            return integer != 0;
        }

        if (scalar.TryGetValue<string>(out var text))
        {
            return text.Equals("true", StringComparison.OrdinalIgnoreCase)
                || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || text == "1"
                ? true
                : text.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("no", StringComparison.OrdinalIgnoreCase)
                    || text == "0"
                    ? false
                    : null;
        }

        return null;
    }

    internal static JsonObject ParseObject(string json)
    {
        return JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("Expected a JSON object.");
    }

    internal static string ToCompactJson(this JsonNode value) => value.ToJsonString(CompactOptions);

    internal static JsonNode NumericId(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? JsonValue.Create(number)
            : JsonValue.Create(value);
    }

    internal static long UnixSeconds(this DateTimeOffset value) => value.ToUnixTimeSeconds();
}
