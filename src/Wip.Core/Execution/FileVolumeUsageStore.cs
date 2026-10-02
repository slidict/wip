using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wip.Execution;

/// <summary>Local durable observations. Hold this lease through the entire lifecycle operation.</summary>
public sealed class FileVolumeUsageStore : IVolumeUsageStore, IDisposable
{
    private readonly string directory;
    private readonly FileStream lease;

    public FileVolumeUsageStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        try
        {
            Directory.CreateDirectory(this.directory);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                try { lease = new FileStream(Path.Combine(this.directory, "usage.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
                catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(10)) { Thread.Sleep(50); }
            }
        }
        catch (IOException exception) { throw new WipException("Volume usage journal is unavailable or locked; no cleanup attempted", exception); }
        catch (UnauthorizedAccessException exception) { throw new WipException("Volume usage journal is inaccessible; no cleanup attempted", exception); }
    }

    private string Marker(string name) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant() + ".json");

    public bool WasUsed(string backendName)
    {
        try
        {
            var path = Marker(backendName);
            if (!File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 || !root.TryGetProperty("backendName", out var name) ||
                name.ValueKind != JsonValueKind.String || name.GetString() != backendName)
                throw new WipException("Volume usage journal is malformed; storage retained");
            return true;
        }
        catch (JsonException exception) { throw new WipException("Volume usage journal is malformed; storage retained", exception); }
        catch (IOException exception) { throw new WipException("Volume usage journal could not be read; storage retained", exception); }
        catch (UnauthorizedAccessException exception) { throw new WipException("Volume usage journal is inaccessible; storage retained", exception); }
    }

    public void MarkUsed(string backendName)
    {
        if (WasUsed(backendName)) return;
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("backendName", backendName); writer.WriteEndObject();
                writer.Flush(); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Marker(backendName));
        }
        catch (IOException exception) { throw new WipException("Volume usage observation could not be saved; storage retained", exception); }
        catch (UnauthorizedAccessException exception) { throw new WipException("Volume usage journal is inaccessible; storage retained", exception); }
        finally
        {
            // A failed temp-file cleanup must not replace the storage-retained error.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => lease.Dispose();

    public void Forget(string backendName)
    {
        if (!WasUsed(backendName)) return;
        try { File.Delete(Marker(backendName)); }
        catch (IOException exception) { throw new WipException("Storage was removed but its usage marker could not be removed", exception); }
        catch (UnauthorizedAccessException exception) { throw new WipException("Storage was removed but its usage marker is inaccessible", exception); }
    }
}
