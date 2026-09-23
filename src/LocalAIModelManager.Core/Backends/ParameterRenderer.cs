namespace LocalAIModelManager.Core.Backends;

/// <summary>
/// Turns one stored parameter into command line tokens.
///
/// This lives in one place because both adapters must agree: a boolean switch such as
/// <c>--mtp</c>, <c>--flash-attn</c> or <c>--no-kv-offload</c> is a bare flag, and passing
/// <c>--mtp true</c> makes llama.cpp reject the command line. The adapter that talks to a
/// generic OpenAI-compatible server used to emit "flag value" for everything, which broke
/// every boolean the UI offers.
/// </summary>
internal static class ParameterRenderer
{
    /// <summary>
    /// Catalog descriptor for <paramref name="key"/>: what the probed build reported, or
    /// failing that the built-in catalog. <c>null</c> for a flag nobody knows, which is
    /// then passed through as "flag value".
    /// </summary>
    public static ParameterDescriptor? Resolve(string key, EngineCapabilities capabilities) =>
        capabilities.Find(key) ?? ParameterCatalog.Find(key);

    /// <summary>
    /// Renders <paramref name="value"/> for <paramref name="key"/>. Returns <c>false</c>
    /// with an empty <paramref name="rendered"/> when the parameter must not be sent at
    /// all - an explicitly disabled boolean, or an empty value.
    /// </summary>
    public static bool TryRender(
        string key,
        string value,
        EngineCapabilities capabilities,
        out List<string> rendered)
    {
        rendered = new List<string>();
        var descriptor = Resolve(key, capabilities);
        var trimmed = (value ?? string.Empty).Trim();

        if (descriptor?.Kind == ParameterKind.Boolean)
        {
            if (string.IsNullOrEmpty(trimmed) || IsTruthy(trimmed))
            {
                rendered.Add(key);
                return true;
            }

            // Explicitly disabled: emit nothing at all.
            return false;
        }

        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        rendered.Add(key);
        rendered.Add(trimmed);
        return true;
    }

    public static bool IsTruthy(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("on", StringComparison.OrdinalIgnoreCase);
}
