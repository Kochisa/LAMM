using LocalAIModelManager.Core.Configuration;

namespace LocalAIModelManager.Core.Gateway;

/// <summary>Effective, already-validated gateway options.</summary>
public sealed record GatewayOptions
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public required bool ApiKeyEnabled { get; init; }

    public required string ApiKey { get; init; }

    public required bool AllowLanAccess { get; init; }

    public required bool EnableStatusEndpoint { get; init; }

    public required int RequestTimeoutSeconds { get; init; }

    public required long MaxRequestBytes { get; init; }

    /// <summary>When false, loopback callers may skip the API key.</summary>
    public bool RequireApiKeyForLocalhost { get; init; } = true;

    public int MaxConcurrentRequests { get; init; } = 64;

    public bool IsLoopback =>
        Host is ApiSettings.Loopback or "localhost" or "::1";

    public string DisplayBaseUrl => $"http://{(IsLoopback ? ApiSettings.Loopback : Host)}:{Port}";

    /// <summary>
    /// Applies the safety rules that must never be bypassable from the UI:
    /// LAN binding requires an API key, LAN binding must be explicitly enabled,
    /// and the internal engine ports never leave loopback.
    /// </summary>
    public static GatewayOptions FromSettings(AppSettings settings)
    {
        var api = settings.Api;
        var host = api.Host;

        if (!api.AllowLanAccess)
        {
            host = ApiSettings.Loopback;
        }

        var apiKeyEnabled = api.ApiKeyEnabled;
        if (!IsLoopbackHost(host) && !apiKeyEnabled && !settings.Advanced.AllowLanWithoutApiKey)
        {
            // Fail closed: refuse the LAN bind rather than serving unauthenticated.
            host = ApiSettings.Loopback;
            apiKeyEnabled = true;
        }

        return new GatewayOptions
        {
            Host = host,
            Port = api.Port,
            ApiKeyEnabled = apiKeyEnabled,
            ApiKey = api.ApiKey,
            AllowLanAccess = api.AllowLanAccess,
            EnableStatusEndpoint = api.EnableStatusEndpoint,
            RequestTimeoutSeconds = api.RequestTimeoutSeconds,
            MaxRequestBytes = settings.Advanced.MaxRequestBytes,
            RequireApiKeyForLocalhost = api.RequireApiKeyForLocalhost,
            MaxConcurrentRequests = api.MaxConcurrentRequests,
        };
    }

    private static bool IsLoopbackHost(string host) =>
        host is ApiSettings.Loopback or "localhost" or "::1";
}
