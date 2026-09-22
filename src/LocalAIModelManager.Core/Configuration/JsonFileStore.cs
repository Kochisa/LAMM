using System.Text.Json;

namespace LocalAIModelManager.Core.Configuration;

/// <summary>
/// Crash-safe JSON document store: writes go to a temporary file first and are then
/// atomically swapped in, and a corrupt document is preserved (not silently dropped)
/// as <c>*.corrupt-&lt;timestamp&gt;</c> before defaults are returned.
/// </summary>
public sealed class JsonFileStore<T>
    where T : class, new()
{
    private readonly object _gate = new();
    private readonly Func<T> _defaultFactory;

    public JsonFileStore(string path, Func<T>? defaultFactory = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        _defaultFactory = defaultFactory ?? (() => new T());
    }

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    /// <summary>Raised after a successful save, with the absolute path written.</summary>
    public event Action<string>? Saved;

    public T Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    public T LoadOrCreate()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                var created = _defaultFactory();
                SaveCore(created);
                return created;
            }

            return LoadCore();
        }
    }

    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            SaveCore(value);
        }
    }

    /// <summary>Applies a mutation to the persisted document under a single lock.</summary>
    public TResult Update<TResult>(Func<T, TResult> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var current = File.Exists(Path) ? LoadCore() : _defaultFactory();
            var result = mutate(current);
            SaveCore(current);
            return result;
        }
    }

    private T LoadCore()
    {
        if (!File.Exists(Path))
        {
            return _defaultFactory();
        }

        try
        {
            var text = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return _defaultFactory();
            }

            return JsonSerializer.Deserialize<T>(text, JsonSerialization.Options) ?? _defaultFactory();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            TryQuarantineCorruptFile();
            return _defaultFactory();
        }
    }

    private void SaveCore(T value)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path + ".tmp";
        var json = JsonSerializer.Serialize(value, JsonSerialization.Options);
        File.WriteAllText(temp, json);

        if (File.Exists(Path))
        {
            File.Replace(temp, Path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, Path);
        }

        Saved?.Invoke(Path);
    }

    private void TryQuarantineCorruptFile()
    {
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var target = $"{Path}.corrupt-{stamp}";
            File.Move(Path, target, overwrite: true);
        }
        catch (IOException)
        {
            // Best effort only - we still return defaults so the app can start.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
