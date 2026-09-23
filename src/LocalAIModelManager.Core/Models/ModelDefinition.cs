namespace LocalAIModelManager.Core.Models;

/// <summary>
/// A GGUF model file registered with the manager. Registering a model never
/// copies, moves or deletes the underlying file - the model file is only ever
/// referenced by absolute path.
/// </summary>
public sealed class ModelDefinition
{
    /// <summary>Stable identifier exposed through the OpenAI-compatible API as the <c>model</c> field.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human friendly name shown in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Absolute path to the model weights (typically a .gguf file).</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Id of the <see cref="EngineDefinition"/> that serves this model.</summary>
    public string EngineId { get; set; } = string.Empty;

    /// <summary>
    /// Per-model inference parameters (CLI flag -> value). EMPTY for a freshly imported
    /// model: nothing is inferred, and an unset parameter is never passed to the engine.
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Advanced engine arguments typed by hand, one CLI line each
    /// (for example <c>--some-option value</c> or a bare <c>--another-option</c>).
    /// Appended verbatim, in order, after the structured parameters.
    /// </summary>
    public List<string> AdditionalArguments { get; set; } = new();

    /// <summary>Disabled models stay registered but are not served.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Reserved for an explicit opt-in preload. Defaults to <c>false</c>: a Windows
    /// reboot must never automatically load models - everything stays in standby.
    /// </summary>
    public bool AutoLoad { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ModelDefinition Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        FilePath = FilePath,
        EngineId = EngineId,
        Parameters = new Dictionary<string, string>(Parameters, StringComparer.OrdinalIgnoreCase),
        AdditionalArguments = new List<string>(AdditionalArguments),
        Enabled = Enabled,
        AutoLoad = AutoLoad,
        Notes = Notes,
        CreatedAtUtc = CreatedAtUtc,
        UpdatedAtUtc = UpdatedAtUtc,
    };

    public void Normalize()
    {
        Id = (Id ?? string.Empty).Trim();
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName.Trim();
        FilePath = (FilePath ?? string.Empty).Trim();
        EngineId = (EngineId ?? string.Empty).Trim();
        Parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var cleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Parameters)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            cleaned[key.Trim()] = (value ?? string.Empty).Trim();
        }

        Parameters = cleaned;

        AdditionalArguments = (AdditionalArguments ?? new List<string>())
            .Select(l => (l ?? string.Empty).Trim())
            .Where(l => l.Length > 0)
            .ToList();

        // Reboot / startup policy is not user-toggleable through normalization.
        AutoLoad = false;
    }

    /// <summary>True when the model file exists on disk.</summary>
    public bool FileExists() => !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);
}
