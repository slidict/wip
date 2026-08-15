using System.Diagnostics;
using System.Globalization;

namespace Wip.Diagnostics;

/// <summary>Prints each step wip takes and how long it took, when debug mode is enabled.</summary>
public sealed class DebugReporter
{
    private readonly bool enabled;
    private readonly TextWriter output;
    private readonly string? log;

    /// <summary>
    /// <paramref name="log"/> overrides where resource snapshots go, regardless of the
    /// per-step <c>live</c> flag: null is automatic, "-" always prints inline even for
    /// interactive steps, and a path always writes there even for non-interactive ones.
    /// </summary>
    public DebugReporter(bool enabled, TextWriter? output = null, string? log = null)
    {
        this.enabled = enabled;
        this.output = output ?? Console.Error;
        this.log = log;
    }

    public T Step<T>(string label, Func<T> action, bool live = true)
    {
        if (!enabled)
        {
            return action();
        }

        var started = Stopwatch.GetTimestamp();
        output.WriteLine($"wip: [debug] {label}");

        var file = OpenLog(live);
        using var monitor = new ResourceMonitor(file ?? output);
        monitor.Start(label);

        try
        {
            return action();
        }
        finally
        {
            monitor.Stop();
            file?.Dispose();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wip: [debug] done in {elapsed:F2}s: {label}"));
        }
    }

    public void Step(string label, Action action, bool live = true) =>
        Step(label, () => { action(); return 0; }, live);

    /// <summary>
    /// <c>live: false</c> is for steps that hand the real console to the child process (a
    /// <c>wslc exec -it</c>, say). The child owns cursor control there, so writing periodic
    /// snapshots into the same terminal races with it and garbles the output; those go to a
    /// log file instead.
    /// </summary>
    private TextWriter? OpenLog(bool live)
    {
        if (log == "-")
        {
            return null;
        }

        if (log is not null)
        {
            output.WriteLine($"wip: [debug] streaming resource snapshots to {log}");
            return CreateWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        }

        if (live)
        {
            return null;
        }

        // Created with a random name rather than a predictable one, so a pre-created symlink
        // in a shared temp directory cannot redirect the write.
        var path = Path.Combine(Path.GetTempPath(), $"wip-debug-{Path.GetRandomFileName()}.log");
        output.WriteLine($"wip: [debug] command owns the terminal; streaming resource snapshots to {path}");
        return CreateWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
    }

    private static TextWriter CreateWriter(FileStream stream) => new StreamWriter(stream) { AutoFlush = true };
}
