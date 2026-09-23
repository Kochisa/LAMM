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
///
/// Groups and titles are produced by <see cref="BuildPages"/> on every access, so a
/// runtime language switch is reflected the next time the shell reads the catalog.
/// The group constants are stable, language neutral ids; the localized group display
/// name comes from the <c>nav.group.*</c> keys.
/// </summary>
public static class PageCatalog
{
    public const string GroupModels = "models";
    public const string GroupRuntime = "runtime";
    public const string GroupIntegration = "integration";
    public const string GroupSettings = "settings";

    public static IReadOnlyList<PageDescriptor> Pages => BuildPages();

    public static IReadOnlyList<string> Groups => new[]
    {
        GroupModels, GroupRuntime, GroupIntegration, GroupSettings,
    };

    /// <summary>Localized display name of a navigation group id.</summary>
    public static string GroupTitle(string group) => group switch
    {
        GroupModels => Loc.T("nav.group.models"),
        GroupRuntime => Loc.T("nav.group.runtime"),
        GroupIntegration => Loc.T("nav.group.integration"),
        GroupSettings => Loc.T("nav.group.settings"),
        _ => group,
    };

    private static IReadOnlyList<PageDescriptor> BuildPages() => new List<PageDescriptor>
    {
        new(
            "models", GroupModels, Loc.T("nav.page.models"), "▣",
            services => new ModelsViewModel(services),
            vm => new ModelsView { DataContext = vm }),

        new(
            "runtime-status", GroupRuntime, Loc.T("nav.page.runtimeStatus"), "◉",
            services => new RuntimeStatusViewModel(services),
            vm => new RuntimeStatusView { DataContext = vm }),

        new(
            "runtime-logs", GroupRuntime, Loc.T("nav.page.runtimeLogs"), "☰",
            services => new RuntimeLogsViewModel(services),
            vm => new RuntimeLogsView { DataContext = vm }),

        new(
            "api-integration", GroupIntegration, Loc.T("nav.page.apiIntegration"), "⇄",
            services => new ApiIntegrationViewModel(services),
            vm => new ApiIntegrationView { DataContext = vm }),

        // ---- dedicated settings pages ----------------------------------------
        new(
            "settings-general", GroupSettings, Loc.T("nav.page.settingsGeneral"), "⚙",
            services => new SettingsPageViewModel(services, SettingsSections.Get(SettingsSections.General)),
            vm => new SettingsPageView { DataContext = vm }),

        new(
            "settings-api", GroupSettings, Loc.T("nav.page.settingsApi"), "⇱",
            services => new SettingsPageViewModel(services, SettingsSections.Get(SettingsSections.Api)),
            vm => new SettingsPageView { DataContext = vm }),

        new(
            "settings-engine", GroupSettings, Loc.T("nav.page.settingsEngine"), "⛭",
            services => new InferenceEngineViewModel(services),
            vm => new InferenceEngineView { DataContext = vm }),

        new(
            "settings-model-parameters", GroupSettings, Loc.T("nav.page.settingsModelParameters"), "⚗",
            services => new ModelParametersViewModel(services),
            vm => new ModelParametersView { DataContext = vm }),

        new(
            "settings-lifecycle", GroupSettings, Loc.T("nav.page.settingsLifecycle"), "⏱",
            services => new SettingsPageViewModel(services, SettingsSections.Get(SettingsSections.Lifecycle)),
            vm => new SettingsPageView { DataContext = vm }),

        new(
            "settings-resources", GroupSettings, Loc.T("nav.page.settingsResources"), "▤",
            services => new SettingsPageViewModel(services, SettingsSections.Get(SettingsSections.Resources)),
            vm => new SettingsPageView { DataContext = vm }),

        new(
            "settings-network", GroupSettings, Loc.T("nav.page.settingsNetwork"), "⛁",
            services => new SettingsPageViewModel(services, SettingsSections.Get(SettingsSections.Network)),
            vm => new SettingsPageView { DataContext = vm }),

        new(
            "settings-advanced", GroupSettings, Loc.T("nav.page.settingsAdvanced"), "⚑",
            services => new SettingsPageViewModel(services, SettingsSections.Get(SettingsSections.Advanced)),
            vm => new SettingsPageView { DataContext = vm }),
    };
}
