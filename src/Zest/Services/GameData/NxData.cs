using System.Buffers.Binary;
using System.Text;
using ZstdSharp;

namespace Zest.Services.GameData;

/// <summary>
/// Readers for the file formats of Nintendo's newer Switch engine (Tears of the Kingdom, and the 2026 Ocarina of Time
/// on Switch 2 built on it): Murmur3 key hashes, SARC archives, BYML documents, MSBT message files and the
/// dictionary-compressed ".zs" files. Read-only, and only what Zest's progress pages need.
/// </summary>
public static class Murmur3
{
    /// <summary>MurmurHash3 x86 32-bit, seed 0, of the UTF-8 bytes: how the engine names its save data and enum values.</summary>
    public static uint Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    public static uint Hash(ReadOnlySpan<byte> data)
    {
        const uint c1 = 0xcc9e2d51, c2 = 0x1b873593;
        uint h = 0;
        var blocks = data.Length / 4;
        for (var i = 0; i < blocks; i++)
        {
            var k = BinaryPrimitives.ReadUInt32LittleEndian(data[(4 * i)..]);
            k *= c1; k = uint.RotateLeft(k, 15); k *= c2;
            h ^= k; h = uint.RotateLeft(h, 13); h = h * 5 + 0xe6546b64;
        }
        var tail = data[(4 * blocks)..];
        uint t = 0;
        switch (tail.Length)
        {
            case 3: t ^= (uint)tail[2] << 16; goto case 2;
            case 2: t ^= (uint)tail[1] << 8; goto case 1;
            case 1:
                t ^= tail[0]; t *= c1; t = uint.RotateLeft(t, 15); t *= c2; h ^= t;
                break;
        }
        h ^= (uint)data.Length;
        h ^= h >> 16; h *= 0x85ebca6b; h ^= h >> 13; h *= 0xc2b2ae35; h ^= h >> 16;
        return h;
    }
}

/// <summary>SARC: a flat archive of named files.</summary>
public static class Sarc
{
    public static Dictionary<string, byte[]> Read(byte[] d)
    {
        if (d.Length < 0x14 || Encoding.ASCII.GetString(d, 0, 4) != "SARC")
            throw new InvalidDataException("Not a SARC archive.");
        var big = d[6] == 0xFE && d[7] == 0xFF;
        uint U32(int o) => big ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
        ushort U16(int o) => big ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
        int header = U16(4);
        var dataStart = (int)U32(0xC);
        var o = header;
        if (Encoding.ASCII.GetString(d, o, 4) != "SFAT")
            throw new InvalidDataException("SARC without SFAT.");
        int count = U16(o + 6);
        o += 12;
        var entries = new (uint attr, int start, int end)[count];
        for (var i = 0; i < count; i++)
            entries[i] = (U32(o + 16 * i + 4), (int)U32(o + 16 * i + 8), (int)U32(o + 16 * i + 12));
        o += 16 * count;
        if (Encoding.ASCII.GetString(d, o, 4) != "SFNT")
            throw new InvalidDataException("SARC without SFNT.");
        o += 8;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (attr, start, end) in entries)
        {
            var nameAt = o + (int)(attr & 0xFFFF) * 4;
            var nameEnd = Array.IndexOf(d, (byte)0, nameAt);
            files[Encoding.UTF8.GetString(d, nameAt, nameEnd - nameAt)] = d[(dataStart + start)..(dataStart + end)];
        }
        return files;
    }
}

/// <summary>The romfs's ".zs" files: zstd, most with one of the dictionaries in Pack/ZsDic.pack.zs.</summary>
public sealed class ZsReader
{
    private readonly List<byte[]> _dictionaries;

    public ZsReader(string romfs)
    {
        using var plain = new Decompressor();
        var pack = plain.Unwrap(File.ReadAllBytes(Path.Combine(romfs, "Pack", "ZsDic.pack.zs"))).ToArray();
        _dictionaries = [.. Sarc.Read(pack).Values];
    }

    public byte[] Read(string path)
    {
        var raw = File.ReadAllBytes(path);
        // The frame names the dictionary it needs, but trying them is simpler and there are only a few
        foreach (var dict in _dictionaries.Prepend(null))
        {
            try
            {
                using var z = new Decompressor();
                if (dict is not null)
                    z.LoadDictionary(dict);
                return z.Unwrap(raw).ToArray();
            }
            catch (ZstdException)
            {
            }
        }
        throw new InvalidDataException($"Couldn't decompress {Path.GetFileName(path)}.");
    }
}

/// <summary>BYML (versions 2-7, either byte order) as plain .NET values: dictionaries, lists, strings, numbers.</summary>
public sealed class Byml
{
    private readonly byte[] _d;
    private readonly bool _big;
    private readonly List<string> _keys;
    private readonly List<string> _strings;

    private Byml(byte[] d)
    {
        _d = d;
        _big = d[0] == (byte)'B' && d[1] == (byte)'Y';
        if (!_big && !(d[0] == (byte)'Y' && d[1] == (byte)'B'))
            throw new InvalidDataException("Not a BYML document.");
        _keys = StringTable((int)U32(4));
        _strings = StringTable((int)U32(8));
    }

    public static object? Parse(byte[] d)
    {
        var b = new Byml(d);
        var root = (int)b.U32(12);
        return root == 0 ? null : b.Container(root);
    }

