using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Wip.Diagnostics;

/// <summary>
/// Periodically prints host CPU and memory information while a debug step is still running,
/// so a hung or slow step is visible even before it produces output of its own.
/// </summary>
/// <remarks>
/// The Ruby build read /proc/loadavg, /proc/meminfo, and /proc/diskstats, none of which exist
/// on the Windows side. Memory and the busiest processes carry over; a load average has no
/// Windows equivalent and per-device I/O counters would need performance-counter plumbing
/// well out of proportion to a --debug aid, so both are reported as unavailable rather than
/// approximated into something misleading.
/// </remarks>
public sealed partial class ResourceMonitor : IDisposable
{
    private readonly TimeSpan interval;
    private readonly TextWriter output;
    private Timer? timer;

    public ResourceMonitor(TextWriter? output = null, TimeSpan? interval = null)
    {
        this.output = output ?? Console.Error;
        this.interval = interval ?? TimeSpan.FromSeconds(5);
    }

    public void Start(string label) =>
        timer = new Timer(_ => output.WriteLine($"wip: [debug] still running ({Snapshot()}): {label}"),
            null, interval, interval);

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    public void Dispose() => Stop();

    private static string Snapshot() => string.Join(" | ", Memory(), TopProcesses());

    private static string Memory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "mem n/a";
        }

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return "mem n/a";
        }

        const double gigabyte = 1024 * 1024 * 1024;
        var total = status.TotalPhys / gigabyte;
        var used = (status.TotalPhys - status.AvailPhys) / gigabyte;
        return string.Create(CultureInfo.InvariantCulture, $"mem {used:F1}G/{total:F1}G");
    }

    private static string TopProcesses()
    {
        try
        {
            var entries = Process.GetProcesses()
                .OrderByDescending(process => SafeWorkingSet(process))
                .Take(3)
                .Select(process => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{process.ProcessName}({process.Id}) mem {SafeWorkingSet(process) / (1024.0 * 1024.0):F0}MB"))
                .ToList();

            return $"top: {string.Join(", ", entries)}";
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            return "top: n/a";
        }
    }

    private static long SafeWorkingSet(Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // The process exited between enumeration and inspection.
            return 0;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhys;
        internal ulong AvailPhys;
        internal ulong TotalPageFile;
        internal ulong AvailPageFile;
        internal ulong TotalVirtual;
        internal ulong AvailVirtual;
        internal ulong AvailExtendedVirtual;
    }
}
