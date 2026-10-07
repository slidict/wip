using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Wip.PtyHarness;

/// <summary>
/// A Windows pseudo console (ConPTY) with a child process attached to it.
/// </summary>
/// <remarks>
/// <para>
/// A CI runner has no terminal of its own, which is often taken to mean that terminal
/// behaviour cannot be tested there. It only means nobody hands one over: a process can
/// create one. <c>CreatePseudoConsole</c> returns a console backed by two pipes, and a child
/// launched with it as its <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> sees real console
/// handles — <c>isatty</c> is true, it can read a window size, and it receives Ctrl-C the way
/// a terminal delivers it.
/// </para>
/// <para>
/// Nothing here is wip's own code path; it is the terminal a user would otherwise have to be
/// sitting at. Keeping it in the e2e suite rather than in <c>Wip.Core</c> is deliberate: wip
/// inherits whatever console it is given and must never create one.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class PseudoConsole : IDisposable
{
    private const int STILL_ACTIVE = 259;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    private readonly nint console;
    private readonly nint process;
    private readonly nint thread;
    private readonly nint attributeList;
    private readonly SafeFileHandle input;
    private readonly SafeFileHandle output;
    private bool disposed;

    /// <summary>Everything the child has written to the console so far.</summary>
    public StringBuilder Transcript { get; } = new();

    private PseudoConsole(nint console, nint process, nint thread, nint attributeList, SafeFileHandle input, SafeFileHandle output)
    {
        this.console = console;
        this.process = process;
        this.thread = thread;
        this.attributeList = attributeList;
        this.input = input;
        this.output = output;
    }

    /// <summary>
    /// Creates a console of the given size and starts <paramref name="argv"/> attached to it.
    /// </summary>
    public static PseudoConsole Start(IReadOnlyList<string> argv, short columns, short rows, string? workingDirectory)
    {
        // Each end is created as a pair: the child reads the input pipe and writes the output
        // pipe through the console, while this process keeps the opposite ends.
        if (!CreatePipe(out var inputRead, out var inputWrite, nint.Zero, 0)) throw Win32("CreatePipe (input)");
        if (!CreatePipe(out var outputRead, out var outputWrite, nint.Zero, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw Win32("CreatePipe (output)");
        }

        var size = new COORD { X = columns, Y = rows };
        var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out var console);
        // The console owns its copies now; holding ours open would keep the child's streams
        // from ever reporting end of file.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new Win32Exception(hr, $"CreatePseudoConsole failed (HRESULT 0x{hr:x8})");
        }

        nint attributeList = nint.Zero;
        try
        {
            var listSize = nint.Zero;
            // The documented two-call form: the first call only reports the size it needs and
            // is expected to fail.
            InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref listSize);
            attributeList = Marshal.AllocHGlobal(listSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref listSize)) throw Win32("InitializeProcThreadAttributeList");
            if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, console, nint.Size, nint.Zero, nint.Zero))
                throw Win32("UpdateProcThreadAttribute");

            var startupInfo = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                lpAttributeList = attributeList,
            };

            // CreateProcess parses one command line, so argv is quoted back into the form
            // CommandLineToArgvW reverses -- the same contract wip itself relies on when it
            // hands argv to a process API.
            var commandLine = new StringBuilder(CommandLine(argv));
            if (!CreateProcess(
                    null, commandLine, nint.Zero, nint.Zero, true,
                    EXTENDED_STARTUPINFO_PRESENT, nint.Zero, workingDirectory,
                    ref startupInfo, out var processInfo))
            {
                throw Win32($"CreateProcess ({argv[0]})");
            }

            var session = new PseudoConsole(console, processInfo.hProcess, processInfo.hThread, attributeList, inputWrite, outputRead);
            attributeList = nint.Zero;
            return session;
        }
        catch
        {
            if (attributeList != nint.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            ClosePseudoConsole(console);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the console's output until the child exits, appending it to the transcript.
    /// Returns when the pipe reaches end of file, which is after the child is gone.
    /// </summary>
    public Task PumpAsync()
    {
        return Task.Run(() =>
        {
            using var stream = new FileStream(output, FileAccess.Read, bufferSize: 1, isAsync: false);
            // The console speaks UTF-8 and emits VT sequences; decoding incrementally keeps a
            // character split across reads from turning into replacement characters.
            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[4096];
            var chars = new char[4096];
            while (true)
            {
                int read;
                try
                {
                    read = stream.Read(bytes, 0, bytes.Length);
                }
                catch (IOException)
                {
                    // The console was closed under us, which is how a finished session ends.
                    return;
                }

                if (read == 0) return;
                var decoded = decoder.GetChars(bytes, 0, read, chars, 0);
                if (decoded == 0) continue;
                lock (Transcript)
                {
                    Transcript.Append(chars, 0, decoded);
                }
            }
        });
    }

    /// <summary>Sends text as typed input. A terminal sends CR for Enter, not LF.</summary>
    public void SendLine(string text) => Write(text + "\r");

    /// <summary>Sends a raw control character, such as 0x03 for Ctrl-C.</summary>
    public void SendControl(char control) => Write(control.ToString());

    private void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!WriteFile(input, bytes, bytes.Length, out var written, nint.Zero) || written != bytes.Length)
            throw Win32("WriteFile (console input)");
        if (!FlushFileBuffers(input))
        {
            // A pipe that cannot be flushed is not an error here: the write already went
            // through, and the console reads it on its own schedule.
        }
    }

    /// <summary>Changes the console's size, as resizing a terminal window would.</summary>
    public void Resize(short columns, short rows)
    {
        var hr = ResizePseudoConsole(console, new COORD { X = columns, Y = rows });
        if (hr != 0) throw new Win32Exception(hr, $"ResizePseudoConsole failed (HRESULT 0x{hr:x8})");
    }

    /// <summary>Closes the input side, which the child sees as end of file.</summary>
    public void CloseInput() => input.Dispose();

    /// <summary>Waits for the child and returns its exit code, or null if it outlived the wait.</summary>
    public int? WaitForExit(TimeSpan timeout)
    {
        var result = WaitForSingleObject(process, (uint)timeout.TotalMilliseconds);
        if (result != 0) return null;
        if (!GetExitCodeProcess(process, out var code)) throw Win32("GetExitCodeProcess");
        return code == STILL_ACTIVE ? null : code;
    }

    public void Kill()
    {
        // Best effort: the child may have exited between the wait and here.
        TerminateProcess(process, 1);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Closing the console first lets the output pump see end of file instead of blocking.
        ClosePseudoConsole(console);
        if (!input.IsClosed) input.Dispose();
        if (!output.IsClosed) output.Dispose();
        if (attributeList != nint.Zero)
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
        if (thread != nint.Zero) CloseHandle(thread);
        if (process != nint.Zero) CloseHandle(process);
    }

    /// <summary>
    /// Quotes argv into a single command line, following the rules CommandLineToArgvW
    /// reverses: a run of backslashes is doubled only when a quote follows it.
    /// </summary>
    internal static string CommandLine(IReadOnlyList<string> argv)
    {
        var result = new StringBuilder();
        foreach (var argument in argv)
        {
            if (result.Length > 0) result.Append(' ');
            if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"'))
            {
                result.Append(argument);
                continue;
            }

            result.Append('"');
            for (var index = 0; index < argument.Length; index++)
            {
                var backslashes = 0;
                while (index < argument.Length && argument[index] == '\\')
                {
                    backslashes++;
                    index++;
                }

                if (index == argument.Length)
                {
                    result.Append('\\', backslashes * 2);
                    break;
                }

                if (argument[index] == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    result.Append('\\', backslashes);
                }

                result.Append(argument[index]);
            }

            result.Append('"');
        }

        return result.ToString();
    }

    private static Win32Exception Win32(string what) => new(Marshal.GetLastWin32Error(), $"{what} failed");

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
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
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, nint attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int toWrite, out int written, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out int code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(nint process, int code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(nint console, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(nint console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        nint attributeList, uint flags, nint attribute, nint value, nint size, nint previousValue, nint returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(nint attributeList);

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
        ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);
}
