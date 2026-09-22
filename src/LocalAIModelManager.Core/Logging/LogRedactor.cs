using System.Text.RegularExpressions;

namespace LocalAIModelManager.Core.Logging;

/// <summary>
/// Guarantees that secrets never reach the log buffer:
/// registered secrets (API keys, tokens) are replaced, and Authorization header
/// values are masked before anything is stored.
/// </summary>
public sealed partial class LogRedactor
{
    public const string Mask = "***REDACTED***";

    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Registers a literal secret that must never appear in a log line.</summary>
    public void RegisterSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 6)
        {
            return;
        }

        lock (_gate)
        {
            _secrets.Add(secret);
        }
    }

    public void ClearSecrets()
    {
        lock (_gate)
        {
            _secrets.Clear();
        }
    }

    public string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var result = message;

        string[] secrets;
        lock (_gate)
        {
            secrets = _secrets.Count == 0 ? Array.Empty<string>() : _secrets.ToArray();
        }

        foreach (var secret in secrets)
        {
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        }

        // Authorization: Bearer <token>
        result = BearerPattern().Replace(result, "Bearer " + Mask);

        // api_key / api-key / key = <value> style payloads
        result = KeyValuePattern().Replace(result, "$1" + Mask);

        // Anything that looks like a provider-style API key.
        result = ApiKeyTokenPattern().Replace(result, Mask);

        return result;
    }

    /// <summary>
    /// Body logging policy. By default only the byte count is recorded; full content
    /// is only kept when the operator explicitly opted in.
    /// </summary>
    public string DescribeBody(ReadOnlySpan<byte> body, bool contentLoggingEnabled, int maxChars)
    {
        if (!contentLoggingEnabled)
        {
            return $"body={body.Length}B";
        }

        var text = System.Text.Encoding.UTF8.GetString(body);
        var redacted = Redact(text);
        if (maxChars > 0 && redacted.Length > maxChars)
        {
            redacted = redacted[..maxChars] + $"...(+{redacted.Length - maxChars} chars)";
        }

        return $"body={body.Length}B content: {redacted}";
    }

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._\-+/=]{6,}")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)(""?(?:api[_-]?key|authorization|token|password)""?\s*[:=]\s*""?)([^"",\s}]{4,})")]
    private static partial Regex KeyValuePattern();

    /// <summary>Catches <c>sk-...</c> / <c>sk-lamm-...</c> tokens even without a label.</summary>
    [GeneratedRegex(@"(?i)\bsk-[A-Za-z0-9._\-]{6,}")]
    private static partial Regex ApiKeyTokenPattern();
}
