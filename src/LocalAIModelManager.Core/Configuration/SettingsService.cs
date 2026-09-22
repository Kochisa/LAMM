namespace LocalAIModelManager.Core.Configuration;

/// <summary>
/// Owns the mutable <see cref="AppSettings"/> instance used across the process.
/// Consumers always read through <see cref="Current"/> so a settings save is
/// immediately visible everywhere.
/// </summary>
public sealed class SettingsService
{
    private readonly JsonFileStore<AppSettings> _store;
    private readonly object _gate = new();
    private AppSettings _current;

    public SettingsService(string configDirectory)
    {
        ConfigDirectory = configDirectory;
        _store = new JsonFileStore<AppSettings>(AppPaths.SettingsFile(configDirectory));
        _current = _store.LoadOrCreate();
        _current.Normalize();
        _store.Save(_current);
    }

    public string ConfigDirectory { get; }

    public string SettingsFilePath => _store.Path;

    /// <summary>Raised after settings are persisted.</summary>
    public event Action<AppSettings>? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Returns a detached copy safe to mutate on a background thread.</summary>
    public AppSettings Snapshot()
    {
        lock (_gate)
        {
            return _current.Clone();
        }
    }

    /// <summary>Applies a mutation and persists the result.</summary>
    public void Update(Action<AppSettings> mutate, bool persist = true)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        AppSettings snapshot;
        lock (_gate)
        {
            mutate(_current);
            _current.Normalize();
            snapshot = _current;
        }

        if (persist)
        {
            _store.Save(snapshot);
        }

        Changed?.Invoke(snapshot);
    }

    public void Reload()
    {
        AppSettings loaded;
        lock (_gate)
        {
            loaded = _store.LoadOrCreate();
            loaded.Normalize();
            _current = loaded;
            _store.Save(_current);
        }

        Changed?.Invoke(loaded);
    }

    public IReadOnlyList<ValidationIssue> Validate() => Current.Validate();
}
