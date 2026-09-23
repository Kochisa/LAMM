using System.Text;

namespace LocalAIModelManager.Core.Logging;

/// <summary>
/// Bounded, memory-only log buffer. Nothing is ever written to disk unless the
/// operator explicitly saves a snapshot from the Runtime Logs page.
/// </summary>
public sealed class InMemoryLogStore : IAppLogger
{
    public const int DefaultCapacity = 2000;
    public const int MaxCapacity = 200_000;

    private readonly object _gate = new();
    private LogEntry[] _buffer;
    private int _head;
    private int _count;
    private long _sequence;
    private long _totalWritten;
    private long _dropped;

    public InMemoryLogStore(int capacity = DefaultCapacity, LogLevel minimumLevel = LogLevel.Information)
    {
        _buffer = new LogEntry[Math.Clamp(capacity, 1, MaxCapacity)];
        MinimumLevel = minimumLevel;
        Redactor = new LogRedactor();
    }

    /// <summary>Raised for every accepted entry; the UI subscribes to append live rows.</summary>
    public event Action<LogEntry>? EntryAdded;

    /// <summary>Raised when the buffer is cleared.</summary>
    public event Action? Cleared;

    public LogRedactor Redactor { get; }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>When paused, new entries are still counted but not retained.</summary>
    public bool IsPaused { get; set; }

    public int Capacity
    {
        get
        {
            lock (_gate)
            {
                return _buffer.Length;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public long TotalWritten
    {
        get
        {
            lock (_gate)
            {
                return _totalWritten;
            }
        }
    }

    public long DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    public bool IsEnabled(LogLevel level) => level >= MinimumLevel;

    public void SetCapacity(int capacity)
    {
        capacity = Math.Clamp(capacity, 1, MaxCapacity);
        lock (_gate)
        {
            if (capacity == _buffer.Length)
            {
                return;
            }

            var existing = SnapshotCore();
            var replacement = new LogEntry[capacity];
            var keep = Math.Min(capacity, existing.Count);
            for (var i = 0; i < keep; i++)
            {
                replacement[i] = existing[existing.Count - keep + i];
            }

            _buffer = replacement;
            _head = keep % capacity;
            _count = keep;
            if (existing.Count > keep)
            {
                _dropped += existing.Count - keep;
            }
        }
    }

    public void Log(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < MinimumLevel || IsPaused)
        {
            return;
        }

        var entry = new LogEntry
        {
            Sequence = Interlocked.Increment(ref _sequence),
            Timestamp = DateTimeOffset.Now,
            Level = level,
            Category = string.IsNullOrWhiteSpace(category) ? "app" : category,
            Message = Redactor.Redact(message),
            Exception = exception is null ? null : Redactor.Redact(exception.ToString()),
        };

        lock (_gate)
        {
            _totalWritten++;
            if (_count == _buffer.Length)
            {
                _dropped++;
            }

            _buffer[_head] = entry;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _count++;
            }
        }

        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return SnapshotCore();
        }
    }

    /// <summary>Filtered view used by the Runtime Logs page search box.</summary>
    public IReadOnlyList<LogEntry> Query(string? search = null, LogLevel? minimumLevel = null, string? category = null, int limit = 0)
    {
        var all = Snapshot();
        IEnumerable<LogEntry> filtered = all;

        if (minimumLevel is { } min)
        {
            filtered = filtered.Where(e => e.Level >= min);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            filtered = filtered.Where(e => e.Category.Contains(category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(e =>
                e.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = filtered.ToList();
        if (limit > 0 && list.Count > limit)
        {
            list = list.Skip(list.Count - limit).ToList();
        }

        return list;
    }

    public IReadOnlyDictionary<LogLevel, int> LevelCounts()
    {
        var snapshot = Snapshot();
        var counts = new Dictionary<LogLevel, int>();
        foreach (var level in Enum.GetValues<LogLevel>())
        {
            counts[level] = snapshot.Count(e => e.Level == level);
        }

        return counts;
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
            _head = 0;
            _count = 0;
        }

        Cleared?.Invoke();
    }

    /// <summary>Explicit, user initiated export. This is the only path that touches disk.</summary>
    public async Task<int> SaveToFileAsync(string path, string? search = null, LogLevel? minimumLevel = null, CancellationToken cancellationToken = default)
    {
        var entries = Query(search, minimumLevel);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"# LAMM log snapshot");
        builder.AppendLine($"# exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"# entries: {entries.Count}");
        builder.AppendLine();
        foreach (var entry in entries)
        {
            builder.AppendLine(entry.ToString());
        }

        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return entries.Count;
    }

    private List<LogEntry> SnapshotCore()
    {
        var result = new List<LogEntry>(_count);
        if (_count == 0)
        {
            return result;
        }

        var start = _count == _buffer.Length ? _head : 0;
        for (var i = 0; i < _count; i++)
        {
            var entry = _buffer[(start + i) % _buffer.Length];
            if (entry is not null)
            {
                result.Add(entry);
            }
        }

        return result;
    }
}
