using System.Text.RegularExpressions;

namespace LocalAIModelManager.Core.Backends;

/// <summary>
/// Parses <c>llama-server --help</c> output into a capability set. Both the modern
/// "argument list" style and the older "options:" style are handled, and any flag
/// that is unknown to the catalog still shows up as a detected parameter - so a
/// future llama.cpp release exposes its new switches without a code change.
/// </summary>
public static partial class LlamaHelpParser
{
    public static EngineCapabilities Parse(string engineId, string executablePath, string helpText, string? fingerprint = null)
    {
        if (string.IsNullOrWhiteSpace(helpText))
        {
            return EngineCapabilities.Unavailable(engineId, executablePath, "The engine produced no help output.");
        }

        var detected = ExtractFlags(helpText);
        var descriptors = new List<ParameterDescriptor>();
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var known in ParameterCatalog.Known)
        {
            var spellings = new[] { known.Key }.Concat(known.Aliases).ToList();
            var present = spellings.Where(detected.Contains).ToList();
            if (present.Count == 0)
            {
                continue;
            }

            descriptors.Add(known);
            supported.Add(known.Key);

            // Both the canonical flag and every alias the build actually prints are
            // recorded, so Supports("-c") and Supports("--ctx-size") agree.
            foreach (var spelling in present)
            {
                supported.Add(spelling);
            }
        }

        // Surface flags the catalog does not know about yet.
        var catalogSpellings = ParameterCatalog.Known
            .SelectMany(k => new[] { k.Key }.Concat(k.Aliases))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var flag in detected.Where(f => !catalogSpellings.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            descriptors.Add(new ParameterDescriptor
            {
                Key = flag,
                DisplayName = flag,
                Category = ParameterCategories.Detected,
                Kind = ParameterKind.String,
                Advanced = true,
                DetectedOnly = true,
                Description = "Discovered from help output; not part of the built-in catalog.",
            });
            supported.Add(flag);
        }

        // The catalog order is the UI order; detected extras are appended in flag order.
        descriptors = descriptors
            .OrderBy(d => CategoryOrder(d.Category))
            .ThenBy(d => d.DetectedOnly ? 1 : 0)
            .ThenBy(d => ParameterCatalog.Known.ToList().FindIndex(k => string.Equals(k.Key, d.Key, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i : int.MaxValue)
            .ToList();

        return new EngineCapabilities
        {
            EngineId = engineId,
            ExecutablePath = executablePath,
            ExecutableFingerprint = fingerprint,
            Version = ExtractVersion(helpText),
            IsAvailable = true,
            RawHelpText = helpText,
            Parameters = descriptors,
            SupportedFlags = supported,
            DetectedFlags = detected,
        };
    }

    private static int CategoryOrder(string category)
    {
        var index = ParameterCategories.Ordered.ToList().FindIndex(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    public static HashSet<string> ExtractFlags(string helpText)
    {
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FlagPattern().Matches(helpText))
        {
            var token = match.Value.TrimEnd(',', ':', '.', ')');
            if (token.Length > 2)
            {
                flags.Add(token);
            }
        }

        return flags;
    }

    public static string? ExtractVersion(string helpText)
    {
        foreach (var pattern in VersionPatterns())
        {
            var match = pattern.Match(helpText);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
        }

        return null;
    }

    private static IEnumerable<Regex> VersionPatterns()
    {
        // "version: b6379 (1a2b3c4d)" / "build: b6379-mock" / "version = 6379"
        yield return new Regex(@"(?im)^\s*(?:version|build)\s*[:=]\s*([A-Za-z0-9][^\s(]*)");
        yield return new Regex(@"llama\.cpp\s+version\s+([A-Za-z0-9][^\s(]*)", RegexOptions.IgnoreCase);
        yield return new Regex(@"(?im)\b(?:version|build):\s*([A-Za-z0-9][^\s(]*)");
        yield return new Regex(@"version\s+([0-9]+\.[0-9][^\s(]*)", RegexOptions.IgnoreCase);
    }

    /// <summary>Matches both <c>--long-flag</c> and the single-dash short forms llama.cpp uses (<c>-ngl</c>).</summary>
    [GeneratedRegex(@"(?<![\w.-])(--[A-Za-z0-9][A-Za-z0-9._-]*|-[A-Za-z]{1,4}[0-9]?)(?![\w-])")]
    private static partial Regex FlagPattern();
}
