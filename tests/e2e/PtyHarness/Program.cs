using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace Wip.PtyHarness;

/// <summary>
/// Runs a command inside a pseudo console it creates itself, driving it from a small script
/// of directives, so terminal behaviour can be asserted on a machine with no terminal.
/// </summary>
/// <remarks>
/// <para>
/// Usage:
/// </para>
/// <code>
/// pty-harness --script session.txt [--cols 80] [--rows 24] [--timeout 120]
///             [--workdir &lt;dir&gt;] [--result &lt;path&gt;] [--echo] -- &lt;command&gt; [args...]
/// </code>
/// <para>
/// Script directives, one per line (<c>#</c> starts a comment):
/// </para>
/// <list type="bullet">
/// <item><c>send &lt;text&gt;</c> — type the text and press Enter (CR, as a terminal does)</item>
/// <item><c>ctrl-c</c> — send 0x03, the character a terminal turns into an interrupt</item>
/// <item><c>resize &lt;cols&gt;x&lt;rows&gt;</c> — resize the console, as dragging a window would</item>
/// <item><c>expect &lt;regex&gt;</c> — wait until the output matches, then continue</item>
/// <item><c>eof</c> — close the input side, which the child reads as end of file</item>
/// <item><c>sleep &lt;ms&gt;</c> — last resort for something no output announces</item>
/// </list>
/// <para>
/// Exit code: the child's own, so a test can assert on it. Harness failures are reported as
/// 97 (an <c>expect</c> that never matched), 98 (the child outlived <c>--timeout</c>) or 99
/// (a usage or platform error), chosen to stay clear of the exit codes under test.
/// </para>
/// <para>
/// <c>--result</c> writes the same outcome to a file as <c>status=&lt;ok|expect-failed|child-timeout&gt;</c>
/// and <c>exit=&lt;code&gt;</c>, and a caller should prefer it. Tearing a pseudo console down
/// raises a control event that can end this process with STATUS_CONTROL_C_EXIT
/// (-1073741510) after it has already decided what to report — observed on most runs here,
/// and not something the harness can decline from outside the console. The file is written
/// before the console is closed, so it survives that.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>Marks the stage-two invocation; not part of the documented interface.</summary>
    internal const string RelaunchFlag = "--inside-pseudo-console";

    /// <summary>The character a terminal sends for Ctrl-C.</summary>
    private const char Interrupt = (char)3;

    /// <summary>
    /// How long a session is given to end on its own before EOF is used to end it. Short
    /// because a script that ends itself does so as soon as its last line is read.
    /// </summary>
    private static readonly TimeSpan EndsItself = TimeSpan.FromSeconds(5);

    private const int ExpectFailed = 97;
    private const int ChildTimedOut = 98;
    private const int HarnessError = 99;

    internal static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("pty-harness: ConPTY is a Windows facility; this harness only runs on Windows.");
            return HarnessError;
        }

        // A control event raised while the console is being torn down reaches this process
        // too, and a process killed that way reports STATUS_CONTROL_C_EXIT (-1073741510)
        // whatever it had decided to return -- which would make the child's exit code, the
        // thing under test, unreadable. Declining every event at the Win32 level rather than
        // through Console.CancelKeyPress: the managed handler covers Ctrl-C and Ctrl-Break
        // only, and a close event still ended the harness on one run in three.
        Interrupts.Decline();

        // Stage two, started by stage one inside the pseudo console it created. See Relaunch.
        if (args.Length > 1 && args[0] == RelaunchFlag)
        {
            return Relaunch.Run(args.Skip(1).ToArray());
        }

        try
        {
            return Run(args);
        }
        catch (UsageException exception)
        {
            Console.Error.WriteLine($"pty-harness: {exception.Message}");
            return HarnessError;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int Run(string[] args)
    {
        var options = Options.Parse(args);
        var directives = Script.Load(options.ScriptPath);

        // The command is started through this executable's own stage two so that it receives
        // the console's handles rather than this process's redirected ones.
        var relaunch = new List<string> { Environment.ProcessPath ?? "pty-harness.exe", RelaunchFlag };
        relaunch.AddRange(options.Argv);
        using var session = PseudoConsole.Start(relaunch, options.Columns, options.Rows, options.WorkingDirectory);
        var pump = session.PumpAsync();
        var inputClosed = false;
        var failure = 0;

        try
        {
            foreach (var directive in directives)
            {
                Trace(options, $"> {directive.Keyword} {directive.Argument}");
                switch (directive.Keyword)
                {
                    case "send":
                        session.SendLine(directive.Argument);
                        break;
                    case "ctrl-c":
                        session.SendControl(Interrupt);
                        break;
                    case "resize":
                        var (columns, rows) = Script.ParseSize(directive.Argument);
                        session.Resize(columns, rows);
                        break;
                    case "expect":
                        if (!Wait(session, directive.Argument, options))
                        {
                            Console.Error.WriteLine($"pty-harness: expect /{directive.Argument}/ did not match within {options.Timeout.TotalSeconds:0}s");
                            failure = ExpectFailed;
                        }

                        break;
                    case "eof":
                        session.CloseInput();
                        inputClosed = true;
                        break;
                    case "sleep":
                        Thread.Sleep(int.Parse(directive.Argument, CultureInfo.InvariantCulture));
                        break;
                    default:
                        throw new UsageException($"unknown directive '{directive.Keyword}' on line {directive.Line}");
                }

                if (failure != 0) break;
            }

            // A session that ends itself -- the script's last line was `exit 7` -- is given
            // the chance to do so before anything is closed. Closing the console's input
            // first is read as the terminal going away: the close event that follows reaches
            // wslc, which dies with STATUS_CONTROL_C_EXIT, and wip faithfully reports that
            // instead of the status the shell chose. Measured repeatedly: the same script
            // reported 7 or -1073741510 depending on which happened first.
            var code = session.WaitForExit(inputClosed ? options.Timeout : EndsItself);
            if (code is null && !inputClosed)
            {
                // It is still running, so this script expects EOF to end it.
                session.CloseInput();
                inputClosed = true;
                code = session.WaitForExit(options.Timeout);
            }

            if (code is null)
            {
                Console.Error.WriteLine($"pty-harness: the child did not exit within {options.Timeout.TotalSeconds:0}s");
                session.Kill();
                failure = failure == 0 ? ChildTimedOut : failure;
            }

            // Give the pump a moment to drain what the child wrote last; the transcript is
            // the only evidence a failing assertion has to go on.
            pump.Wait(TimeSpan.FromSeconds(5));
            WriteTranscript(session);
            WriteResult(options, failure, code);

            // Exit before the console is torn down. Closing it raises a control event that
            // reaches this process, and a process killed that way reports
            // STATUS_CONTROL_C_EXIT (-1073741510) no matter what it had decided to return --
            // measured on every run, which would make the child's exit code unreadable. The
            // handles this skips releasing are released by process teardown a moment later.
            Console.Out.Flush();
            Console.Error.Flush();
            Environment.Exit(failure != 0 ? failure : code!.Value);
            throw new UnreachableException();
        }
        catch
        {
            pump.Wait(TimeSpan.FromSeconds(1));
            WriteTranscript(session);
            throw;
        }
    }

    /// <summary>
    /// Records the outcome where a caller can read it regardless of how this process ends.
    /// </summary>
    private static void WriteResult(Options options, int failure, int? code)
    {
        if (options.ResultPath is null) return;
        var status = failure switch
        {
            0 => "ok",
            ExpectFailed => "expect-failed",
            ChildTimedOut => "child-timeout",
            _ => "harness-error",
        };
        var exit = code?.ToString(CultureInfo.InvariantCulture) ?? "none";
        File.WriteAllLines(options.ResultPath, [$"status={status}", $"exit={exit}"]);
    }

    [SupportedOSPlatform("windows")]
    private static bool Wait(PseudoConsole session, string pattern, Options options)
    {
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        var deadline = DateTime.UtcNow + options.Timeout;
        while (DateTime.UtcNow < deadline)
        {
            string current;
            lock (session.Transcript)
            {
                current = session.Transcript.ToString();
            }

            if (regex.IsMatch(current)) return true;
            Thread.Sleep(50);
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteTranscript(PseudoConsole session)
    {
        string transcript;
        lock (session.Transcript)
        {
            transcript = session.Transcript.ToString();
        }

        // The console's own escape sequences are dropped so an assertion can match on the
        // text a person would have read, not on the cursor moves around it.
        Console.Out.Write(Vt.Strip(transcript));
        Console.Out.Flush();
    }

    private static void Trace(Options options, string message)
    {
        if (options.Echo) Console.Error.WriteLine(message);
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed record Directive(string Keyword, string Argument, int Line);

    private sealed record Options(
        string ScriptPath,
        short Columns,
        short Rows,
        TimeSpan Timeout,
        string? WorkingDirectory,
        string? ResultPath,
        bool Echo,
        IReadOnlyList<string> Argv)
    {
        internal static Options Parse(string[] args)
        {
            string? script = null;
            short columns = 80;
            short rows = 24;
            var timeout = TimeSpan.FromSeconds(120);
            string? workingDirectory = null;
            string? resultPath = null;
            var echo = false;
            var argv = new List<string>();

            var index = 0;
            for (; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument == "--")
                {
                    index++;
                    break;
                }

                string Value(string name) => index + 1 < args.Length
                    ? args[++index]
                    : throw new UsageException($"{name} requires a value");

                switch (argument)
                {
                    case "--script":
                        script = Value("--script");
                        break;
                    case "--cols":
                        columns = short.Parse(Value("--cols"), CultureInfo.InvariantCulture);
                        break;
                    case "--rows":
                        rows = short.Parse(Value("--rows"), CultureInfo.InvariantCulture);
                        break;
                    case "--timeout":
                        timeout = TimeSpan.FromSeconds(double.Parse(Value("--timeout"), CultureInfo.InvariantCulture));
                        break;
                    case "--workdir":
                        workingDirectory = Value("--workdir");
                        break;
                    case "--result":
                        resultPath = Value("--result");
                        break;
                    case "--echo":
                        echo = true;
                        break;
                    default:
                        throw new UsageException($"unknown option '{argument}' (the command goes after --)");
                }
            }

            for (; index < args.Length; index++) argv.Add(args[index]);

            if (script is null) throw new UsageException("--script <path> is required");
            if (argv.Count == 0) throw new UsageException("a command is required after --");
            if (columns <= 0 || rows <= 0) throw new UsageException("--cols and --rows must be positive");

            return new(script, columns, rows, timeout, workingDirectory, resultPath, echo, argv);
        }
    }

    private static class Script
    {
        internal static IReadOnlyList<Directive> Load(string path)
        {
            if (!File.Exists(path)) throw new UsageException($"script not found: {path}");
            var directives = new List<Directive>();
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var split = line.IndexOf(' ');
                var keyword = split < 0 ? line : line[..split];
                var argument = split < 0 ? "" : line[(split + 1)..].Trim();
                directives.Add(new(keyword, argument, index + 1));
            }

            if (directives.Count == 0) throw new UsageException($"script has no directives: {path}");
            return directives;
        }

        internal static (short Columns, short Rows) ParseSize(string value)
        {
            var parts = value.Split('x', 2);
            if (parts.Length != 2) throw new UsageException($"resize takes <cols>x<rows>, got '{value}'");
            return (
                short.Parse(parts[0], CultureInfo.InvariantCulture),
                short.Parse(parts[1], CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Removes the escape sequences a console emits around the text itself.</summary>
    internal static class Vt
    {
        internal static string Strip(string text)
        {
            var result = new StringBuilder(text.Length);
            for (var index = 0; index < text.Length; index++)
            {
                var current = text[index];
                if (current != '')
                {
                    // A console redraws with CR; keeping it would make every assertion match
                    // against half-overwritten lines.
                    if (current != '\r') result.Append(current);
                    continue;
                }

                if (index + 1 >= text.Length) break;
                var next = text[++index];
                switch (next)
                {
                    case '[':
                        // CSI: parameters and intermediates, then one final byte.
                        while (index + 1 < text.Length && text[index + 1] is (>= ' ' and <= '?')) index++;
                        if (index + 1 < text.Length) index++;
                        break;
                    case ']':
                        // OSC: runs to BEL or ST.
                        while (index + 1 < text.Length)
                        {
                            index++;
                            if (text[index] == '') break;
                            if (text[index] == '' && index + 1 < text.Length && text[index + 1] == '\\')
                            {
                                index++;
                                break;
                            }
                        }

                        break;
                    default:
                        // Two-character sequences such as ESC = and ESC >.
                        break;
                }
            }

            return result.ToString();
        }
    }
}
