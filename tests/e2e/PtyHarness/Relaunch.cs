using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Wip.PtyHarness;

/// <summary>
/// The second stage of the harness: it already runs inside the pseudo console, opens that
/// console's own <c>CONIN$</c>/<c>CONOUT$</c>, and starts the real command with those as its
/// standard handles.
/// </summary>
/// <remarks>
/// <para>
/// This stage exists because of how std handles are assigned. A process created with
/// <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> is attached to the console — it can read the
/// window size, and VT output reaches the pipe — but its standard handles still come from the
/// creator when the creator has its own, and a CI step's creator has redirected pipes. The
/// child then sees <c>stdin</c> as a pipe: measured directly, PowerShell inside such a
/// console reported <c>IsInputRedirected=True</c> while correctly reporting an 80x24 window,
/// and <c>wslc exec -i</c> failed with <c>ERROR_INVALID_HANDLE</c>.
/// </para>
/// <para>
/// Once a process is inside the console it can open the console device by name, which is what
/// this stage does before launching the command under test. That command then starts with
/// genuine console handles, so <c>isatty</c> is true for it and for everything it hands the
/// console on to — which is the whole point of the exercise.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Relaunch
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const int STILL_ACTIVE = 259;

    internal static int Run(IReadOnlyList<string> argv)
    {
        using var input = OpenConsoleDevice("CONIN$", GENERIC_READ | GENERIC_WRITE);
        using var output = OpenConsoleDevice("CONOUT$", GENERIC_READ | GENERIC_WRITE);

        var startupInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESTDHANDLES,
            hStdInput = input.DangerousGetHandle(),
            hStdOutput = output.DangerousGetHandle(),
            // stderr goes to the same console, as it does for a command run at a prompt.
            hStdError = output.DangerousGetHandle(),
        };

        var commandLine = new StringBuilder(PseudoConsole.CommandLine(argv));
        if (!CreateProcess(null, commandLine, nint.Zero, nint.Zero, true, 0, nint.Zero, null, ref startupInfo, out var processInfo))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess ({argv[0]}) failed");

        try
        {
            WaitForSingleObject(processInfo.hProcess, 0xFFFFFFFF);
            if (!GetExitCodeProcess(processInfo.hProcess, out var code))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeProcess failed");
            return code == STILL_ACTIVE ? 0 : code;
        }
        finally
        {
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
        }
    }

    /// <summary>
    /// Opens one end of the console this process is attached to. The handle is created
    /// inheritable, since the point is to pass it to the command under test.
    /// </summary>
    private static SafeFileHandle OpenConsoleDevice(string name, uint access)
    {
        var security = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = nint.Zero,
            bInheritHandle = true,
        };

        var handle = CreateFile(name, access, FILE_SHARE_READ | FILE_SHARE_WRITE, ref security, OPEN_EXISTING, 0, nint.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile({name}) failed; this stage must run inside the pseudo console");
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint access, uint share, ref SECURITY_ATTRIBUTES security,
        uint creationDisposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out int code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
