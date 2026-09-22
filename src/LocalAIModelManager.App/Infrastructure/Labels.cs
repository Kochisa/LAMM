using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.App.Infrastructure;

/// <summary>Human readable Chinese labels for the core enums.</summary>
public static class Labels
{
    public static string ModelState(ModelState state) => state switch
    {
        LocalAIModelManager.Core.Models.ModelState.Standby => "待机",
        LocalAIModelManager.Core.Models.ModelState.Starting => "启动中",
        LocalAIModelManager.Core.Models.ModelState.Loading => "加载中",
        LocalAIModelManager.Core.Models.ModelState.Ready => "就绪",
        LocalAIModelManager.Core.Models.ModelState.Stopping => "停止中",
        LocalAIModelManager.Core.Models.ModelState.Failed => "失败",
        _ => state.ToString(),
    };

    public static string StopMode(BackendStopMode mode) => mode switch
    {
        BackendStopMode.Graceful => "优雅退出",
        BackendStopMode.ForcedKill => "强制结束",
        BackendStopMode.Crashed => "异常退出",
        _ => "—",
    };

    public static string ParameterKindLabel(ParameterKind kind) => kind switch
    {
        Core.Backends.ParameterKind.Integer => "整数",
        Core.Backends.ParameterKind.Float => "小数",
        Core.Backends.ParameterKind.Boolean => "开关",
        Core.Backends.ParameterKind.Enum => "枚举",
        _ => "文本",
    };
}
