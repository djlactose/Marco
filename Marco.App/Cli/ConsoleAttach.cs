using System.IO;
using System.Runtime.InteropServices;

namespace Marco.App.Cli;

/// <summary>
/// A WinExe has no console. When Marco's `scan` verb is run from an existing terminal we attach to the PARENT
/// console so output is visible; when there is none (Task Scheduler), we simply have no console and rely on
/// --out/--log. AllocConsole is deliberately never used — a scheduled task must not pop a window.
/// The stdout/stderr writers are rebuilt after attach because the CLR cached the (invalid) handles at startup.
/// </summary>
public static class ConsoleAttach
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    public static void TryAttachToParent()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess)) return;
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
        catch
        {
            // No parent console (scheduled/detached): output goes to --out/--log instead. Never fatal.
        }
    }
}
