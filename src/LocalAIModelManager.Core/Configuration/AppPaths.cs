namespace LocalAIModelManager.Core.Configuration;

/// <summary>Resolves where user data lives. Everything is overridable for tests.</summary>
public static class AppPaths
{
    public const string ProductFolderName = "LocalAIModelManager";

    /// <summary>Environment variable that relocates the whole configuration directory.</summary>
    public const string ConfigDirectoryEnvironmentVariable = "LAMM_CONFIG_DIR";

    public static string DefaultConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductFolderName);

    public static string ResolveConfigDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ConfigDirectoryEnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? DefaultConfigDirectory : fromEnvironment.Trim();
    }

    public static string SettingsFile(string configDirectory) => Path.Combine(configDirectory, "settings.json");

    public static string ModelsFile(string configDirectory) => Path.Combine(configDirectory, "models.json");

    public static string EnginesFile(string configDirectory) => Path.Combine(configDirectory, "engines.json");

    /// <summary>Directory used only when the user explicitly saves logs.</summary>
    public static string DefaultExportDirectory(string configDirectory) => Path.Combine(configDirectory, "exports");
}
