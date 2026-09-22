using System.Windows;

namespace LocalAIModelManager.App.Infrastructure;

/// <summary>Clipboard access that never throws into the UI (the clipboard can be locked).</summary>
public static class ClipboardHelper
{
    public static bool SetText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is owned by another process.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
