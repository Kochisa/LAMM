using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalAIModelManager.MockEngine;

/// <summary>
/// An offline, OpenAI-compatible stand-in for <c>llama-server</c>.
///
/// It exists so the whole manager (lazy load, readiness probing, streaming proxy,
/// idle unload, VRAM eviction, engine switching) can be verified end-to-end on a
/// machine with no llama.cpp download and no model weights. It deliberately mimics
/// llama.cpp's CLI surface - including a <c>--help</c> text that advertises only a
/// subset of the catalogued flags - so capability detection is exercised for real.
/// </summary>
internal static class Program
{
    private const string EngineVersion = "b6379-mock";

    /// <summary>
    /// Sidecar files let a test simulate "a different llama.cpp build was installed":
    /// <c>mock-engine.version</c> changes the reported version and
    /// <c>mock-engine.flags</c> adds help lines, i.e. new CLI flags that only that
    /// build supports. That is exactly the situation the capability probe must handle.
    /// </summary>
    private const string VersionSidecar = "mock-engine.version";

    private const string FlagsSidecar = "mock-engine.flags";

    private static readonly Lazy<string> ReportedVersion = new(() =>
        ReadSidecar(VersionSidecar) is { Length: > 0 } custom ? custom : EngineVersion);

    private static readonly Lazy<string[]> ExtraHelpLines = new(() =>
        ReadSidecar(FlagsSidecar) is { Length: > 0 } extra
            ? extra.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>());

    private static readonly object Sync = new();
    private static readonly Dictionary<string, string> Arguments = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> RawArguments = new();
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    private static bool _loaded;
    private static string _model = "(none)";
    private static string _alias = "mock-model";
    private static int _port;
    private static string _host = "127.0.0.1";
    private static int _loadMilliseconds;
    private static bool _failLoad;
    private static bool _exitOnStdinEof = true;
    private static bool _detachedConsole;
    private static long _completionCount;
    private static readonly StringBuilder StartupLog = new();

