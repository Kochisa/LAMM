namespace LocalAIModelManager.Core.Configuration;

/// <summary>Resolves where user data lives. Everything is overridable for tests.</summary>
public static class AppPaths
{
    public const string ProductFolderName = "LAMM";

    /// <summary>Folder this product used before it was renamed to LAMM.</summary>
    public const string LegacyProductFolderName = "LocalAIModelManager";

    /// <summary>Environment variable that relocates the whole configuration directory.</summary>
    public const string ConfigDirectoryEnvironmentVariable = "LAMM_CONFIG_DIR";

    public static string DefaultConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductFolderName);

    public static string LegacyConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyProductFolderName);

    public static string ResolveConfigDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ConfigDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        MigrateLegacyConfigDirectory();
        return DefaultConfigDirectory;
    }

    /// <summary>
    /// One-time move of <c>%APPDATA%\LocalAIModelManager</c> to <c>%APPDATA%\LAMM</c>, so a
    /// rename does not cost the user their model registry, API key and settings. Files are
    /// copied rather than moved: if anything goes wrong the old folder is still intact.
    /// Does nothing when the new folder already exists or there is nothing to migrate.
    /// </summary>
    public static void MigrateLegacyConfigDirectory()
    {
        try
        {
            var legacy = LegacyConfigDirectory;
            var current = DefaultConfigDirectory;

            if (!Directory.Exists(legacy) || Directory.Exists(current))
            {
                return;
            }

            Directory.CreateDirectory(current);
            foreach (var file in Directory.GetFiles(legacy))
            {
                File.Copy(file, Path.Combine(current, Path.GetFileName(file)), overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or locked profile is not fatal: the app just starts from defaults.
        }
    }

    public static string SettingsFile(string configDirectory) => Path.Combine(configDirectory, "settings.json");

    public static string ModelsFile(string configDirectory) => Path.Combine(configDirectory, "models.json");

    public static string EnginesFile(string configDirectory) => Path.Combine(configDirectory, "engines.json");

    /// <summary>Directory used only when the user explicitly saves logs.</summary>
    public static string DefaultExportDirectory(string configDirectory) => Path.Combine(configDirectory, "exports");
}
