using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaibeExam.Models;

/// <summary>Shared System.Text.Json options for the wire format (camelCase, lenient).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = false,
        };
        return o;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>ISO-8601 helpers matching the macOS client.</summary>
public static class Iso8601
{
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            return dto;
        }
        return null;
    }

    public static string Now() => Format(DateTimeOffset.UtcNow);

    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// Base converter for enums whose wire form is a fixed string (e.g. "kiosk-fallback").
/// Unknown / null / non-string input maps to <see cref="Fallback"/> instead of throwing.
/// </summary>
public abstract class MappedEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    protected abstract IReadOnlyDictionary<T, string> Map { get; }
    protected abstract T Fallback { get; }

    /// <summary>Let the converter see JSON null so it can map it to the fallback instead of throwing.</summary>
    public override bool HandleNull => true;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString() ?? string.Empty;
            foreach (var kv in Map)
            {
                if (string.Equals(kv.Value, s, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            }
            return Fallback;
        }
        // null, number, true/false: single tokens — nothing to skip. Object / array: the reader
        // is on StartObject / StartArray and MUST be advanced to the matching end token, or the
        // serializer's position is corrupted and the whole document fails to deserialize. (W-28)
        if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Skip();
        }
        return Fallback;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(Map.TryGetValue(value, out var s) ? s : value.ToString());
    }

    public string ToWire(T value) => Map.TryGetValue(value, out var s) ? s : value.ToString();
}
