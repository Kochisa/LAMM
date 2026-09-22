using System.Windows;
using LocalAIModelManager.App.ViewModels;
using LocalAIModelManager.App.ViewModels.Settings;
using LocalAIModelManager.App.Views;
using LocalAIModelManager.App.Views.Settings;

namespace LocalAIModelManager.App.Infrastructure;

/// <summary>One navigable page: its view model factory and its view factory.</summary>
public sealed record PageDescriptor(
    string Key,
    string Group,
    string Title,
    string Glyph,
    Func<Services.AppServices, PageViewModelBase> CreateViewModel,
    Func<PageViewModelBase, FrameworkElement> CreateView);

/// <summary>
/// The navigation registry. Adding a page means adding one entry here - the shell
/// itself knows nothing about specific pages.
/// </summary>
public static class PageCatalog
{
    public const string GroupModels = "模型";
    public const string GroupRuntime = "运行时";
    public const string GroupIntegration = "集成";
    public const string GroupSettings = "设置";

    public static IReadOnlyList<PageDescriptor> Pages { get; } = new List<PageDescriptor>
    {
        new(
            "models", GroupModels, "模型", "▣",
            services => new ModelsViewModel(services),
            vm => new ModelsView { DataContext = vm }),

        new(
            "runtime-status", GroupRuntime, "运行状态", "◉",
            services => new RuntimeStatusViewModel(services),
            vm => new RuntimeStatusView { DataContext = vm }),

        new(
            "runtime-logs", GroupRuntime, "运行日志", "☰",
            services => new RuntimeLogsViewModel(services),
            vm => new RuntimeLogsView { DataContext = vm }),

        new(
            "api-integration", GroupIntegration, "API 集成", "⇄",
            services => new ApiIntegrationViewModel(services),
            vm => new ApiIntegrationView { DataContext = vm }),

        // ---- dedicated settings pages ----------------------------------------
        new(
            "settings-general", GroupSettings, "常规", "⚙",
            services => new SettingsPageViewModel(services, SettingsSections.Get(SettingsSections.General)),
            vm => new SettingsPageView { DataContext = vm }),

        new(
            "settings-api", GroupSettings, "API", "⇱",
            services => new SettingsPageViewModel(services, SettingsSections.Get(SettingsSections.Api)),
            vm => new SettingsPageView { DataContext = vm }),

        new(
            "settings-engine", GroupSettings, "推理引擎", "⛭",
            services => new InferenceEngineViewModel(services),
            vm => new InferenceEngineView { DataContext = vm }),

        new(
            "settings-model-parameters", GroupSettings, "模型参数", "⚗",
            services => new ModelParametersViewModel(services),
            vm => new ModelParametersView { DataContext = vm }),

        new(
            "settings-lifecycle", GroupSettings, "生命周期", "⏱",
            services => new SettingsPageViewModel(services, SettingsSections.Get(SettingsSections.Lifecycle)),
            vm => new SettingsPageView { DataContext = vm }),

        new(
            "settings-resources", GroupSettings, "资源", "▤",
            services => new SettingsPageViewModel(services, SettingsSections.Get(SettingsSections.Resources)),
            vm => new SettingsPageView { DataContext = vm }),

        new(
            "settings-network", GroupSettings, "网络", "⛁",
            services => new SettingsPageViewModel(services, SettingsSections.Get(SettingsSections.Network)),
            vm => new SettingsPageView { DataContext = vm }),

        new(
            "settings-advanced", GroupSettings, "高级", "⚑",
            services => new SettingsPageViewModel(services, SettingsSections.Get(SettingsSections.Advanced)),
            vm => new SettingsPageView { DataContext = vm }),
    };

    public static IReadOnlyList<string> Groups { get; } = new[]
    {
        GroupModels, GroupRuntime, GroupIntegration, GroupSettings,
    };
}
