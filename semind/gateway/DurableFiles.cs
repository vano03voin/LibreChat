using System.Text.Json;

namespace SeMind.Assistant.Gateway;

/// <summary>Service-owned metadata only; no path here may be writable by a tenant.</summary>
public static class DurableFiles
{
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new BrokerError("metadata_path_invalid", 503);
    }

    public static async Task<FileStream> LockAsync(string path, CancellationToken ct)
    {
        RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11) { await Task.Delay(25, ct); }
        }
    }

    public static async Task<byte[]> ReadBytesAsync(string path, CancellationToken ct)
    {
        RejectLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16384, FileOptions.Asynchronous);
        using var output = new MemoryStream();
        await input.CopyToAsync(output, ct);
        return output.ToArray();
    }

    public static Task WriteAsync<T>(string path, T value, CancellationToken ct) =>
        WriteBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json), ct);

    public static async Task WriteBytesAsync(string path, byte[] data, CancellationToken ct)
    {
        RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(data, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
