using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LocalAIModelManager.App.Infrastructure;

/// <summary>
/// Paints the native window frame (title bar, caption text, 1px window border) with the
/// same colours as the application palette.
///
/// Without this, Windows draws the caption using the system theme - a light grey bar with
/// black text - while the window interior follows our dark (or light) palette, so the two
/// never match. The attributes used here require Windows 11 (build 22000+) for the colour
/// overrides; the dark-mode flag works on Windows 10 1809+. Failures are ignored so the
/// application still runs on older systems, just with the system caption.
/// </summary>
public static class WindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    /// <summary>
    /// Registers a class handler so every window - including ones created later, such as
    /// the editor and prompt dialogs - gets the palette applied as soon as it is loaded.
    /// </summary>
    public static void HookAllWindows()
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window)
                {
                    Apply(window);
                }
            }));
    }

    /// <summary>Re-applies the palette to every open window, for example after a theme change.</summary>
    public static void ApplyToOpenWindows()
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        foreach (Window window in application.Windows)
        {
            Apply(window);
        }
    }

    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var caption = ResourceColor("App.Background", Color.FromRgb(0x12, 0x14, 0x1A));
            var text = ResourceColor("App.Foreground", Color.FromRgb(0xE8, 0xEA, 0xF0));
            var border = ResourceColor("App.Border", Color.FromRgb(0x2E, 0x32, 0x40));
            var dark = IsDark(caption);

            SetAttribute(handle, DwmwaUseImmersiveDarkMode, dark ? 1 : 0);
            SetAttribute(handle, DwmwaCaptionColor, ToColorRef(caption));
            SetAttribute(handle, DwmwaTextColor, ToColorRef(text));
            SetAttribute(handle, DwmwaBorderColor, ToColorRef(border));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            // Older Windows or a window without a handle yet: keep the system caption.
        }
    }

    private static void SetAttribute(IntPtr handle, int attribute, int value) =>
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    private static Color ResourceColor(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

    private static bool IsDark(Color color)
    {
        // Perceived luminance; the caption glyph colour also depends on this in the system.
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return luminance < 0.5;
    }

    /// <summary>DWM colour attributes expect a COLORREF: 0x00BBGGRR.</summary>
    private static int ToColorRef(Color color) =>
        color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
