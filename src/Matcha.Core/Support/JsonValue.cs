using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Matcha.Core;

[JsonConverter(typeof(JsonValueConverter))]
public readonly struct JsonValue : IEquatable<JsonValue>
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };
    private readonly JsonElement _element;

    public JsonValue(JsonElement element) => _element = element.Clone();

    public static JsonValue Null { get; } = Parse("null");
    public JsonValueKind Kind => _element.ValueKind == JsonValueKind.Undefined ? JsonValueKind.Null : _element.ValueKind;
    public bool IsNull => Kind is JsonValueKind.Null or JsonValueKind.Undefined;
    public bool? BoolValue => Kind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => _element.TryGetDouble(out var number) ? number != 0 : null,
        JsonValueKind.String => ParseLooseBoolean(_element.GetString()),
        _ => null,
    };

    public double? DoubleValue => Kind switch
    {
        JsonValueKind.Number when _element.TryGetDouble(out var number) => number,
        JsonValueKind.String when double.TryParse(_element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    public long? Int64Value => DoubleValue is { } number && double.IsFinite(number)
        && number is >= -9_007_199_254_740_992d and <= 9_007_199_254_740_992d
        ? checked((long)number)
        : null;

    public string? StringValue => Kind switch
    {
        JsonValueKind.String => _element.GetString(),
        JsonValueKind.Number when Int64Value is { } integer && integer == DoubleValue => integer.ToString(CultureInfo.InvariantCulture),
        JsonValueKind.Number => DoubleValue?.ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    public JsonValue? this[string key] => Kind == JsonValueKind.Object && _element.TryGetProperty(key, out var value)
        ? new JsonValue(value)
        : (JsonValue?)null;

    public JsonValue? this[int index]
    {
        get
        {
            if (Kind != JsonValueKind.Array || index < 0 || index >= _element.GetArrayLength())
            {
                return null;
            }

            return new JsonValue(_element[index]);
        }
    }

    public static JsonValue Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new JsonValue(document.RootElement);
    }

    public static JsonValue From<T>(T value) => Parse(JsonSerializer.Serialize(value));
    public string ToJsonString(bool indented = false)
    {
        var element = _element.ValueKind == JsonValueKind.Undefined ? Null._element : _element;
        return indented
            ? JsonSerializer.Serialize(element, IndentedOptions)
            : JsonSerializer.Serialize(element);
    }
    public override string ToString() => ToJsonString();
    public void WriteTo(Utf8JsonWriter writer) => (_element.ValueKind == JsonValueKind.Undefined ? Null._element : _element).WriteTo(writer);

    public bool Equals(JsonValue other) => JsonElement.DeepEquals(
        _element.ValueKind == JsonValueKind.Undefined ? Null._element : _element,
        other._element.ValueKind == JsonValueKind.Undefined ? Null._element : other._element);

    public override bool Equals(object? obj) => obj is JsonValue other && Equals(other);
    public override int GetHashCode() => HashElement(
        _element.ValueKind == JsonValueKind.Undefined ? Null._element : _element);
    public static bool operator ==(JsonValue left, JsonValue right) => left.Equals(right);
    public static bool operator !=(JsonValue left, JsonValue right) => !left.Equals(right);

    public static implicit operator JsonValue(string value) => From(value);
    public static implicit operator JsonValue(bool value) => From(value);
    public static implicit operator JsonValue(int value) => From(value);
    public static implicit operator JsonValue(long value) => From(value);
    public static implicit operator JsonValue(double value) => From(value);

    private static bool? ParseLooseBoolean(string? value) => value?.ToLowerInvariant() switch
    {
        "true" or "yes" or "1" => true,
        "false" or "no" or "0" => false,
        _ => null,
    };

    private static int HashElement(JsonElement element)
    {
        var hash = new HashCode();
        hash.Add(element.ValueKind);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    hash.Add(property.Name, StringComparer.Ordinal);
                    hash.Add(HashElement(property.Value));
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    hash.Add(HashElement(item));
                }

                break;
            case JsonValueKind.String:
                hash.Add(element.GetString(), StringComparer.Ordinal);
                break;
            case JsonValueKind.Number when element.TryGetDecimal(out var decimalValue):
                hash.Add(decimalValue);
                break;
            case JsonValueKind.Number:
                hash.Add(element.GetDouble());
                break;
            case JsonValueKind.True:
                hash.Add(true);
                break;
            case JsonValueKind.False:
                hash.Add(false);
                break;
        }

        return hash.ToHashCode();
    }
}

internal sealed class JsonValueConverter : JsonConverter<JsonValue>
{
    public override JsonValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return new JsonValue(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, JsonValue value, JsonSerializerOptions options) => value.WriteTo(writer);
}
