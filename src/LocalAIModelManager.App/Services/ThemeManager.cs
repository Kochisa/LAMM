using System.Windows;

namespace LocalAIModelManager.App.Services;

/// <summary>
/// Swaps the palette resource dictionary at runtime. Control styles use
/// DynamicResource, so a theme change is applied without restarting.
/// </summary>
public static class ThemeManager
{
    private const string DarkSource = "Themes/Dark.xaml";
    private const string LightSource = "Themes/Light.xaml";

    public static string Current { get; private set; } = "Dark";

    public static void Apply(string? theme)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var wanted = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        var source = wanted == "Light" ? LightSource : DarkSource;
        var dictionaries = application.Resources.MergedDictionaries;

        var existing = dictionaries.FirstOrDefault(d =>
            d.Source is { } uri &&
            (uri.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
             uri.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase)));

        var replacement = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

        if (existing is not null)
        {
            var index = dictionaries.IndexOf(existing);
            dictionaries[index] = replacement;
        }
        else
        {
            dictionaries.Insert(0, replacement);
        }

        Current = wanted;

        // The native title bar is not part of the palette, so it has to be repainted
        // whenever the palette changes, otherwise the frame keeps the previous colours.
        Infrastructure.WindowChrome.ApplyToOpenWindows();
    }
}
