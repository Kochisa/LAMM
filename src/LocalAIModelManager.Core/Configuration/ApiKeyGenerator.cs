using System.Security.Cryptography;

namespace LocalAIModelManager.Core.Configuration;

/// <summary>Generates and compares gateway API keys.</summary>
public static class ApiKeyGenerator
{
    public const string Prefix = "sk-lamm-";

    /// <summary>Creates a new cryptographically random key, e.g. <c>sk-lamm-3f9c... </c>.</summary>
    public static string Create(int byteLength = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        var body = Convert.ToBase64String(bytes)
            .Replace('+', 'A')
            .Replace('/', 'B')
            .Replace('=', 'C');
        return Prefix + body;
    }

    /// <summary>Constant-time comparison so key checks do not leak timing information.</summary>
    public static bool FixedTimeEquals(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var a = System.Text.Encoding.UTF8.GetBytes(provided);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Never log a full key; this is the only representation that may reach the log buffer.</summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "(none)";
        }

        return key.Length <= 12 ? key[..2] + "***" : key[..12] + "***";
    }
}
