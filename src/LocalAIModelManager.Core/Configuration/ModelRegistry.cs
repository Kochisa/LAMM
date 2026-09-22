using System.Text.RegularExpressions;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.Core.Configuration;

public sealed class ModelRegistryDocument
{
    public int SchemaVersion { get; set; } = 1;

    public List<ModelDefinition> Models { get; set; } = new();
}

/// <summary>
/// The model catalog. Registration is metadata-only: deleting an entry never
/// deletes the underlying weights file.
/// </summary>
public sealed partial class ModelRegistry
{
    private readonly JsonFileStore<ModelRegistryDocument> _store;
    private readonly object _gate = new();
    private List<ModelDefinition> _models = new();

    public ModelRegistry(string path)
    {
        _store = new JsonFileStore<ModelRegistryDocument>(path);
        Reload();
    }

    public string Path => _store.Path;

    /// <summary>Raised whenever the catalog changes (add / update / remove / rename).</summary>
    public event Action? Changed;

    public void Reload()
    {
        lock (_gate)
        {
            var document = _store.LoadOrCreate();
            _models = document.Models ?? new List<ModelDefinition>();
            foreach (var model in _models)
            {
                model.Normalize();
            }

            _models = _models
                .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<ModelDefinition> All
    {
        get
        {
            lock (_gate)
            {
                return _models.Select(m => m.Clone()).ToList();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _models.Count;
            }
        }
    }

    public ModelDefinition? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (_gate)
        {
            var match = _models.FirstOrDefault(m => string.Equals(m.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            return match?.Clone();
        }
    }

    public bool Exists(string id) => Get(id) is not null;

    public IReadOnlyList<string> Validate(ModelDefinition model)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Id))
        {
            errors.Add("Model ID is required.");
        }
        else if (!IdPattern().IsMatch(model.Id))
        {
            errors.Add("Model ID may only contain letters, digits, dot, dash and underscore (1-96 characters).");
        }
        else
        {
            lock (_gate)
            {
                if (_models.Any(m => string.Equals(m.Id, model.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"Model ID '{model.Id}' is already registered.");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(model.FilePath))
        {
            errors.Add("Model file path is required.");
        }
        else if (!File.Exists(model.FilePath))
        {
            errors.Add($"Model file not found: {model.FilePath}");
        }

        if (string.IsNullOrWhiteSpace(model.EngineId))
        {
            errors.Add("Select an inference engine for this model.");
        }

        return errors;
    }

    /// <summary>Adds a model. Returns the stored definition, or throws when validation fails.</summary>
    public ModelDefinition Add(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var copy = model.Clone();
        copy.Normalize();

        var errors = Validate(copy);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }

        copy.CreatedAtUtc = DateTimeOffset.UtcNow;
        copy.UpdatedAtUtc = copy.CreatedAtUtc;

        Mutate(list => list.Add(copy));
        return copy;
    }

    /// <summary>Updates an existing model in place. The Id is immutable through this call.</summary>
    public bool Update(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var copy = model.Clone();
        copy.Normalize();
        copy.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var changed = false;
        Mutate(list =>
        {
            var index = list.FindIndex(m => string.Equals(m.Id, copy.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                copy.CreatedAtUtc = list[index].CreatedAtUtc;
                list[index] = copy;
                changed = true;
            }
        });

        return changed;
    }

    /// <summary>Renames the display name and (optionally) the API facing model id.</summary>
    public bool Rename(string currentId, string newDisplayName, string? newId = null)
    {
        var changed = false;
        Mutate(list =>
        {
            var index = list.FindIndex(m => string.Equals(m.Id, currentId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            var target = list[index];
            target.DisplayName = (newDisplayName ?? string.Empty).Trim();

            var candidateId = (newId ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(candidateId) &&
                !string.Equals(candidateId, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (!IdPattern().IsMatch(candidateId))
                {
                    throw new InvalidOperationException("Model ID may only contain letters, digits, dot, dash and underscore.");
                }

                if (list.Any(m => !ReferenceEquals(m, target) &&
                                  string.Equals(m.Id, candidateId, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Model ID '{candidateId}' is already registered.");
                }

                target.Id = candidateId;
            }

            if (string.IsNullOrWhiteSpace(target.DisplayName))
            {
                target.DisplayName = target.Id;
            }

            target.UpdatedAtUtc = DateTimeOffset.UtcNow;
            changed = true;
        });

        return changed;
    }

    /// <summary>
    /// Removes a model entry from the catalog. The weights file on disk is left
    /// untouched - this is a deliberate product guarantee.
    /// </summary>
    public bool Remove(string id)
    {
        var removed = false;
        Mutate(list =>
        {
            var index = list.FindIndex(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                list.RemoveAt(index);
                removed = true;
            }
        });

        return removed;
    }

    public void ReplaceAll(IEnumerable<ModelDefinition> models)
    {
        var list = models.Select(m =>
        {
            var copy = m.Clone();
            copy.Normalize();
            return copy;
        }).ToList();

        Mutate(target =>
        {
            target.Clear();
            target.AddRange(list);
        });
    }

    private void Mutate(Action<List<ModelDefinition>> mutate)
    {
        lock (_gate)
        {
            mutate(_models);
            var document = new ModelRegistryDocument { Models = _models };
            _store.Save(document);
        }

        Changed?.Invoke();
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,96}$")]
    private static partial Regex IdPattern();
}
