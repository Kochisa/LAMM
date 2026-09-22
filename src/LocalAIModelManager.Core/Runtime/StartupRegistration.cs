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

    public const string DefaultValueName = "LocalAIModelManager";

    /// <summary>Reads the registered command line, or null when startup is not configured.</summary>
    public static string? GetCommand(string valueName = DefaultValueName, string keyPath = DefaultRunKeyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName) as string;
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
    /// Command line used for the startup entry. Deliberately free of any
    /// "load models" switch: the manager always starts with all models in standby.
    /// </summary>
    public static string BuildStartupArguments(bool startMinimized) =>
        startMinimized ? "--startup --minimized" : "--startup";

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}