    private uint U32(int o) => _big ? BinaryPrimitives.ReadUInt32BigEndian(_d.AsSpan(o)) : BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(o));
    private int U24(int o) => _big ? _d[o] << 16 | _d[o + 1] << 8 | _d[o + 2] : _d[o] | _d[o + 1] << 8 | _d[o + 2] << 16;

    private List<string> StringTable(int o)
    {
        if (o == 0)
            return [];
        var n = U24(o + 1);
        var list = new List<string>(n);
        for (var i = 0; i < n; i++)
        {
            var s = o + (int)U32(o + 4 + 4 * i);
            list.Add(Encoding.UTF8.GetString(_d, s, Array.IndexOf(_d, (byte)0, s) - s));
        }
        return list;
    }

    private object? Value(byte type, uint v) => type switch
    {
        0xA0 => _strings[(int)v],
        0xD0 => v != 0,
        0xD1 => (int)v,
        0xD2 => BitConverter.UInt32BitsToSingle(v),
        0xD3 => v,
        0xD4 => (long)U64((int)v),
        0xD5 => U64((int)v),
        0xD6 => BitConverter.UInt64BitsToDouble(U64((int)v)),
        0xFF => null,
        0xA1 => null, // binary blobs: not needed here
        _ => Container((int)v),
    };

    private ulong U64(int o) => _big ? BinaryPrimitives.ReadUInt64BigEndian(_d.AsSpan(o)) : BinaryPrimitives.ReadUInt64LittleEndian(_d.AsSpan(o));

    private object Container(int o)
    {
        var type = _d[o];
        var n = U24(o + 1);
        switch (type)
        {
            case 0xC0:
            {
                var list = new List<object?>(n);
                var values = o + 4 + ((n + 3) & ~3);
                for (var i = 0; i < n; i++)
                    list.Add(Value(_d[o + 4 + i], U32(values + 4 * i)));
                return list;
            }
            case 0xC1:
            {
                var dict = new Dictionary<string, object?>(n, StringComparer.Ordinal);
                for (var i = 0; i < n; i++)
                {
                    var e = o + 4 + 8 * i;
                    dict[_keys[U24(e)]] = Value(_d[e + 3], U32(e + 4));
                }
                return dict;
            }
            case 0x20 or 0x21:
            {
                // Hash-keyed maps (v7): entries, then one type byte per entry
                var keySize = type == 0x20 ? 4 : 8;
                var dict = new Dictionary<string, object?>(n, StringComparer.Ordinal);
                var types = o + 4 + (keySize + 4) * n;
                for (var i = 0; i < n; i++)
                {
                    var e = o + 4 + (keySize + 4) * i;
                    var key = keySize == 4 ? U32(e) : U64(e);
                    dict[key.ToString()] = Value(_d[types + i], U32(e + keySize));
                }
                return dict;
            }
            default:
                throw new InvalidDataException($"BYML node type {type:x2} isn't supported.");
        }
    }
}

/// <summary>MSBT: a message file, label → text. Control tags (colours, icons) are dropped; line breaks kept.</summary>
public static class Msbt
{
    public static Dictionary<string, string> Read(byte[] d)
    {
        if (Encoding.ASCII.GetString(d, 0, 8) != "MsgStdBn")
            throw new InvalidDataException("Not an MSBT file.");
        var big = d[8] == 0xFE && d[9] == 0xFF;
        var utf8 = d[0xC] == 0;
        uint U32(byte[] b, int o) => big ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        ushort U16(byte[] b, int o) => big ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));

        int sections = U16(d, 0xE);
        var o = 0x20;
        byte[]? lbl = null, txt = null;
        for (var s = 0; s < sections && o + 16 <= d.Length; s++)
        {
            var magic = Encoding.ASCII.GetString(d, o, 4);
            var size = (int)U32(d, o + 4);
            var body = d[(o + 16)..(o + 16 + size)];
            if (magic == "LBL1") lbl = body;
            else if (magic == "TXT2") txt = body;
            o = (o + 16 + size + 15) & ~15;
        }
        if (lbl is null || txt is null)
            return [];

        var labels = new Dictionary<int, string>();
        var groups = (int)U32(lbl, 0);
        for (var g = 0; g < groups; g++)
        {
            var count = (int)U32(lbl, 4 + 8 * g);
            var p = (int)U32(lbl, 8 + 8 * g);
            for (var i = 0; i < count; i++)
            {
                int len = lbl[p];
                var name = Encoding.ASCII.GetString(lbl, p + 1, len);
                labels[(int)U32(lbl, p + 1 + len)] = name;
                p += 5 + len;
            }
        }

        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        var n = (int)U32(txt, 0);
        for (var i = 0; i < n; i++)
        {
            var start = (int)U32(txt, 4 + 4 * i);
            var end = i + 1 < n ? (int)U32(txt, 8 + 4 * i) : txt.Length;
            messages[labels.GetValueOrDefault(i, i.ToString())] = utf8 ? Utf8Text(txt, start, end, big) : Utf16Text(txt, start, end, big);
        }
        return messages;
    }

    private static string Utf8Text(byte[] t, int j, int end, bool big)
    {
        var bytes = new List<byte>();
        while (j < end)
        {
            var c = t[j];
            if (c == 0x0E)  // tag: group, type, parameter size (u16 each), parameters
            {
                var size = big ? BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(j + 5)) : BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(j + 5));
                j += 7 + size;
                continue;
            }
            if (c == 0x0F) { j += 5; continue; }
            if (c == 0) break;
            bytes.Add(c);
            j++;
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static string Utf16Text(byte[] t, int j, int end, bool big)
    {
        var sb = new StringBuilder();
        ushort U16(int o) => big ? BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(o)) : BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(o));
        while (j + 1 < end)
        {
            var c = U16(j);
            if (c == 0x0E) { j += 8 + U16(j + 6); continue; }
            if (c == 0x0F) { j += 6; continue; }
            if (c == 0) break;
            sb.Append((char)c);
            j += 2;
        }
        return sb.ToString();
    }
}
