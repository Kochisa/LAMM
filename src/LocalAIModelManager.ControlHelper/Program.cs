using System.Globalization;
using System.Runtime.InteropServices;

namespace LocalAIModelManager.ControlHelper;

/// <summary>
/// A tiny, disposable helper that delivers CTRL_BREAK to another process's console.
///
/// Why a separate process at all: <c>GenerateConsoleCtrlEvent</c> only reaches
/// processes attached to the *caller's* console, so the caller must
/// <c>AttachConsole</c> first. Attaching also exposes the caller to the very signal
/// it is sending - harmless for this throwaway helper (it may die from the signal
/// after delivering it), but fatal for the long-lived manager when it runs as a
/// windowless GUI process.
///
/// Usage: LocalAIModelManager.ControlHelper --send-ctrl-break &lt;pid&gt;
/// Exit codes: 0 = signal delivered, 1 = bad usage, 2 = attach failed, 3 = send failed.
/// </summary>
internal static class Program
{
    private const uint CtrlBreakEvent = 1;

    private static int Main(string[] args)
    {
        if (args.Length != 2 ||
            !string.Equals(args[0], "--send-ctrl-break", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ||
            pid <= 0)
        {
            Console.Error.WriteLine("usage: LocalAIModelManager.ControlHelper --send-ctrl-break <pid>");
            return 1;
        }

        // A process can be attached to at most one console.
        FreeConsole();

        if (!AttachConsole((uint)pid))
        {
            return 2;
        }

        try
        {
            // Process group 0 = every process attached to the console, i.e. the engine.
            return GenerateConsoleCtrlEvent(CtrlBreakEvent, 0) ? 0 : 3;
        }
        finally
        {
            FreeConsole();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
}
