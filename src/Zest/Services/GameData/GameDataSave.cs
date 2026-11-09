using System.Buffers.Binary;
using System.Text;

namespace Zest.Services.GameData;

/// <summary>
/// One of the engine's save files (progress.sav, caption.sav…): a header, then (hash, value) pairs in sections by
/// type, then the out-of-line data (strings, arrays, blobs) the values point into. Keys are Murmur3 hashes of the
/// names ("PlayerStatus.MaxLife"); bools, ints, floats and enums (the hash of the value's name) are stored in place.
/// </summary>
public sealed class GameDataSave
{
    private readonly byte[] _d;
    private readonly Dictionary<uint, uint> _values = [];

    private GameDataSave(byte[] d)
    {
        _d = d;
        if (d.Length < 0x28 || BinaryPrimitives.ReadUInt32LittleEndian(d) != 0x01020304)
            throw new InvalidDataException("Not a game data save file.");
        var end = Math.Min((int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8)), d.Length);
        for (var o = 0x28; o + 8 <= end; o += 8)
        {
            var hash = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
            if (hash != 0)   // hash 0 starts the next type's section
                _values[hash] = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 4));
        }
    }

    public static GameDataSave Load(string path) => new(File.ReadAllBytes(path));

    public bool Has(string name) => _values.ContainsKey(Murmur3.Hash(name));

    public uint? Raw(string name) => Raw(Murmur3.Hash(name));

    public uint? Raw(uint hash) => _values.TryGetValue(hash, out var v) ? v : null;

    public bool Bool(string name) => Raw(name) is { } v && v != 0;

    public int Int(string name) => Raw(name) is { } v ? (int)v : 0;

    /// <summary>An enum's value as the hash of its name: compare with <see cref="Murmur3.Hash(string)"/>.</summary>
    public uint? Enum(string name) => Raw(name);

    /// <summary>A fixed-size string (String16/32/64/256): the value is its offset.</summary>
    public string? String(string name, int size = 64)
    {
        if (Raw(name) is not { } o || o + size > _d.Length)
            return null;
        var span = _d.AsSpan((int)o, size);
        var len = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(len < 0 ? span : span[..len]);
    }

    /// <summary>A binary blob: the value is the offset of its length-prefixed bytes.</summary>
    public byte[]? Binary(string name) => Binary(Murmur3.Hash(name));

    /// <summary>By hash, for keys whose names aren't known.</summary>
    public byte[]? Binary(uint hash)
    {
        if (Raw(hash) is not { } o || o + 4 > _d.Length)
            return null;
        var len = BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan((int)o));
        return len <= 0 || o + 4 + len > _d.Length ? null : _d[(int)(o + 4)..(int)(o + 4 + len)];
    }
}
