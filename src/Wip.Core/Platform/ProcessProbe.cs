using System.Diagnostics;

namespace Wip.Platform;

/// <summary>
/// Runs a short-lived helper process and reports whether it succeeded.
/// </summary>
/// <remarks>
/// Redirecting a child's output without reading it deadlocks as soon as the child writes more
/// than the pipe buffer holds: the child blocks on the write, and the parent blocks forever
/// waiting for an exit that cannot happen. Both diagnostic probes in wip redirect, so the
/// draining and the timeout live here rather than being repeated — and got wrong — at each
/// call site.
/// </remarks>
public static class ProcessProbe
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns true when the command ran and exited zero. A command that cannot be started,
    /// or that outlives <paramref name="timeout"/>, is a failure rather than an exception:
    /// every caller is a diagnostic asking "does this work?", and a hung wslc should not hang
    /// <c>wip doctor</c> with it.
    /// </summary>
    public static bool Succeeds(string fileName, IEnumerable<string> arguments, TimeSpan? timeout = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // Started before the wait so the pipes keep draining while the child runs.
            // The probe only cares about the exit status. ReadToEndAsync would retain an
            // unlimited pair of strings and let a broken or hostile executable exhaust
            // wip's memory before the timeout can stop it.
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);

            if (!process.WaitForExit(timeout ?? DefaultTimeout))
            {
                Kill(process);
                return false;
            }

            // The overload without a timeout is what actually flushes the redirected streams.
            process.WaitForExit();
            Task.WaitAll([output, error], TimeSpan.FromSeconds(5));

            return process.ExitCode == 0;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
                or InvalidOperationException
                or PlatformNotSupportedException
                // Killing a process tree reports a descendant's failure through this.
                or AggregateException)
        {
            return false;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);

            // Kill only requests termination. Waiting keeps the handle valid until the exit
            // is real, so disposing it does not race the kill still in progress.
            process.WaitForExit(5000);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException
                or AggregateException)
        {
            // It exited on its own between the timeout and this call, or a descendant
            // could not be reached — either way there is nothing left to do.
        }
    }
}
