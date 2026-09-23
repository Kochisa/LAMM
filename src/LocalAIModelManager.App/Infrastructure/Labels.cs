using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.App.Infrastructure;

/// <summary>Human readable labels for the core enums, localized on every access.</summary>
public static class Labels
{
    public static string ModelState(ModelState state) => state switch
    {
        LocalAIModelManager.Core.Models.ModelState.Standby => Loc.T("label.state.standby"),
        LocalAIModelManager.Core.Models.ModelState.Starting => Loc.T("label.state.starting"),
        LocalAIModelManager.Core.Models.ModelState.Loading => Loc.T("label.state.loading"),
        LocalAIModelManager.Core.Models.ModelState.Ready => Loc.T("label.state.ready"),
        LocalAIModelManager.Core.Models.ModelState.Stopping => Loc.T("label.state.stopping"),
        LocalAIModelManager.Core.Models.ModelState.Failed => Loc.T("label.state.failed"),
        _ => state.ToString(),
    };

    public static string StopMode(BackendStopMode mode) => mode switch
    {
        BackendStopMode.Graceful => Loc.T("label.stop.graceful"),
        BackendStopMode.ForcedKill => Loc.T("label.stop.forced"),
        BackendStopMode.Crashed => Loc.T("label.stop.crashed"),
        _ => Loc.T("label.stop.none"),
    };

    public static string ParameterKindLabel(ParameterKind kind) => kind switch
    {
        Core.Backends.ParameterKind.Integer => Loc.T("label.kind.integer"),
        Core.Backends.ParameterKind.Float => Loc.T("label.kind.float"),
        Core.Backends.ParameterKind.Boolean => Loc.T("label.kind.boolean"),
        Core.Backends.ParameterKind.Enum => Loc.T("label.kind.enum"),
        _ => Loc.T("label.kind.text"),
    };
}
