namespace LocalAIModelManager.Core.Backends;

/// <summary>
/// Turns the free-form "Other" argument lines into command line tokens.
///
/// The user types plain CLI text, one argument line per entry, for example:
/// <code>
/// --some-option value
/// --another-option
/// </code>
/// Tokens are passed through verbatim and in order. The only thing that is refused are
/// the flags the manager owns itself, because letting them through would silently break
/// the engine's bind address or port.
/// </summary>
public static class AdditionalArguments
{
    public static (IReadOnlyList<string> Tokens, IReadOnlyList<string> Warnings) Tokenize(IEnumerable<string>? lines)
    {
        var tokens = new List<string>();
        var warnings = new List<string>();

        if (lines is null)
        {
            return (tokens, warnings);
        }

        foreach (var line in lines)
        {
            var parts = (line ?? string.Empty)
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            for (var i = 0; i < parts.Count; i++)
            {
                var token = TrimQuotes(parts[i]);
                if (token.Length == 0)
                {
                    continue;
                }

                if (ParameterCatalog.ManagedFlags.Contains(token))
                {
                    warnings.Add($"'{token}' 由管理器托管，已忽略该自定义参数。");
                    if (i + 1 < parts.Count && !parts[i + 1].StartsWith('-'))
                    {
                        i++;
                    }

                    continue;
                }

                tokens.Add(token);
            }
        }

        return (tokens, warnings);
    }

    private static string TrimQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
