using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Lifecycle;
using LocalAIModelManager.Core.Logging;
using LocalAIModelManager.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalAIModelManager.Core.Gateway;

/// <summary>
/// The public OpenAI-compatible gateway. It is the only listener that faces
/// clients; engine processes stay on loopback on their own internal ports.
///
/// Request flow for <c>POST /v1/chat/completions</c>:
/// resolve model -> acquire (lazy load, possibly starting llama-server)
/// -> forward -> stream back -> release lease.
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    private readonly IModelLifecycleManager _lifecycle;
    private readonly ModelRegistry _models;
    private readonly IAppLogger _logger;
    private readonly LogRedactor _redactor;
    private readonly Func<RuntimeStatusSnapshot>? _statusProvider;
    private readonly HttpClient _upstream;
    private readonly SemaphoreSlim _concurrencyGate;

    private WebApplication? _application;
    private long _totalRequests;
    private long _activeRequests;
    private string? _lastError;

    public GatewayHost(
        GatewayOptions options,
        IModelLifecycleManager lifecycle,
        ModelRegistry models,
        IAppLogger logger,
        LogRedactor redactor,
        Func<RuntimeStatusSnapshot>? statusProvider = null)
    {
        Options = options;
        _lifecycle = lifecycle;
        _models = models;
        _logger = logger;
        _redactor = redactor;
        _statusProvider = statusProvider;

        // No client timeout: streaming responses may legitimately run for minutes.
        // Per-request limits are enforced with a linked CancellationToken instead.
        _upstream = Backends.LoopbackHttp.Create(Timeout.InfiniteTimeSpan);
        _concurrencyGate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentRequests));

        if (options.ApiKeyEnabled)
        {
            redactor.RegisterSecret(options.ApiKey);
        }
    }

    public GatewayOptions Options { get; }

    public bool IsRunning => _application is not null;

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public string BaseUrl => Options.DisplayBaseUrl;

    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    public long ActiveRequests => Interlocked.Read(ref _activeRequests);

    public string? LastError => _lastError;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_application is not null)
        {
            return;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = "LocalAIModelManager.Gateway",
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None);
        builder.Services.AddSingleton<IHostLifetime, NoopHostLifetime>();

        var address = ResolveAddress(Options.Host);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = Options.MaxRequestBytes;
            kestrel.Listen(address, Options.Port);
        });

        var app = builder.Build();
        ConfigurePipeline(app);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        _application = app;
        StartedAtUtc = DateTimeOffset.UtcNow;
        _logger.Info("gateway",
            $"API gateway listening on http://{Options.Host}:{Options.Port} (api key {(Options.ApiKeyEnabled ? "enabled" : "disabled")}, " +
            $"LAN {(Options.AllowLanAccess ? "enabled" : "disabled")}, LAN bind {(Options.IsLoopback ? "no" : "yes")})");

        if (!Options.IsLoopback && !Options.ApiKeyEnabled)
        {
            _logger.Warn("gateway", "the gateway is bound to a non-loopback address without an API key");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var app = _application;
        if (app is null)
        {
            return;
        }

        _application = null;
        try
        {
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
            StartedAtUtc = null;
            _logger.Info("gateway", "API gateway stopped");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _concurrencyGate.Dispose();
        _upstream.Dispose();
    }

    private void ConfigurePipeline(WebApplication app)
    {
        // ---- global error boundary -------------------------------------------
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client hung up mid-stream; nothing to report.
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.Error("gateway", $"unhandled error for {context.Request.Method} {context.Request.Path}", ex);
                if (!context.Response.HasStarted)
                {
                    await WriteErrorAsync(
                        context,
                        500,
                        "Internal gateway error.",
                        "server_error",
                        "internal_error").ConfigureAwait(false);
                }
            }
        });

        // ---- authentication ---------------------------------------------------
        app.Use(async (context, next) =>
        {
            if (!Options.ApiKeyEnabled || context.Request.Path.StartsWithSegments("/health"))
            {
                await next();
                return;
            }

            // Local-only convenience: an operator may decide that processes on this
            // machine do not need the key. LAN traffic always does.
            if (!Options.RequireApiKeyForLocalhost && IsLoopbackCaller(context))
            {
                await next();
                return;
            }

            var provided = ExtractApiKey(context.Request);
            if (!ApiKeyGenerator.FixedTimeEquals(provided, Options.ApiKey))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                _logger.Warn("gateway",
                    $"rejected unauthenticated request for {context.Request.Method} {context.Request.Path} from {context.Connection.RemoteIpAddress}");
                await WriteErrorAsync(
                    context,
                    401,
                    "Missing or invalid API key. Use 'Authorization: Bearer <API_KEY>'.",
                    "invalid_request_error",
                    "invalid_api_key").ConfigureAwait(false);
                return;
            }

            await next();
        });

        // ---- health -----------------------------------------------------------
        app.MapGet("/health", (HttpContext context) =>
        {
            var status = _statusProvider?.Invoke();
            return Results.Json(new
            {
                status = "ok",
                service = "local-ai-model-manager",
                gateway = new
                {
                    running = IsRunning,
                    baseUrl = BaseUrl,
                    apiKeyEnabled = Options.ApiKeyEnabled,
                    lanAccess = Options.AllowLanAccess,
                },
                models = new
                {
                    registered = _models.Count,
                    loaded = status?.LoadedModelCount ?? 0,
                },
                uptimeSeconds = StartedAtUtc is { } started
                    ? (long)(DateTimeOffset.UtcNow - started).TotalSeconds
                    : 0,
            }, JsonSerialization.Compact);
        });

        // ---- model catalog ----------------------------------------------------
        app.MapGet("/v1/models", (HttpContext context) =>
        {
            var data = _models.All.Select(model =>
            {
                var runtime = _lifecycle.GetStatus(model.Id);
                return new
                {
                    id = model.Id,
                    @object = "model",
                    created = model.CreatedAtUtc.ToUnixTimeSeconds(),
                    owned_by = "local-ai-model-manager",
                    lamm = new
                    {
                        displayName = model.DisplayName,
                        engineId = model.EngineId,
                        enabled = model.Enabled,
                        state = (runtime?.State ?? Models.ModelState.Standby).ToString().ToLowerInvariant(),
                        loaded = runtime?.IsLoaded ?? false,
                    },
                };
            }).ToList();

            return Results.Json(new { @object = "list", data }, JsonSerialization.Compact);
        });

        app.MapGet("/v1/models/{id}", (string id) =>
        {
            var model = _models.Get(id);
            if (model is null)
            {
                return Results.Json(
                    ErrorPayload($"The model '{id}' does not exist.", "invalid_request_error", "model_not_found"),
                    JsonSerialization.Compact,
                    statusCode: StatusCodes.Status404NotFound);
            }

            var runtime = _lifecycle.GetStatus(model.Id);
            return Results.Json(new
            {
                id = model.Id,
                @object = "model",
                created = model.CreatedAtUtc.ToUnixTimeSeconds(),
                owned_by = "local-ai-model-manager",
                lamm = new
                {
                    displayName = model.DisplayName,
                    engineId = model.EngineId,
                    enabled = model.Enabled,
                    state = (runtime?.State ?? Models.ModelState.Standby).ToString().ToLowerInvariant(),
                    loaded = runtime?.IsLoaded ?? false,
                },
            }, JsonSerialization.Compact);
        });

        // ---- inference proxies ------------------------------------------------
        app.MapPost("/v1/chat/completions", (HttpContext context) =>
            HandleInferenceAsync(context, "/v1/chat/completions"));

        app.MapPost("/v1/completions", (HttpContext context) =>
            HandleInferenceAsync(context, "/v1/completions"));

        app.MapPost("/v1/embeddings", (HttpContext context) =>
            HandleInferenceAsync(context, "/v1/embeddings"));

        // ---- internal status --------------------------------------------------
        if (Options.EnableStatusEndpoint)
        {
            app.MapGet("/v1/internal/status", () =>
                Results.Json(_statusProvider?.Invoke(), JsonSerialization.Compact));
        }
    }

    private async Task HandleInferenceAsync(HttpContext context, string upstreamPath)
    {
        if (!_concurrencyGate.Wait(0))
        {
            Interlocked.Increment(ref _totalRequests);
            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                $"Too many concurrent requests (limit {Options.MaxConcurrentRequests}). Retry shortly.",
                "server_error",
                "too_many_requests").ConfigureAwait(false);
            return;
        }

        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _activeRequests);
        var started = System.Diagnostics.Stopwatch.StartNew();
        string? modelId = null;
        var status = StatusCodes.Status200OK;

        try
        {
            byte[] body;
            using (var buffer = new MemoryStream())
            {
                await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
                body = buffer.ToArray();
            }

            if (body.Length == 0)
            {
                status = StatusCodes.Status400BadRequest;
                await WriteErrorAsync(context, status, "The request body is empty.", "invalid_request_error", "invalid_body").ConfigureAwait(false);
                return;
            }

            bool streamRequested;
            try
            {
                using var document = JsonDocument.Parse(body);
                modelId = document.RootElement.TryGetProperty("model", out var modelElement)
                    ? modelElement.GetString()
                    : null;
                streamRequested = document.RootElement.TryGetProperty("stream", out var streamElement) &&
                                  streamElement.ValueKind == JsonValueKind.True;
            }
            catch (JsonException ex)
            {
                status = StatusCodes.Status400BadRequest;
                await WriteErrorAsync(context, status, $"The request body is not valid JSON: {ex.Message}", "invalid_request_error", "invalid_body")
                    .ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(modelId))
            {
                status = StatusCodes.Status400BadRequest;
                await WriteErrorAsync(context, status, "You must provide a 'model' parameter.", "invalid_request_error", "missing_model")
                    .ConfigureAwait(false);
                return;
            }

            var model = _models.Get(modelId);
            if (model is null)
            {
                status = StatusCodes.Status404NotFound;
                await WriteErrorAsync(
                    context,
                    status,
                    $"The model '{modelId}' is not registered with Local AI Model Manager.",
                    "invalid_request_error",
                    "model_not_found").ConfigureAwait(false);
                return;
            }

            _logger.Info("gateway",
                $"{(streamRequested ? "streaming " : string.Empty)}request for model '{model.Id}' ({body.Length}B)");

            ModelLease lease;
            try
            {
                lease = await _lifecycle.AcquireAsync(model.Id, context.RequestAborted).ConfigureAwait(false);
            }
            catch (ModelNotFoundException ex)
            {
                status = StatusCodes.Status404NotFound;
                await WriteErrorAsync(context, status, ex.Message, "invalid_request_error", "model_not_found").ConfigureAwait(false);
                return;
            }
            catch (ModelCapacityException ex)
            {
                status = StatusCodes.Status503ServiceUnavailable;
                await WriteErrorAsync(context, status, ex.Message, "server_error", "no_capacity").ConfigureAwait(false);
                return;
            }
            catch (ModelUnavailableException ex)
            {
                status = StatusCodes.Status502BadGateway;
                await WriteErrorAsync(context, status, ex.Message, "server_error", "model_load_failed").ConfigureAwait(false);
                return;
            }

            using (lease)
            {
                status = await ProxyAsync(context, lease, upstreamPath, body).ConfigureAwait(false);
            }

            _logger.Info("gateway",
                $"model '{model.Id}' responded {status} in {started.ElapsedMilliseconds}ms");
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
            _concurrencyGate.Release();

            if (modelId is not null)
            {
                _logger.Debug("gateway",
                    $"completed {context.Request.Method} {context.Request.Path} model='{modelId}' status={status} in {started.ElapsedMilliseconds}ms");
            }
        }
    }

    private async Task<int> ProxyAsync(HttpContext context, ModelLease lease, string upstreamPath, byte[] body)
    {
        var target = lease.BaseUrl + upstreamPath;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        if (Options.RequestTimeoutSeconds > 0)
        {
            linked.CancelAfter(TimeSpan.FromSeconds(Options.RequestTimeoutSeconds));
        }

        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new ByteArrayContent(body),
        };
        upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _upstream
                .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status504GatewayTimeout,
                $"The engine did not respond within {Options.RequestTimeoutSeconds}s.",
                "server_error",
                "engine_timeout").ConfigureAwait(false);
            return StatusCodes.Status504GatewayTimeout;
        }
        catch (HttpRequestException ex)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status502BadGateway,
                $"Could not reach the engine for model '{lease.ModelId}': {ex.Message}",
                "server_error",
                "engine_unreachable").ConfigureAwait(false);
            return StatusCodes.Status502BadGateway;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.ToString();
            if (!string.IsNullOrEmpty(contentType))
            {
                context.Response.ContentType = contentType;
            }

            context.Response.Headers.CacheControl = "no-store";

            var isEventStream = contentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
            if (isEventStream)
            {
                // Streaming must not be buffered by the server.
                context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                context.Response.Headers["X-Accel-Buffering"] = "no";
            }

            long totalBytes = 0;
            try
            {
                await using var upstreamStream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
                try
                {
                    int read;
                    while ((read = await upstreamStream.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                    {
                        totalBytes += read;
                        await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                        await context.Response.Body.FlushAsync(linked.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                _logger.Debug("gateway", $"client disconnected while streaming model '{lease.ModelId}'");
                return context.Response.StatusCode;
            }

            _logger.Debug("gateway",
                $"proxied {totalBytes}B from engine port {lease.Port} for model '{lease.ModelId}'");
            return context.Response.StatusCode;
        }
    }

    private async Task WriteErrorAsync(HttpContext context, int statusCode, string message, string type, string code)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response
            .WriteAsync(JsonSerializer.Serialize(ErrorPayload(_redactor.Redact(message), type, code), JsonSerialization.Compact), context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static object ErrorPayload(string message, string type, string code) => new
    {
        error = new
        {
            message,
            type,
            param = (string?)null,
            code,
        },
    };

    private static string? ExtractApiKey(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(header))
        {
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return header["Bearer ".Length..].Trim();
            }

            return header.Trim();
        }

        if (request.Headers.TryGetValue("x-api-key", out var alternate))
        {
            return alternate.ToString().Trim();
        }

        return null;
    }

    private static bool IsLoopbackCaller(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        return remote is null || System.Net.IPAddress.IsLoopback(remote);
    }

    private static IPAddress ResolveAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host is "localhost" or ApiSettings.Loopback)
        {
            return IPAddress.Loopback;
        }

        return host switch
        {
            ApiSettings.AnyAddress => IPAddress.Any,
            "::1" => IPAddress.IPv6Loopback,
            _ => IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Loopback,
        };
    }

    private sealed class NoopHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
