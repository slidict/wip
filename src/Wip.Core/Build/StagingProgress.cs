namespace Wip.Build;

/// <summary>
/// Prints a self-overwriting "copying build context files: N/total" line every half second
/// while a slow, silent step is running. Reporting is driven by a timer rather than by each
/// tick, so copying one large file does not make progress appear hung.
/// </summary>
public sealed class StagingProgress : IDisposable
{
    private readonly TextWriter output;
    private readonly TimeSpan interval;
    private readonly Lock gate = new();
    private Timer? timer;
    private int count;
    private int total;
    private bool finished;

    public StagingProgress(TextWriter? output = null, TimeSpan? interval = null)
    {
        this.output = output ?? Console.Error;
        this.interval = interval ?? TimeSpan.FromSeconds(0.5);
    }

    public void Tick(int count, int total)
    {
        lock (gate)
        {
            if (finished)
            {
                return;
            }

            this.count = count;
            this.total = total;

            if (timer is null)
            {
                Print();
                timer = new Timer(_ => OnInterval(), null, interval, interval);
            }
            else if (count == total)
            {
                Print();
            }
        }
    }

    /// <summary>Idempotent: a caller cannot know in advance whether <see cref="Tick"/> ever fired.</summary>
    public void Finish()
    {
        Timer? stopped;
        lock (gate)
        {
            if (finished || timer is null)
            {
                finished = true;
                return;
            }

            finished = true;
            stopped = timer;
            timer = null;
        }

        stopped.Dispose();
        lock (gate)
        {
            output.WriteLine();
            output.Flush();
        }
    }

    public void Dispose() => Finish();

    private void OnInterval()
    {
        lock (gate)
        {
            if (!finished)
            {
                Print();
            }
        }
    }

    private void Print()
    {
        output.Write($"\rwip: copying build context files: {count}/{total}");
        output.Flush();
    }
}
