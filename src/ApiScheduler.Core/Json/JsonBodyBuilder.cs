using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiScheduler.Core.Models;

namespace ApiScheduler.Core.Json;

public static class JsonBodyBuilder
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonObject Build(IReadOnlyDictionary<string, object?> row, IEnumerable<FieldMapping> mappings)
    {
        var obj = new JsonObject();
        foreach (var m in mappings)
        {
            if (string.IsNullOrWhiteSpace(m.JsonField)) continue;
            object? raw;
            if (m.Source == MappingSource.Fixed)
            {
                raw = m.Value;
            }
            else
            {
                if (!row.TryGetValue(m.Value, out raw))
                    throw new KeyNotFoundException($"Column '{m.Value}' (for JSON field '{m.JsonField}') is not returned by the query.");
            }
            if (raw is null or DBNull && !string.IsNullOrEmpty(m.DefaultIfNull)) raw = m.DefaultIfNull;
            obj[m.JsonField] = Convert(raw, m.Type, m.JsonField);
        }
        return obj;
    }

    public static string Serialize(JsonObject obj, bool indented = false) => obj.ToJsonString(indented ? Pretty : Compact);

    private static JsonNode? Convert(object? value, FieldType type, string field)
    {
        if (value is null or DBNull) return null;
        switch (type)
        {
            case FieldType.Number:
                return value switch
                {
                    int or long or short or byte or decimal or double or float => JsonValue.Create(System.Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
                    _ when decimal.TryParse(System.Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
                    _ => throw new FormatException($"Field '{field}': value is not a number."),
                };
            case FieldType.Boolean:
                return value switch
                {
                    bool b => JsonValue.Create(b),
                    string s when bool.TryParse(s, out var b2) => JsonValue.Create(b2),
                    string s when s is "1" or "0" => JsonValue.Create(s == "1"),
                    int i => JsonValue.Create(i != 0),
                    _ => throw new FormatException($"Field '{field}': value is not true/false."),
                };
            default:
                return JsonValue.Create(value switch
                {
                    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString(),
                });
        }
    }
}
