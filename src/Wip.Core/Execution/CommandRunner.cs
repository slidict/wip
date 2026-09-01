using System.Diagnostics;
using System.Text;
using Wip.Diagnostics;

namespace Wip.Execution;

/// <summary>Executes a built command, pumping its I/O and returning the exit status.</summary>
/// <remarks>
/// <para>
/// Two paths, not the Ruby build's three. Piped execution captures output so
/// <see cref="ErrorInterpreter"/> can turn a raw wslc failure into a hint; interactive
/// execution lets the child inherit wip's real console instead.
/// </para>
/// <para>
/// The Ruby build had a third path on Linux that ran interactive commands behind a pty, so
/// it could be interactive <em>and</em> capture output. That existed because wip ran inside a
/// distribution; running on the Windows side there is no openpty to reach for, and the
/// honest trade is the one the Windows path already made — a child that inherits the console
/// gets correct job control, Ctrl-C, and isatty behaviour, and wip gives up seeing its
/// output. So interactive commands produce no error hints. Every non-interactive path
/// (probes, `wip up`, `wip doctor`) still captures, which is where the hints were most
/// useful anyway. Recovering the rest would mean ConPTY; see the migration plan §4.2.
/// </para>
/// </remarks>
public sealed class CommandRunner
{
    // Error hints only need recent diagnostic output. Keeping an unlimited transcript lets
    // a noisy or malicious child exhaust the parent process even though the same output is
    // already being streamed to the user's terminal.
    private const int MaxCapturedCharacters = 1024 * 1024;

    /// <summary>Reported for a command killed by <c>timeout</c> — coreutils' own convention.</summary>
    public const int TimeoutExitCode = 124;

    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly ErrorInterpreter interpreter;
    private readonly bool debug;
    private readonly bool quiet;

    /// <summary>
    /// <paramref name="quiet"/> holds back the captured (non-interactive) path's own stdout
    /// and stderr until the command is known to have failed, instead of streaming it live — see
    /// issue #134's point 4. It has no effect on the interactive path: a child that owns the
    /// console writes straight to it, with nothing passing through here to hold back.
    /// </summary>
    public CommandRunner(
        ErrorInterpreter interpreter,
        TextWriter? output = null,
        TextWriter? error = null,
        bool debug = false,
        bool quiet = false)
    {
        this.interpreter = interpreter;
        this.output = output ?? Console.Out;
        this.error = error ?? Console.Error;
        this.debug = debug;
        this.quiet = quiet;
    }

    /// <summary>
    /// <paramref name="timeout"/> only applies to the captured (non-interactive) path: a
    /// command that runs past it is killed and reported as <see cref="TimeoutExitCode"/>,
    /// with whatever it had already written still captured for a hint. Used by readiness
    /// checks, which poll on a schedule and must never let one slow probe hang <c>wip up</c>.
    /// </summary>
    public int Run(
        IReadOnlyList<string> command,
        IReadOnlyDictionary<string, string>? environment = null,
        bool interactive = false,
        string? workingDirectory = null,
        TimeSpan? timeout = null)
    {
        if (debug)
        {
            error.WriteLine($"+ {CommandDisplay.ForDebug(command)}");
        }

        var startInfo = new ProcessStartInfo(command[0]) { UseShellExecute = false };
        foreach (var argument in command.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        return interactive ? RunInherited(startInfo) : RunCaptured(startInfo, timeout);
    }

    private int RunCaptured(ProcessStartInfo startInfo, TimeSpan? timeout)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;

        var captured = new StringBuilder();

        // Holding each stream's own writer back, rather than buffering just one merged
        // stream, is what keeps stdout and stderr from bleeding into each other once a quiet
        // run's output is finally released -- `captured` above still merges both, same as
        // always, but that copy is only ever read by ErrorInterpreter, never replayed verbatim.
        var outputSink = quiet ? new BufferingWriter(output) : output;
        var errorSink = quiet ? new BufferingWriter(error) : error;

        try
        {
            using var process = Process.Start(startInfo)
                                ?? throw new WipException($"Could not start {startInfo.FileName}");

            // Closing stdin immediately matches the Ruby build: a captured command is never
            // one that reads from the terminal, and leaving the pipe open would hang any
            // child that checks.
            process.StandardInput.Close();

            var pumps = new[]
            {
                Pump(process.StandardOutput, outputSink, captured),
                Pump(process.StandardError, errorSink, captured),
            };

            var exited = timeout is null || process.WaitForExit((int)timeout.Value.TotalMilliseconds);
            if (!exited)
            {
                KillTree(process);
            }

            Task.WaitAll(pumps);
            process.WaitForExit();

            var code = exited ? process.ExitCode : TimeoutExitCode;
            if (code != 0)
            {
                // A held-back run only earns its silence by succeeding; a failure gets the
                // full transcript released before the hint, in the same shape it would have
                // printed in all along.
                (outputSink as BufferingWriter)?.Release();
                (errorSink as BufferingWriter)?.Release();
                ReportHint(captured.ToString());
            }

            return code;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            error.WriteLine(exception.Message);
            return 127;
        }
    }

