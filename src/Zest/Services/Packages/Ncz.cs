using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;

namespace Zest.Services.Packages;

/// <summary>
/// Turns an NCZ (an NCA compressed by nsz) back into the original NCA. Layout:
/// the NCA's first 0x4000 bytes as they were; "NCZSECTN", a section count, and per section its offset, size, crypto
/// type, key and counter; then either one zstd stream of the rest of the NCA, decrypted, or "NCZBLOCK" and a table of
/// independently compressed blocks. Sections with crypto type 3 or 4 are encrypted again with AES-128-CTR (counter:
/// the section's 8-byte nonce, then offset / 16 as a big-endian 64-bit number), which restores the NCA byte for byte.
/// </summary>
public static class Ncz
{
    private const int HeaderSize = 0x4000;
    private const int Chunk = 4 << 20;

    private sealed record Section(long Offset, long Size, long CryptoType, byte[] Key, byte[] Counter);

    /// <summary>Writes the NCA to <paramref name="output"/> and returns its SHA-256 (lowercase hex).</summary>
    public static string Decompress(Stream input, long start, long length, Stream output, Action<long>? progress, CancellationToken cancel)
    {
        var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var source = new SubStream(input, start, length);
        using var br = new BinaryReader(source, Encoding.ASCII, leaveOpen: true);

        var header = br.ReadBytes(HeaderSize);
        if (header.Length != HeaderSize)
            throw new InvalidDataException("NCZ is shorter than an NCA header.");
        Write(output, sha, header);

        if (Encoding.ASCII.GetString(br.ReadBytes(8)) != "NCZSECTN")
            throw new InvalidDataException("Not an NCZ (no NCZSECTN table).");
        var count = br.ReadInt64();
        if (count is <= 0 or > 64)
            throw new InvalidDataException("NCZ section table is damaged.");
        var sections = new List<Section>();
        for (var i = 0; i < count; i++)
        {
            var offset = br.ReadInt64();
            var size = br.ReadInt64();
            var type = br.ReadInt64();
            br.ReadInt64();
            sections.Add(new Section(offset, size, type, br.ReadBytes(16), br.ReadBytes(16)));
        }

        var emitter = new SectionEmitter(sections, output, sha);
        var afterTable = source.Position;
        var block = Encoding.ASCII.GetString(br.ReadBytes(8)) == "NCZBLOCK";
        if (block)
            DecompressBlocks(br, source, emitter, progress, cancel);
        else
        {
            source.Position = afterTable;
            DecompressStream(source, emitter, progress, cancel);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static void DecompressStream(SubStream source, SectionEmitter emitter, Action<long>? progress, CancellationToken cancel)
    {
        using var z = new DecompressionStream(source, leaveOpen: true);
        var buffer = new byte[Chunk];
        int read;
        while ((read = ReadFull(z, buffer)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            emitter.Emit(buffer.AsSpan(0, read));
            progress?.Invoke(source.Position);
        }
    }

    private static void DecompressBlocks(BinaryReader br, SubStream source, SectionEmitter emitter, Action<long>? progress, CancellationToken cancel)
    {
        br.ReadByte();                  // version
        br.ReadByte();                  // type
        br.ReadByte();                  // unused
        var exponent = br.ReadByte();
        var blocks = br.ReadInt32();
        var total = br.ReadInt64();
        if (exponent is < 12 or > 30 || blocks < 0)
            throw new InvalidDataException("NCZ block table is damaged.");
        var sizes = new int[blocks];
        for (var i = 0; i < blocks; i++)
            sizes[i] = br.ReadInt32();

        var blockSize = 1 << exponent;
        using var zstd = new Decompressor();
        var output = new byte[blockSize];
        long done = 0;
        for (var i = 0; i < blocks; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var expected = (int)Math.Min(blockSize, total - done);
            var data = br.ReadBytes(sizes[i]);
            if (data.Length != sizes[i])
                throw new EndOfStreamException("NCZ ends inside a block.");
            // nsz stores a block raw when compressing doesn't make it smaller, so a full-size block is raw.
            ReadOnlySpan<byte> plain;
            if (data.Length == expected)
                plain = data;
            else
            {
                var n = zstd.Unwrap(data, output);
                if (n != expected)
                    throw new InvalidDataException($"NCZ block {i} decompressed to {n} bytes, expected {expected}.");
                plain = output.AsSpan(0, n);
            }
            emitter.Emit(plain);
            done += plain.Length;
            progress?.Invoke(source.Position);
        }
    }

    private static int ReadFull(Stream s, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }

    private static void Write(Stream output, IncrementalHash sha, ReadOnlySpan<byte> data)
    {
        output.Write(data);
        sha.AppendData(data);
    }

    /// <summary>Writes decompressed bytes in NCA order, encrypting the parts that fall in CTR sections.</summary>
    private sealed class SectionEmitter(List<Section> sections, Stream output, IncrementalHash sha)
    {
        private long _pos = HeaderSize;
        private readonly Dictionary<Section, AesCtr> _ciphers = [];
        private byte[] _scratch = new byte[Chunk];

        public void Emit(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                var section = sections.FirstOrDefault(s => _pos >= s.Offset && _pos < s.Offset + s.Size);
                // Bytes outside every section (padding between them) are written as they are.
                var end = section is not null
                    ? section.Offset + section.Size
                    : sections.Where(s => s.Offset > _pos).Select(s => s.Offset).DefaultIfEmpty(long.MaxValue).Min();
                var take = (int)Math.Min(data.Length, end - _pos);
                var part = data[..take];

                if (section is { CryptoType: 3 or 4 })
                {
                    if (_scratch.Length < take)
                        _scratch = new byte[take];
                    var buf = _scratch.AsSpan(0, take);
                    part.CopyTo(buf);
                    if (!_ciphers.TryGetValue(section, out var aes))
                        _ciphers[section] = aes = new AesCtr(section.Key, section.Counter);
                    aes.Transform(buf, _pos);
                    Write(output, sha, buf);
                }
                else
                {
                    Write(output, sha, part);
                }
                _pos += take;
                data = data[take..];
            }
        }
    }

    /// <summary>AES-128-CTR at any byte offset: keystream block n is AES(nonce[0..8] ‖ big-endian n).</summary>
    private sealed class AesCtr
    {
        private readonly Aes _aes = Aes.Create();
        private readonly byte[] _nonce;
        private byte[] _counters = new byte[64 << 10];
        private byte[] _stream = new byte[64 << 10];

        public AesCtr(byte[] key, byte[] counter)
        {
            _aes.Key = key;
            _nonce = counter[..8];
        }

        public void Transform(Span<byte> data, long offset)
        {
            var done = 0;
            while (done < data.Length)
            {
                var pos = offset + done;
                var skip = (int)(pos & 15);
                var bytes = Math.Min(data.Length - done, _stream.Length - skip);
                var blocks = (skip + bytes + 15) / 16;
                var first = pos >> 4;
                for (var b = 0; b < blocks; b++)
                {
                    var c = _counters.AsSpan(b * 16, 16);
                    _nonce.CopyTo(c);
                    BinaryPrimitives.WriteInt64BigEndian(c[8..], first + b);
                }
                _aes.EncryptEcb(_counters.AsSpan(0, blocks * 16), _stream.AsSpan(0, blocks * 16), PaddingMode.None);
                var ks = _stream.AsSpan(skip, bytes);
                var target = data.Slice(done, bytes);
                for (var i = 0; i < bytes; i++)
                    target[i] ^= ks[i];
                done += bytes;
            }
        }
    }
}

/// <summary>A read-only window [start, start + length) of another stream.</summary>
internal sealed class SubStream(Stream inner, long start, long length) : Stream
{
    private long _pos;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _pos;
        set => _pos = Math.Clamp(value, 0, length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = (int)Math.Min(buffer.Length, length - _pos);
        if (n <= 0)
            return 0;
        inner.Position = start + _pos;
        var read = inner.Read(buffer[..n]);
        _pos += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => length + offset,
        };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
