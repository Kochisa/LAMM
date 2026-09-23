using LocalAIModelManager.Core.Backends;

namespace LocalAIModelManager.App.ViewModels.Settings;

/// <summary>
/// Builds the parameter rows shown outside the free-form "Other" box.
///
/// The common group is a fixed, short list (<see cref="EssentialParameters"/>). MTP and
/// speculative decoding get their own group instead of being hidden in "Other", because
/// that family is the one people actually reach for on a modern build - and its flags
/// differ between builds, so the group is assembled from what the probe found
/// (<see cref="ParameterCatalog.SpeculativeFor"/>).
/// </summary>
public static class ParameterRowFactory
{
    /// <summary>
    /// One row for <paramref name="descriptor"/>. <paramref name="configuredValue"/> is the
    /// value that is actually stored for this scope, or <c>null</c> when nothing was ever
    /// configured - and "nothing configured" must stay "nothing sent", so the row comes up
    /// switched off with an empty value.
    /// </summary>
    public static ParameterRowViewModel Create(
        ParameterDescriptor descriptor,
        EngineCapabilities capabilities,
        string? configuredValue)
    {
        var row = new ParameterRowViewModel(
            descriptor,
            capabilities.Supports(descriptor.Key),
            descriptor.DefaultValue);

        if (configuredValue is not null)
        {
            row.IsEnabled = true;
            row.Value = configuredValue;
        }
        else
        {
            row.IsEnabled = false;
            row.Value = descriptor.Kind == ParameterKind.Boolean ? "false" : string.Empty;
        }

        return row;
    }
}
