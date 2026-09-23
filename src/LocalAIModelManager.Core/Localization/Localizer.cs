using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace LocalAIModelManager.Core.Localization;

public sealed record LanguageOption(string Code, string DisplayName);

/// <summary>
/// Runtime localization for the whole application: the user interface and the log
/// output both read their text through <see cref="T"/>, so switching the language
/// switches everything at once.
///
/// Strings live in JSON files embedded next to this class (<c>strings.zh-CN.json</c>,
/// <c>strings.en-US.json</c>, <c>strings.ja-JP.json</c>, <c>strings.fr-FR.json</c>) and
/// are looked up by key. Lookup order is: current language, then English, then the key
/// itself, so a missing translation degrades to English instead of breaking the UI.
/// </summary>
public static class Localizer
{
    public const string DefaultLanguage = "zh-CN";
    public const string FallbackLanguage = "en-US";

    public static readonly IReadOnlyList<LanguageOption> Languages = new[]
    {
        new LanguageOption("zh-CN", "简体中文"),
        new LanguageOption("en-US", "English"),
        new LanguageOption("ja-JP", "日本語"),
        new LanguageOption("fr-FR", "Français"),
    };

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static string _current = DefaultLanguage;

    /// <summary>Raised after the language changed, so rebuildable UI can refresh itself.</summary>
    public static event Action<string>? LanguageChanged;

    public static string Current => _current;

    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return DefaultLanguage;
        }

        var trimmed = code.Trim();
        foreach (var language in Languages)
        {
            if (string.Equals(language.Code, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return language.Code;
            }

            // Accept "en", "ja", "fr", "zh" as well.
            var shortCode = language.Code.Split('-')[0];
            if (string.Equals(shortCode, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return language.Code;
            }
        }

        return DefaultLanguage;
    }

    public static void SetLanguage(string? code)
    {
        var normalized = Normalize(code);
        if (string.Equals(normalized, _current, StringComparison.Ordinal))
        {
            return;
        }

        _current = normalized;
        ApplyCulture(normalized);
        LanguageChanged?.Invoke(normalized);
    }

    /// <summary>True when the current language actually defines this key.</summary>
    public static bool Has(string key) =>
        !string.IsNullOrEmpty(key) &&
        (CurrentStrings().ContainsKey(key) || FallbackStrings().ContainsKey(key));

    /// <summary>Localized text for <paramref name="key"/>, formatted with <paramref name="args"/>.</summary>
    public static string T(string key, params object?[] args)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        string? value = null;
        if (CurrentStrings().TryGetValue(key, out var current) && !string.IsNullOrEmpty(current))
        {
            value = current;
        }
        else if (FallbackStrings().TryGetValue(key, out var fallback) && !string.IsNullOrEmpty(fallback))
        {
            value = fallback;
        }

        if (value is null)
        {
            return key;
        }

        if (args is null || args.Length == 0)
        {
            return value;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, value, args);
        }
        catch (FormatException)
        {
            // A translation with a broken placeholder must not crash the application.
            return value;
        }
    }

    /// <summary>Localized text, or <c>null</c> when the key is unknown in every language.</summary>
    public static string? TOrNull(string key) => Has(key) ? T(key) : null;

    private static IReadOnlyDictionary<string, string> CurrentStrings() => Load(_current);

    private static IReadOnlyDictionary<string, string> FallbackStrings() => Load(FallbackLanguage);

    private static IReadOnlyDictionary<string, string> Load(string code) =>
        Cache.GetOrAdd(code, static language =>
        {
            var assembly = typeof(Localizer).Assembly;
            var suffix = $"strings.{language}.json";
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

            if (name is null)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                {
                    return new Dictionary<string, string>(StringComparer.Ordinal);
                }

                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                return parsed is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        });

    private static void ApplyCulture(string code)
    {
        try
        {
            // Only the *UI* culture is switched. The data culture stays untouched on
            // purpose: parameters are parsed and formatted as invariant text, and a
            // decimal-comma culture would silently corrupt values such as 0.7.
            var culture = CultureInfo.GetCultureInfo(code);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // Keep the invariant culture.
        }
    }
}

/// <summary>Short alias used all over the code base.</summary>
public static class Loc
{
    public static string T(string key) => Localizer.T(key);

    public static string T(string key, params object?[] args) => Localizer.T(key, args);

    public static bool Has(string key) => Localizer.Has(key);
}
