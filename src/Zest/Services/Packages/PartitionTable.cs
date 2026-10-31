using System.Text;

namespace Zest.Services.Packages;

/// <summary>One file in a PFS0 (NSP) or HFS0 (XCI partition) container. Offset is absolute in the container file.</summary>
public sealed record PartitionEntry(string Name, long Offset, long Size, uint HashedRegionSize, byte[]? Hash);

/// <summary>A file to list in a container header being written. Offset is relative to the start of the data area.</summary>
public sealed record PartitionFile(string Name, long Offset, long Size, uint HashedRegionSize = 0, byte[]? Hash = null);

/// <summary>
/// Reads and writes the two Switch container headers. PFS0 (NSP): 0x18-byte entries. HFS0 (XCI): 0x40-byte entries
/// with a SHA-256 of each file's first HashedRegionSize bytes. Both: magic, count, string table size, reserved, entries,
/// string table, then the data.
/// </summary>
public static class PartitionTable
{
    public static (bool IsHfs, List<PartitionEntry> Entries) Read(Stream s, long baseOffset)
    {
        s.Position = baseOffset;
        using var br = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        var magic = Encoding.ASCII.GetString(br.ReadBytes(4));
        var hfs = magic switch
        {
            "HFS0" => true,
            "PFS0" => false,
            _ => throw new InvalidDataException($"Not a PFS0/HFS0 container (magic \"{magic}\")."),
        };
        var count = br.ReadInt32();
        var stringsSize = br.ReadInt32();
        br.ReadInt32();
        if (count is < 0 or > 100_000 || stringsSize is < 0 or > 16 << 20)
            throw new InvalidDataException("Container header is damaged.");

        var raw = new List<(long Offset, long Size, int Name, uint Hashed, byte[]? Hash)>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = br.ReadInt64();
            var size = br.ReadInt64();
            var name = br.ReadInt32();
            if (hfs)
            {
                var hashed = br.ReadUInt32();
                br.ReadInt64();
                raw.Add((offset, size, name, hashed, br.ReadBytes(32)));
            }
            else
            {
                br.ReadInt32();
                raw.Add((offset, size, name, 0, null));
            }
        }

        var strings = br.ReadBytes(stringsSize);
        var dataStart = baseOffset + HeaderSize(hfs, count, stringsSize);
        var entries = raw.Select(r => new PartitionEntry(NameAt(strings, r.Name), dataStart + r.Offset, r.Size, r.Hashed, r.Hash)).ToList();
        return (hfs, entries);
    }

    /// <summary>The size of a header for these names, padded so the data area starts aligned.</summary>
    public static int HeaderLength(bool hfs, IEnumerable<string> names, int alignment) =>
        HeaderSize(hfs, names.Count(), PaddedStringsSize(hfs, names, alignment));

    public static byte[] Build(bool hfs, IReadOnlyList<PartitionFile> files, int alignment)
    {
        var stringsSize = PaddedStringsSize(hfs, files.Select(f => f.Name), alignment);
        var header = new byte[HeaderSize(hfs, files.Count, stringsSize)];
        using var ms = new MemoryStream(header);
        using var bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes(hfs ? "HFS0" : "PFS0"));
        bw.Write(files.Count);
        bw.Write(stringsSize);
        bw.Write(0);

        var nameOffset = 0;
        foreach (var f in files)
        {
            bw.Write(f.Offset);
            bw.Write(f.Size);
            bw.Write(nameOffset);
            if (hfs)
            {
                bw.Write(f.HashedRegionSize);
                bw.Write(0L);
                bw.Write(f.Hash ?? new byte[32]);
            }
            else
            {
                bw.Write(0);
            }
            nameOffset += Encoding.UTF8.GetByteCount(f.Name) + 1;
        }
        foreach (var f in files)
        {
            bw.Write(Encoding.UTF8.GetBytes(f.Name));
            bw.Write((byte)0);
        }
        return header;
    }

    private static int HeaderSize(bool hfs, int count, int stringsSize) => 0x10 + count * (hfs ? 0x40 : 0x18) + stringsSize;

    private static int PaddedStringsSize(bool hfs, IEnumerable<string> names, int alignment)
    {
        var list = names.ToList();
        var strings = list.Sum(n => Encoding.UTF8.GetByteCount(n) + 1);
        var unpadded = HeaderSize(hfs, list.Count, strings);
        var padded = (unpadded + alignment - 1) / alignment * alignment;
        return strings + (padded - unpadded);
    }

    private static string NameAt(byte[] strings, int offset)
    {
        if (offset < 0 || offset >= strings.Length)
            throw new InvalidDataException("Container file name is out of range.");
        var end = Array.IndexOf(strings, (byte)0, offset);
        return Encoding.UTF8.GetString(strings, offset, (end < 0 ? strings.Length : end) - offset);
    }
}
