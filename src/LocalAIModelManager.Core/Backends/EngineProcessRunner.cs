using System.Diagnostics;
using System.Text;

namespace LocalAIModelManager.Core.Backends;

public sealed record ProcessRunResult
{
    public required int ExitCode { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }

    public required bool TimedOut { get; init; }

    public string CombinedOutput => string.IsNullOrEmpty(StandardError)
        ? StandardOutput
        : string.IsNullOrEmpty(StandardOutput)
            ? StandardError
            : StandardOutput + Environment.NewLine + StandardError;
}

/// <summary>
/// Runs short-lived helper invocations of an engine binary (<c>--help</c>,
/// <c>--version</c>) to discover what the installed build actually supports.
/// </summary>
public static class EngineProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(
        string executablePath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        int exitCode;
        try
        {
            exitCode = process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        return new ProcessRunResult
        {
            ExitCode = exitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
            TimedOut = timedOut,
        };
    }

    /// <summary>Cheap identity for cache invalidation: a replaced binary must be re-probed.</summary>
    public static string? Fingerprint(string executablePath)
    {
        try
        {
            var info = new FileInfo(executablePath);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