    /// <summary>
    /// Best-effort: the process may have exited on its own between the timeout check and
    /// here, in which case <see cref="Process.Kill(bool)"/> throwing is not a real failure.
    /// </summary>
    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private int RunInherited(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo)
                                ?? throw new WipException($"Could not start {startInfo.FileName}");

            // The child shares wip's console, so the terminal delivers Ctrl-C to both. There
            // is nothing to forward and nothing to kill — only a child to wait for, which is
            // why this handler just declines to tear wip down first.
            using var interrupt = new ConsoleCancelHandler();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            error.WriteLine(exception.Message);
            return 127;
        }
    }

    private static Task Pump(StreamReader source, TextWriter destination, StringBuilder captured)
    {
        return Task.Run(async () =>
        {
            var buffer = new char[4096];
            while (true)
            {
                int read;
                try
                {
                    read = await source.ReadAsync(buffer).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The pipe closed under us because the child exited; that is a normal
                    // end of stream, not a failure worth surfacing.
                    return;
                }

                if (read == 0)
                {
                    return;
                }

                var text = new string(buffer, 0, read);
                lock (captured)
                {
                    captured.Append(text);
                    if (captured.Length > MaxCapturedCharacters)
                    {
                        captured.Remove(0, captured.Length - MaxCapturedCharacters);
                    }
                }

                destination.Write(text);
                destination.Flush();
            }
        });
    }

    private void ReportHint(string captured)
    {
        if (interpreter.Interpret(captured) is { } hint)
        {
            error.WriteLine();
            error.WriteLine(hint);
        }
    }

    /// <summary>
    /// Keeps Ctrl-C from terminating wip while a child owns the console, so wip can report
    /// the child's own exit status instead of dying first.
    /// </summary>
    private sealed class ConsoleCancelHandler : IDisposable
    {
        private readonly ConsoleCancelEventHandler handler;

        internal ConsoleCancelHandler()
        {
            handler = (_, eventArgs) => eventArgs.Cancel = true;
            Console.CancelKeyPress += handler;
        }

        public void Dispose() => Console.CancelKeyPress -= handler;
    }

    /// <summary>
    /// Accumulates everything written to it instead of forwarding it, until <see cref="Release"/>
    /// says the run turned out to need it after all.
    /// </summary>
    /// <remarks>
    /// <see cref="Pump"/> calls <see cref="TextWriter.Flush"/> after every chunk so a live run
    /// echoes immediately; overriding it to do nothing here is exactly what makes a quiet run
    /// quiet; without that override the base class default would still eagerly forward each
    /// write.
    /// </remarks>
    private sealed class BufferingWriter(TextWriter inner) : TextWriter
    {
        private readonly StringBuilder buffer = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value) => buffer.Append(value);

        public override void Write(string? value)
        {
            if (value is not null)
            {
                buffer.Append(value);
            }
        }

        public override void Flush()
        {
        }

        internal void Release()
        {
            if (buffer.Length == 0)
            {
                return;
            }

            inner.Write(buffer.ToString());
            inner.Flush();
        }
    }
}
