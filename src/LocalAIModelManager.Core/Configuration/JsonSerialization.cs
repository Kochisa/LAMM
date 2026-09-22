using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAIModelManager.Core.Configuration;

/// <summary>Single source of truth for on-disk JSON formatting.</summary>
public static class JsonSerialization
{
    public static readonly JsonSerializerOptions Options = Create(writeIndented: true);

    public static readonly JsonSerializerOptions Compact = Create(writeIndented: false);

    private static JsonSerializerOptions Create(bool writeIndented) => new()
    {
        WriteIndented = writeIndented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