    private static async Task<int> Main(string[] args)
    {
        ParseArguments(args);

        if (Has("--help") || Has("-h"))
        {
            PrintHelp();
            return 0;
        }

        // The manager probes this to learn whether GPU offload is actually possible. This
        // demo engine has no compute backend, so it reports no devices - exactly like a
        // CPU-only llama.cpp build - and must answer immediately instead of starting up.
        if (Has("--list-devices"))
        {
            Console.WriteLine("Available devices:");
            Console.WriteLine("  (none)");
            return 0;
        }

        if (Has("--version"))
        {
            Console.WriteLine($"version: {ReportedVersion.Value}");
            Console.WriteLine("built with MSVC 19.38 for x86_64");
            return 0;
        }

        _model = Get("--model", Get("-m", "(none)"))!;
        _host = Get("--host", "127.0.0.1")!;
        _port = int.TryParse(Get("--port", "8080"), out var port) ? port : 8080;
        _alias = Get("--alias", Path.GetFileNameWithoutExtension(_model) ?? "mock-model")!;
        _loadMilliseconds = int.TryParse(Get("--mock-load-ms", "400"), out var loadMs) ? loadMs : 400;
        _failLoad = Has("--mock-fail-load");
        _exitOnStdinEof = !string.Equals(Get("--mock-exit-on-stdin-eof", "true"), "false", StringComparison.OrdinalIgnoreCase);
        _detachedConsole = Has("--mock-no-console");

        if (!File.Exists(_model) && !_failLoad)
        {
            Console.Error.WriteLine($"error: failed to load model '{_model}': file not found");
            return 2;
        }

        EmitStartupBanner();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = "LocalAIModelManager.MockEngine",
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Parse(_host), _port));

        var app = builder.Build();
        MapEndpoints(app);

        _ = Task.Run(LoadModelAsync);
        WatchStdin();
        try
        {
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/health", () => _loaded
            ? Results.Json(new { status = "ok" }, Compact)
            : Results.Json(new { error = "Loading model" }, Compact, statusCode: StatusCodes.Status503ServiceUnavailable));

        app.MapGet("/props", () => Results.Json(new
        {
            default_generation_settings = new { n_ctx = int.TryParse(Get("--ctx-size", Get("-c", "4096")), out var ctx) ? ctx : 4096 },
            model_path = _model,
            total_slots = 1,
        }, Compact));

        app.MapGet("/v1/models", () => Results.Json(new
        {
            @object = "list",
            data = new[]
            {
                new
                {
                    id = _alias,
                    @object = "model",
                    created = StartedAt.ToUnixTimeSeconds(),
                    owned_by = "mock-engine",
                },
            },
        }, Compact));

        // Test-only introspection endpoint used by the acceptance harness.
        app.MapGet("/mock/state", () => Results.Json(new
        {
            pid = Environment.ProcessId,
            loaded = _loaded,
            model = _model,
            alias = _alias,
            host = _host,
            port = _port,
            arguments = RawArguments,
            parameters = Arguments,
            completions = Interlocked.Read(ref _completionCount),
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
        }, Compact));

        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            if (!_loaded)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("{\"error\":{\"message\":\"Loading model\"}}");
                return;
            }

            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var request = ParseChatRequest(body);
            Interlocked.Increment(ref _completionCount);

            if (request.Stream)
            {
                await WriteStreamAsync(context, request);
            }
            else
            {
                await WriteCompletionAsync(context, request);
            }
        });

        app.MapPost("/v1/completions", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            await context.Response.WriteAsync(body.Length == 0
                ? "{\"error\":{\"message\":\"empty\"}}"
                : $"{{\"id\":\"cmpl-mock\",\"object\":\"text_completion\",\"model\":\"{_alias}\",\"choices\":[{{\"text\":\"mock\",\"index\":0,\"finish_reason\":\"stop\"}}]}}");
        });

        app.MapPost("/v1/embeddings", async (HttpContext context) =>
        {
            await context.Response.WriteAsync(
                $"{{\"object\":\"list\",\"model\":\"{_alias}\",\"data\":[{{\"object\":\"embedding\",\"index\":0,\"embedding\":[0.0,0.1,0.2]}}]}}");
        });
    }

    private static async Task WriteCompletionAsync(HttpContext context, ChatRequest request)
    {
        var content = BuildContent(request);
        var id = "chatcmpl-mock-" + Guid.NewGuid().ToString("N")[..12];
        var payload = new
        {
            id,
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = _alias,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content },
                    finish_reason = "stop",
                },
            },
            usage = new
            {
                prompt_tokens = CountTokens(request.Prompt),
                completion_tokens = CountTokens(content),
                total_tokens = CountTokens(request.Prompt) + CountTokens(content),
            },
        };

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, Compact));
    }

    private static async Task WriteStreamAsync(HttpContext context, ChatRequest request)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

        var id = "chatcmpl-mock-" + Guid.NewGuid().ToString("N")[..12];
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var content = BuildContent(request);
        var pieces = Chunk(content, 6);

        foreach (var piece in pieces)
        {
            var chunk = new
            {
                id,
                @object = "chat.completion.chunk",
                created,
                model = _alias,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { role = "assistant", content = piece },
                        finish_reason = (string?)null,
                    },
                },
            };

            await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(chunk, Compact) + "\n\n");
            await context.Response.Body.FlushAsync();
            await Task.Delay(15);
        }

        var final = new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model = _alias,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "stop",
                },
            },
        };

        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(final, Compact) + "\n\n");
        await context.Response.WriteAsync("data: [DONE]\n\n");
        await context.Response.Body.FlushAsync();
    }

    private static string BuildContent(ChatRequest request)
    {
        var prompt = request.Prompt;
        if (prompt.Length > 200)
        {
            prompt = prompt[..200];
        }

        var contextSize = Get("--ctx-size", Get("-c", "(default)")) ?? "(default)";
        return $"[mock:{_alias}] ctx={contextSize} echo: {prompt}";
    }

    private static IEnumerable<string> Chunk(string value, int size)
    {
        for (var i = 0; i < value.Length; i += size)
        {
            yield return value.Substring(i, Math.Min(size, value.Length - i));
        }

        if (value.Length == 0)
        {
            yield return string.Empty;
        }
    }

    private static int CountTokens(string value) => Math.Max(1, value.Length / 4);

    private static ChatRequest ParseChatRequest(string body)
    {
        var stream = false;
        var prompt = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("stream", out var streamElement) && streamElement.ValueKind == JsonValueKind.True)
            {
                stream = true;
            }

            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.TryGetProperty("content", out var contentElement))
                    {
                        prompt = contentElement.ValueKind == JsonValueKind.String
                            ? contentElement.GetString() ?? string.Empty
                            : contentElement.ToString();
                    }
                }
            }
            else if (root.TryGetProperty("prompt", out var promptElement))
            {
                prompt = promptElement.ToString();
            }
        }
        catch (JsonException)
        {
        }

        return new ChatRequest(stream, prompt);
    }

    private static async Task LoadModelAsync()
    {
        await Task.Delay(Math.Max(0, _loadMilliseconds));

        if (_failLoad)
        {
            Console.Error.WriteLine("error: failed to load model (simulated failure)");
            Environment.Exit(3);
        }

        lock (Sync)
        {
            _loaded = true;
        }

        Console.WriteLine($"main: model loaded");
        Console.WriteLine($"main: server is listening on http://{_host}:{_port} - starting the main loop");
        await Console.Out.FlushAsync();
    }

    private static void WatchStdin()
    {
        if (_detachedConsole)
        {
            // Models an engine that offers no console at all: neither stdin nor a
            // console control event can reach it, so only a forced kill will stop it.
            FreeConsole();
            Console.WriteLine("main: running without a console (graceful stop unavailable)");
            Console.Out.Flush();
            return;
        }

        // A control event is honoured when the engine is configured as "polite".
        // A "stubborn" engine (mock-exit-on-stdin-eof false) deliberately ignores
        // every graceful signal so the forced-kill path can be verified.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            if (_exitOnStdinEof)
            {
                Console.WriteLine("main: interrupt received, shutting down");
                Environment.Exit(0);
            }

            Console.WriteLine("main: ignoring shutdown request");
        };

        if (!_exitOnStdinEof)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var line = Console.In.ReadLine();
                    if (line is null)
                    {
                        break;
                    }

                    if (line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // stdin closed
            }

            Console.WriteLine("main: stdin closed, shutting down gracefully");
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "stdin-watch",
        };

        thread.Start();
    }

    private static void EmitStartupBanner()
    {
        var banner = new[]
        {
            $"build: {ReportedVersion.Value} (mock build, offline)",
            $"system info: n_threads = {Get("--threads", Get("-t", "8"))}, n_threads_batch = {Get("--threads-batch", Get("-tb", "8"))}",
            $"main: loading model",
            $"main:   model path  = {_model}",
            $"main:   n_ctx       = {Get("--ctx-size", Get("-c", "4096"))}",
            $"main:   n_gpu_layers= {Get("--n-gpu-layers", Get("-ngl", "0"))}",
            $"main:   cache type K= {Get("--cache-type-k", "f16")}, V= {Get("--cache-type-v", "f16")}",
        };

        lock (Sync)
        {
            foreach (var line in banner)
            {
                StartupLog.AppendLine(line);
            }
        }

        foreach (var line in banner)
        {
            Console.WriteLine(line);
        }

        Console.Out.Flush();
    }

    private static void ParseArguments(string[] args)
    {
        RawArguments.Clear();
        RawArguments.AddRange(args);

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith('-'))
            {
                continue;
            }

            var hasValue = i + 1 < args.Length && !IsFlag(args[i + 1]);
            if (hasValue)
            {
                Arguments[token] = args[i + 1];
                i++;
            }
            else
            {
                Arguments[token] = string.Empty;
            }
        }
    }

    /// <summary>True for a flag token; a leading minus followed by a digit is a negative value.</summary>
    private static bool IsFlag(string token) =>
        token.StartsWith('-') && !(token.Length > 1 && (char.IsDigit(token[1]) || token[1] == '.'));

    private static bool Has(string flag) => Arguments.ContainsKey(flag);

    private static string? Get(string flag, string? fallback = null) =>
        Arguments.TryGetValue(flag, out var value) && value.Length > 0 ? value : fallback;

    private static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record ChatRequest(bool Stream, string Prompt);

    private static string? ReadSidecar(string fileName)
    {
        try
        {
            var directory = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            var path = Path.Combine(directory, fileName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Mirrors llama.cpp's help output closely enough to exercise the real parser:
    /// some catalogued flags are present, some are deliberately absent, and a few
    /// flags are present that the catalog does not know about.
    /// </summary>
    private static void PrintHelp()
    {
        Console.WriteLine($"usage: llama-server [options]");
        Console.WriteLine();
        Console.WriteLine($"build: {ReportedVersion.Value}");
        Console.WriteLine();
        Console.WriteLine("options:");
        Write("-h, --help", "print this help and exit");
        Write("--version", "show version and build info");
        Write("--list-devices", "list the compute devices this build can use");
        Write("-m, --model <file>", "path to the model file");
        Write("--host <ip>", "ip address to listen on (default: 127.0.0.1)");
        Write("--port <n>", "port to listen on (default: 8080)");
        Write("--alias <name>", "set the model name reported by the API");
        Write("-c, --ctx-size <n>", "size of the prompt context (default: 4096)");
        Write("-n, --n-predict <n>", "number of tokens to predict (default: -1)");
        Write("--cache-type-k <type>", "KV cache data type for K (f32, f16, bf16, q8_0, q4_0, iq4_nl)");
        Write("--cache-type-v <type>", "KV cache data type for V (f32, f16, bf16, q8_0, q4_0, iq4_nl)");
        Write("--flash-attn", "enable flash attention");
        Write("--no-kv-offload", "do not offload the KV cache to the GPU");
        Write("-ngl, --n-gpu-layers <n>", "number of layers to store in VRAM");
        Write("--no-mmap", "do not memory-map the model");
        Write("--mlock", "force the system to keep the model in RAM");
        Write("-t, --threads <n>", "number of CPU threads to use during generation");
        Write("-tb, --threads-batch <n>", "number of threads to use during batch and prompt processing");
        Write("-b, --batch-size <n>", "logical maximum batch size (default: 2048)");
        Write("-ub, --ubatch-size <n>", "physical maximum batch size (default: 512)");
        Write("-np, --parallel <n>", "number of parallel sequences to decode (default: 1)");
        Write("--cont-batching", "enable continuous batching");
        Write("--no-cont-batching", "disable continuous batching");
        Write("--model-draft <file>", "path to a draft model for speculative decoding");
        Write("--draft-max <n>", "maximum number of tokens to draft");
        Write("--draft-min <n>", "minimum number of tokens to draft");
        Write("--draft-p-min <p>", "minimum probability to accept a drafted token");
        Write("-ngld, --n-gpu-layers-draft <n>", "number of draft-model layers to store in VRAM");
        Write("-cd, --ctx-size-draft <n>", "prompt context size for the draft model");
        Write("--mtp", "enable multi-token prediction (self-speculative decoding)");
        // Present in this build but intentionally absent from the manager catalog: proves a
        // brand-new MTP switch is written out in the MTP group instead of being buried in
        // the free-form "Other" box.
        Write("--mtp-n-max <n>", "maximum number of tokens MTP may propose per step");
        Write("--jinja", "use the model's embedded chat template");
        Write("--no-webui", "disable the built-in web UI");
        Write("--embedding", "restrict to embedding mode");
        Write("--verbose", "verbose logging");
        Write("--log-verbosity <n>", "set the log verbosity (0=generic, 1=error, 2=warning, 3=info)");
        // Present in this build but unknown to the manager catalog: proves that new
        // llama.cpp switches surface as "detected" parameters without a code change.
        Write("--swa-full", "use a full-size SWA cache");
        foreach (var extra in ExtraHelpLines.Value)
        {
            Console.WriteLine("  " + extra);
        }

        Write("--mock-load-ms <n>", "mock engine only: simulated model load time in milliseconds");
        Write("--mock-fail-load", "mock engine only: simulate a load failure");
        Write("--mock-exit-on-stdin-eof <bool>", "mock engine only: exit when stdin closes (default true)");
        Write("--mock-no-console", "mock engine only: detach from the console (only a forced kill can stop it)");
        Console.WriteLine();
        Console.WriteLine("example:");
        Console.WriteLine("  llama-server -m model.gguf -c 4096 -ngl 99 --host 127.0.0.1 --port 8080");
    }

    private static void Write(string flag, string description) =>
        Console.WriteLine($"  {flag,-34} {description}");

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
}
