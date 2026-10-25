using System.Text;

namespace Wip.Security;

/// <summary>Publishes complete run artifacts, with result.json acting as the final commit marker.</summary>
internal sealed class ScanResultStore(string outputDirectory, Action<string, string>? publish = null)
{
    internal string Write(ScanResult result, string? rawReport, string? rawUnperformedChecks)
    {
        ScanResultWriter.Validate(result);
        var temporaryFiles = new List<string>();
        try
        {
            var runDirectory = Path.Combine(Path.GetFullPath(outputDirectory), result.Run.RunId);
            Directory.CreateDirectory(runDirectory);
            // Empty files represent unavailable artifacts; the result's status and coverage
            // reason are authoritative. Available raw text is never transformed.
            WriteAtomic(runDirectory, "report.json", stream => WriteRaw(stream, rawReport ?? ""), temporaryFiles);
            WriteAtomic(runDirectory, "unperformed-checks.txt", stream => WriteRaw(stream, rawUnperformedChecks ?? ""), temporaryFiles);
            WriteAtomic(runDirectory, "result.json", stream => ScanResultWriter.Write(stream, result), temporaryFiles);
            return runDirectory;
        }
        catch (IOException exception) { throw new WipException("Scan result artifacts could not be saved", exception); }
        catch (UnauthorizedAccessException exception) { throw new WipException("Scan result output directory is inaccessible", exception); }
        finally
        {
            foreach (var temporary in temporaryFiles)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void WriteAtomic(string directory, string name, Action<Stream> write, List<string> temporaryFiles)
    {
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        temporaryFiles.Add(temporary);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            write(stream);
            stream.Flush(flushToDisk: true);
        }
        var destination = Path.Combine(directory, name);
        if (publish is { } commit) commit(temporary, destination);
        else File.Move(temporary, destination);
    }

    private static void WriteRaw(Stream stream, string text)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(text);
        writer.Flush();
    }
}
