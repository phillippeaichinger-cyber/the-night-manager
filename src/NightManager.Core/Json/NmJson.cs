using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightManager.Core.Json;

/// <summary>The single JSON configuration for the wire format and local persistence.</summary>
public static class NmJson {
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create() {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            WriteIndented = false
        };
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Always writes UTC with millisecond precision ("2026-10-12T21:04:05.123Z").</summary>
    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset> {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Timestamps.Format(value));
    }
}

public static class Timestamps {
    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
