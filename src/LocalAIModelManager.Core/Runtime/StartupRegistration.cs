using Microsoft.Win32;

namespace LocalAIModelManager.Core.Runtime;

/// <summary>
/// Windows "Start with Windows" support through the per-user Run key.
/// The key path is injectable so tests can exercise the mechanism without
/// touching the real startup configuration.
/// </summary>
public static class StartupRegistration
{
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string DefaultValueName = "LAMM";

    /// <summary>Run-key value name this product used before it was renamed to LAMM.</summary>
    public const string LegacyValueName = "LocalAIModelManager";

    /// <summary>Reads the registered command line, or null when startup is not configured.</summary>
    public static string? GetCommand(string valueName = DefaultValueName, string keyPath = DefaultRunKeyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        var command = key?.GetValue(valueName) as string;
        if (!string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        // An entry written before the rename still means "start with Windows", so report
        // it rather than silently showing the feature as off.
        return string.Equals(valueName, DefaultValueName, StringComparison.Ordinal)
            ? key?.GetValue(LegacyValueName) as string
            : null;
    }

    public static bool IsEnabled(string valueName = DefaultValueName, string keyPath = DefaultRunKeyPath) =>
        !string.IsNullOrWhiteSpace(GetCommand(valueName, keyPath));

    /// <summary>
    /// True when this process may actually modify the Run key. Returns false in
    /// restricted environments (locked-down HKCU, sandboxes) so the UI can explain
    /// the failure instead of throwing.
    /// </summary>
    public static bool IsWritable(string keyPath = DefaultRunKeyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            return key is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Non-throwing variant used by the settings UI.</summary>
    public static bool TrySetEnabled(
        bool enabled,
        string executablePath,
        string arguments,
        out string? error,
        string valueName = DefaultValueName,
        string keyPath = DefaultRunKeyPath)
    {
        try
        {
            SetEnabled(enabled, executablePath, arguments, valueName, keyPath);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Registers or removes the startup entry.</summary>
    public static void SetEnabled(
        bool enabled,
        string executablePath,
        string arguments,
        string valueName = DefaultValueName,
        string keyPath = DefaultRunKeyPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKCU\\{keyPath} for writing.");

        // Never leave two entries behind: the pre-rename one launched a binary that no
        // longer exists and would fight with this one.
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

        if (!enabled)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
            return;
        }

        var command = string.IsNullOrWhiteSpace(arguments)
            ? Quote(executablePath)
            : $"{Quote(executablePath)} {arguments}";

        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    /// <summary>
    /// Rewrites a pre-rename startup entry so it launches <paramref name="executablePath"/>
    /// instead of the old binary, which no longer exists after the rename. No-op when
    /// startup was never registered; returns true when an entry was migrated.
    /// </summary>
    public static bool TryMigrateLegacyEntry(string executablePath, string arguments)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(DefaultRunKeyPath, writable: false))
            {
                if (string.IsNullOrWhiteSpace(key?.GetValue(LegacyValueName) as string))
                {
                    return false;
                }
            }

            SetEnabled(true, executablePath, arguments);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Command line used for the startup entry. Deliberately free of any
    /// "load models" switch: the manager always starts with all models in standby.
    /// </summary>
    public static string BuildStartupArguments(bool startMinimized) =>
        startMinimized ? "--startup --minimized" : "--startup";

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}
