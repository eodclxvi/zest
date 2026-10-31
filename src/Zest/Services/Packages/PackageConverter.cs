using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Zest.Services.Packages;

/// <summary>
/// Turns nsz-compressed dumps back into standard ones: NSZ → NSP, XCZ → XCI. Every NCA rebuilt from an NCZ is checked
/// against its own name (an NCA's file name is the first half of its SHA-256), so a wrong byte refuses the result.
/// Files that were stored uncompressed are copied as they are, without judging them: some dumps carry files that never
/// matched their names (a Link's Awakening cartridge dump's bundled update does), and copying can't change them.
/// Output goes to a ".part" file that is only renamed into place once everything is written and verified.
/// </summary>
public static class PackageConverter
{
    private const int MediaUnit = 0x200;

    public static bool CanConvert(string path) => OutputExtension(path) is not null;

    public static string? OutputExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".nsz" => ".nsp",
        ".xcz" => ".xci",
        _ => null,
    };

    public static string DefaultOutput(string path) =>
        Path.ChangeExtension(path, OutputExtension(path) ?? throw new ArgumentException("Not an NSZ or XCZ file.", nameof(path)));

    public static void Convert(string input, string output, IProgress<(long done, long total)>? progress, CancellationToken cancel)
    {
        if (File.Exists(output))
            throw new IOException($"{Path.GetFileName(output)} already exists.");

        var partial = output + ".part";
        try
        {
            using (var src = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var dst = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                if (Path.GetExtension(input).Equals(".xcz", StringComparison.OrdinalIgnoreCase))
                    ConvertXcz(src, dst, progress, cancel);
                else
                    ConvertNsz(src, dst, progress, cancel);
            }
            File.Move(partial, output);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    // NSP: a PFS0 of the same files, with each .ncz decompressed to its .nca.
    private static void ConvertNsz(FileStream src, FileStream dst, IProgress<(long, long)>? progress, CancellationToken cancel)
    {
        var (_, entries) = PartitionTable.Read(src, 0);
        var names = entries.Select(e => OutputName(e.Name)).ToList();
        var headerLength = PartitionTable.HeaderLength(false, names, 0x20);
        dst.Write(new byte[headerLength]);

        var total = entries.Sum(e => e.Size);
        long before = 0;
        var files = new List<PartitionFile>();
        foreach (var (entry, name) in entries.Zip(names))
        {
            var offset = dst.Position - headerLength;
            CopyOrDecompress(src, dst, entry, name, p => progress?.Report((before + p, total)), cancel, out _);
            files.Add(new PartitionFile(name, offset, dst.Position - headerLength - offset));
            before += entry.Size;
        }

        dst.Position = 0;
        dst.Write(PartitionTable.Build(false, files, 0x20));
        progress?.Report((total, total));
    }

    // XCI: the card header area kept, then a root HFS0 of the partitions (update, normal, secure, logo), each an HFS0
    // of its files, every hash recomputed for the new layout. The header's layout fields are updated to match; its RSA
    // signature can't be (only the card's maker can sign), which emulators don't check.
    private static void ConvertXcz(FileStream src, FileStream dst, IProgress<(long, long)>? progress, CancellationToken cancel)
    {
        var head = new byte[0x200];
        src.Position = 0;
        src.ReadExactly(head);
        if (Encoding.ASCII.GetString(head, 0x100, 4) != "HEAD")
            throw new InvalidDataException("Not an XCI/XCZ (no HEAD at 0x100).");
        var rootOffset = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(0x130));

        var (_, partitions) = PartitionTable.Read(src, rootOffset);
        var contents = partitions.Select(p => PartitionTable.Read(src, p.Offset).Entries).ToList();
        var total = contents.Sum(c => c.Sum(e => e.Size));

        // Everything before the root partition (header, key area, certificate) as it was.
        src.Position = 0;
        CopyRange(src, dst, rootOffset, cancel);

        var rootLength = PartitionTable.HeaderLength(true, partitions.Select(p => p.Name), MediaUnit);
        dst.Write(new byte[rootLength]);
        var rootData = rootOffset + rootLength;

        long before = 0;
        long secureStart = 0;
        var rootFiles = new List<PartitionFile>();
        foreach (var (partition, files) in partitions.Zip(contents))
        {
            Pad(dst, MediaUnit);
            var start = dst.Position;
            if (partition.Name == "secure")
                secureStart = start;

            var names = files.Select(f => OutputName(f.Name)).ToList();
            var headerLength = PartitionTable.HeaderLength(true, names, MediaUnit);
            dst.Write(new byte[headerLength]);
            var dataStart = start + headerLength;

            var written = new List<PartitionFile>();
            foreach (var (file, name) in files.Zip(names))
            {
                Pad(dst, MediaUnit);
                var offset = dst.Position - dataStart;
                CopyOrDecompress(src, dst, file, name, p => progress?.Report((before + p, total)), cancel, out var head0);
                var size = dst.Position - dataStart - offset;
                var hashed = (uint)Math.Min(file.HashedRegionSize == 0 ? MediaUnit : file.HashedRegionSize, size);
                written.Add(new PartitionFile(name, offset, size, hashed, SHA256.HashData(head0.AsSpan(0, (int)hashed))));
                before += file.Size;
            }
            var end = dst.Position;

            var header = PartitionTable.Build(true, written, MediaUnit);
            dst.Position = start;
            dst.Write(header);
            dst.Position = end;
            rootFiles.Add(new PartitionFile(partition.Name, start - rootData, end - start, (uint)header.Length, SHA256.HashData(header)));
        }
        Pad(dst, MediaUnit);
        var fileEnd = dst.Position;

        var root = PartitionTable.Build(true, rootFiles, MediaUnit);
        dst.Position = rootOffset;
        dst.Write(root);

        // Header layout fields: secure area start and valid data end (media units), root header size and hash.
        if (secureStart > 0)
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0x104), (uint)(secureStart / MediaUnit));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0x118), (uint)(fileEnd / MediaUnit - 1));
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(0x138), root.Length);
        SHA256.HashData(root).CopyTo(head.AsSpan(0x140));
        dst.Position = 0;
        dst.Write(head);
        dst.Position = fileEnd;
        progress?.Report((total, total));
    }

    private static string OutputName(string name) =>
        name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase) ? name[..^4] + ".nca" : name;

    /// <summary>
    /// Writes one container file: .ncz decompressed (and the NCA it becomes must hash to its own name), anything else
    /// copied as it is. Returns the file's first 0x200 bytes (for HFS0 hashes) in <paramref name="first"/>.
    /// </summary>
    private static void CopyOrDecompress(Stream src, Stream dst, PartitionEntry entry, string outName, Action<long> progress,
        CancellationToken cancel, out byte[] first)
    {
        var start = dst.Position;
        if (entry.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
        {
            var hash = Ncz.Decompress(src, entry.Offset, entry.Size, dst, progress, cancel);
            var id = outName.Split('.')[0].ToLowerInvariant();
            if (id.Length == 32 && !hash.StartsWith(id))
                throw new InvalidDataException($"{outName} failed verification: its SHA-256 starts with {hash[..32]}. Nothing was kept.");
        }
        else
        {
            src.Position = entry.Offset;
            CopyRange(src, dst, entry.Size, cancel, progress: progress);
        }

        var end = dst.Position;
        first = new byte[Math.Min(MediaUnit, end - start)];
        dst.Position = start;
        dst.ReadExactly(first);
        dst.Position = end;
    }

    private static void CopyRange(Stream src, Stream dst, long count, CancellationToken cancel,
        IncrementalHash? sha = null, Action<long>? progress = null)
    {
        var buffer = new byte[4 << 20];
        long done = 0;
        while (done < count)
        {
            cancel.ThrowIfCancellationRequested();
            var n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, count - done));
            if (n == 0)
                throw new EndOfStreamException("The file ended early.");
            dst.Write(buffer, 0, n);
            sha?.AppendData(buffer, 0, n);
            done += n;
            progress?.Invoke(done);
        }
    }

    private static void Pad(Stream s, int alignment)
    {
        var rem = (int)(s.Position % alignment);
        if (rem != 0)
            s.Write(new byte[alignment - rem]);
    }
}
