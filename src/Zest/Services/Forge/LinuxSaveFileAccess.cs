using PKForge.Domain;

namespace Zest.Services.Forge;

/// <summary>
/// PKForge's file access on a plain filesystem: a document id is the save's absolute path.
/// Writes land in a temp file next to the save and are renamed over it, so the emulator
/// never sees a half-written save.
/// </summary>
public sealed class LinuxSaveFileAccess : ISaveFileAccess
{
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default) =>
        await File.ReadAllBytesAsync(documentId, cancellationToken);

    public async ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var temp = documentId + ".zest-tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        // Keep the save's permission bits (Eden writes some saves as 0750).
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, File.GetUnixFileMode(documentId));
        File.Move(temp, documentId, overwrite: true);
    }
}
