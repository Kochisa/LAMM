namespace LocalAIModelManager.Core.Logging;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}

/// <summary>One in-memory log record. Logs are never written to disk implicitly.</summary>
public sealed record LogEntry
{
    public required long Sequence { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required LogLevel Level { get; init; }

    public required string Category { get; init; }

    public required string Message { get; init; }

    public string? Exception { get; init; }

    public override string ToString() =>
        $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level,-11}] {Category}: {Message}" +
        (string.IsNullOrEmpty(Exception) ? string.Empty : Environment.NewLine + Exception);
}

public interface IAppLogger
{
    bool IsEnabled(LogLevel level);

    void Log(LogLevel level, string category, string message, Exception? exception = null);
}

public static class AppLoggerExtensions
{
    public static void Trace(this IAppLogger logger, string category, string message) =>
        logger.Log(LogLevel.Trace, category, message);

    public static void Debug(this IAppLogger logger, string category, string message) =>
        logger.Log(LogLevel.Debug, category, message);

    public static void Info(this IAppLogger logger, string category, string message) =>
        logger.Log(LogLevel.Information, category, message);

    public static void Warn(this IAppLogger logger, string category, string message, Exception? exception = null) =>
        logger.Log(LogLevel.Warning, category, message, exception);

    public static void Error(this IAppLogger logger, string category, string message, Exception? exception = null) =>
        logger.Log(LogLevel.Error, category, message, exception);
}

/// <summary>Discards everything. Useful in unit tests that do not care about logs.</summary>
public sealed class NullAppLogger : IAppLogger
{
    public static readonly NullAppLogger Instance = new();

    public bool IsEnabled(LogLevel level) => false;

    public void Log(LogLevel level, string category, string message, Exception? exception = null)
    {
    }
}
