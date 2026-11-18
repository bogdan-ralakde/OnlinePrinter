using System.Runtime.InteropServices;
using System.Text;

namespace LabelBridge;

/// <summary>
/// WinExe apps have no console. When started from a terminal we attach to the
/// parent console so CLI output and self-tests are visible.
/// </summary>
internal static class ConsoleBridge
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    public static void Attach()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess)) return;
            var stdout = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
        catch
        {
            // No console available - GUI mode is unaffected.
        }
    }
}
