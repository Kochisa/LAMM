using System.Net.Http;

namespace LocalAIModelManager.Core.Backends;

/// <summary>
/// HTTP clients used to talk to engine instances. Proxy usage is disabled on
/// purpose: internal engine traffic must never leave the loopback interface,
/// even when the machine has a system-wide proxy configured.
/// </summary>
public static class LoopbackHttp
{
    public static HttpClient Create(TimeSpan timeout, bool disableProxy = true)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = !disableProxy,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(5, timeout.TotalSeconds)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        return new HttpClient(handler)
        {
            Timeout = timeout,
        };
    }

    /// <summary>Shared client for short readiness probes.</summary>
    public static HttpClient Probe { get; } = Create(TimeSpan.FromSeconds(3));
}
