using LocalAIModelManager.Core.Configuration;

namespace LocalAIModelManager.App.ViewModels.Settings;

public enum SettingFieldKind
{
    Text,
    Number,
    Bool,
    Choice,
    Secret,
    Folder,
    File,
    ReadOnly,
}

/// <summary>
/// Declarative description of one configurable value. The settings pages are built
/// from these descriptors, which is why eight separate pages need one view instead
/// of eight hand-written forms.
/// </summary>
public sealed record SettingField
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public string? Help { get; init; }

    public SettingFieldKind Kind { get; init; } = SettingFieldKind.Text;

    public required Func<AppSettings, string> Read { get; init; }

    public Action<AppSettings, string>? Write { get; init; }

    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    public string? Suffix { get; init; }

    /// <summary>Minimum / maximum for numeric fields (inclusive).</summary>
    public long? Min { get; init; }

    public long? Max { get; init; }

    /// <summary>Marks the value as security relevant; rendered with an accent.</summary>
    public bool SecurityRelevant { get; init; }

    /// <summary>Value that must never be written back (enforced invariants).</summary>
    public bool Enforced { get; init; }
}

/// <summary>One settings page: a section of <see cref="AppSettings"/>.</summary>
public sealed record SettingsSection
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required IReadOnlyList<SettingField> Fields { get; init; }

    /// <summary>Extra side effects applied after the values are persisted.</summary>
    public Action<Services.AppServices, AppSettings>? OnSaved { get; init; }

    /// <summary>Additional notes rendered at the bottom of the page.</summary>
    public Func<Services.AppServices, IEnumerable<string>>? Notes { get; init; }

    /// <summary>True when changing this section requires the API gateway to be restarted.</summary>
    public bool RequiresGatewayRestart { get; init; }
}
