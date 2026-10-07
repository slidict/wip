using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Wip.PtyHarness;

/// <summary>
/// Keeps console control events from ending the harness, so the exit code it reports is the
/// one the command under test chose.
/// </summary>
/// <remarks>
/// The handler is held in a static field on purpose: a delegate passed to
/// <c>SetConsoleCtrlHandler</c> is called by the OS, and letting it be collected would leave
/// the callback pointing at freed memory.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Interrupts
{
    private static readonly HandlerRoutine Handler = _ => true;

    internal static void Decline()
    {
        if (!SetConsoleCtrlHandler(Handler, true))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetConsoleCtrlHandler failed");
    }

    private delegate bool HandlerRoutine(uint controlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine? handler, bool add);
}
