using System.Diagnostics;
using System.Net.Sockets;
using LocalAIModelManager.Core.Processes;

namespace LocalAIModelManager.Core.Backends;

/// <summary>
/// Shared behaviour for engines that expose an OpenAI-compatible HTTP surface:
/// process launch, readiness probing (health endpoint with a TCP fallback) and
/// graceful shutdown. Concrete adapters only translate configuration into a
/// command line and describe their own capability probe.
/// </summary>
public abstract class OpenAiCompatibleBackendAdapter : IBackendAdapter
{
    private readonly BackendJobObject? _job;

    protected OpenAiCompatibleBackendAdapter(BackendJobObject? job = null)
    {
        _job = job;
    }

    public abstract string Kind { get; }

    public abstract string DisplayName { get; }

    public abstract string DefaultExecutableFileName { get; }

    public abstract EngineCapabilities Inspect(Models.EngineDefinition engine, bool forceRefresh = false);

    public abstract LaunchPlan BuildLaunchPlan(LaunchRequest request);

    public virtual IBackendProcess StartProcess(LaunchPlan plan, BackendProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(plan.ExecutablePath))
        {
            throw new InvalidOperationException("The engine executable path is empty.");
        }

        if (!File.Exists(plan.ExecutablePath))
        {
            throw new FileNotFoundException($"Engine executable not found: {plan.ExecutablePath}", plan.ExecutablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = plan.ExecutablePath,
            WorkingDirectory = string.IsNullOrWhiteSpace(plan.WorkingDirectory)
                ? Path.GetDirectoryName(plan.ExecutablePath) ?? Environment.CurrentDirectory
                : plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = context.CaptureOutput,
            RedirectStandardError = context.CaptureOutput,
            RedirectStandardInput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (context.Environment is not null)
        {
            foreach (var (key, value) in context.Environment)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        var process = new BackendProcess(
            startInfo,
            modelId: plan.ModelId ?? "unknown",
            engineId: plan.EngineId ?? "unknown",
            bindHost: plan.BindHost,
            port: plan.Port,
            outputBufferLines: context.OutputBufferLines,
            logger: context.Logger,
            job: _job,
            controlHelperPath: context.ControlHelperPath);

        process.Start();
        return process;
    }

    public virtual async Task<BackendReadyResult> WaitUntilReadyAsync(
        IBackendProcess process,
        LaunchPlan plan,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(plan);

        var started = Stopwatch.StartNew();
        var endpoint = new BackendEndpoint
        {
            Host = plan.BindHost,
            Port = plan.Port,
            HealthPath = plan.HealthPath,
        };

        var healthUnsupported = 0;

        while (started.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!process.IsAlive)
            {
                return BackendReadyResult.Fail(
                    started.Elapsed,
                    $"The engine exited during startup (exit code {process.ExitCode?.ToString() ?? "?"}).{Environment.NewLine}{process.Tail(15)}");
            }

            if (healthUnsupported >= 3)
            {
                if (await CanConnectAsync(plan.BindHost, plan.Port, cancellationToken).ConfigureAwait(false))
                {
                    return BackendReadyResult.Ok(started.Elapsed);
                }
            }
            else
            {
                try
                {
                    using var response = await LoopbackHttp.Probe
                        .GetAsync(endpoint.HealthUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        // A 200 from /health is not enough for every engine: verify the
                        // socket is really accepting OpenAI requests before declaring ready.
                        return BackendReadyResult.Ok(started.Elapsed);
                    }

                    if ((int)response.StatusCode == 404)
                    {
                        healthUnsupported++;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Probe timeout: keep polling.
                }
            }

            try
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var reason = cancellationToken.IsCancellationRequested
            ? "Model load was cancelled."
            : $"The engine did not become ready within {timeout.TotalSeconds:0}s.";

        return BackendReadyResult.Fail(started.Elapsed, reason + Environment.NewLine + process.Tail(20));
    }

    public virtual async Task<bool> RequestGracefulShutdownAsync(
        IBackendProcess process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.IsAlive)
        {
            return true;
        }

        return await process.StopAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    public virtual IReadOnlyList<string> ValidateLaunch(LaunchPlan plan, EngineCapabilities capabilities)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(plan.ExecutablePath) || !File.Exists(plan.ExecutablePath))
        {
            problems.Add($"Engine executable not found: {plan.ExecutablePath}");
        }

        if (plan.Port is < 1 or > 65535)
        {
            problems.Add($"Invalid internal port: {plan.Port}");
        }

        if (!string.Equals(plan.BindHost, Configuration.ApiSettings.Loopback, StringComparison.Ordinal))
        {
            problems.Add($"Backend bind host must stay on {Configuration.ApiSettings.Loopback} (was '{plan.BindHost}').");
        }

        if (!capabilities.IsAvailable && capabilities.Error is not null)
        {
            problems.Add($"Engine probe failed: {capabilities.Error}");
        }

        // plan.Warnings are informational (dropped optional parameters) and are
        // deliberately not treated as hard failures.
        return problems;
    }

    protected static async Task<bool> CanConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(host, port, timeoutSource.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}
