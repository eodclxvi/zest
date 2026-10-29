using System.Buffers.Binary;
using System.Text;

namespace Zest.Services;

/// <summary>
/// Reads the title and icon from a decrypted 3DS game (.cci/.3ds/.cxi, or an installed .app):
/// NCSD → NCCH → ExeFS → "icon" (SMDH).
/// </summary>
public static class N3dsMetadata
{
    private const int MediaUnit = 0x200;
    private const int IconSize = 48;

    public sealed record Info(string? Title, ulong TitleId, bool HasExeFs, byte[]? IconBmp);

    public static Info? Read(string path)
    {
        using var f = File.OpenRead(path);
        var header = new byte[0x200];
        if (f.Read(header) < header.Length)
            return null;

        long ncch = 0;
        if (Magic(header, 0x100) == "NCSD")
        {
            // Partition 0 of a cartridge image is the game itself.
            ncch = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x120)) * (long)MediaUnit;
            f.Seek(ncch, SeekOrigin.Begin);
            if (f.Read(header) < header.Length)
                return null;
        }
        if (Magic(header, 0x100) != "NCCH")
            return null;

        var titleId = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x118));
        var exefs = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x1A0)) * (long)MediaUnit;
        var noCrypto = (header[0x18F] & 0x4) != 0;
        if (exefs == 0)
            return new Info(null, titleId, false, null);
        if (!noCrypto)
            return new Info(null, titleId, true, null);

        f.Seek(ncch + exefs, SeekOrigin.Begin);
        var exefsHeader = new byte[0x200];
        f.ReadExactly(exefsHeader);
        for (var i = 0; i < 10; i++)
        {
            var name = Encoding.ASCII.GetString(exefsHeader, i * 16, 8).TrimEnd('\0');
            if (name != "icon")
                continue;
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(exefsHeader.AsSpan(i * 16 + 8));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(exefsHeader.AsSpan(i * 16 + 12));
            if (size < 0x36C0)
                break;

            var smdh = new byte[size];
            f.Seek(ncch + exefs + 0x200 + offset, SeekOrigin.Begin);
            f.ReadExactly(smdh);
            if (Magic(smdh, 0) != "SMDH")
                break;
            return new Info(SmdhTitle(smdh), titleId, true, IconToBmp(smdh.AsSpan(0x24C0, IconSize * IconSize * 2)));
        }
        return new Info(null, titleId, true, null);
    }

    private static string Magic(byte[] data, int offset) => Encoding.ASCII.GetString(data, offset, 4);

    // 16 language slots of 0x200 bytes starting at 0x8; each begins with a 0x80-byte UTF-16 short title.
    private static string? SmdhTitle(byte[] smdh)
    {
        foreach (var lang in new[] { 1, 0, 2, 3, 4, 5 }) // English first, then Japanese, French, German, Italian, Spanish
        {
            var title = Encoding.Unicode.GetString(smdh, 0x8 + lang * 0x200, 0x80).TrimEnd('\0').Trim();
            if (title.Length > 0)
                return title.Replace('\n', ' ');
        }
        return null;
    }

    /// <summary>The 48×48 icon is RGB565 in 8×8 tiles with Morton-ordered pixels; written out as a top-down 32-bit BMP.</summary>
    private static byte[] IconToBmp(ReadOnlySpan<byte> rgb565)
    {
        const int pixelBytes = IconSize * IconSize * 4;
        var bmp = new byte[54 + pixelBytes];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), IconSize);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -IconSize); // negative height: rows top to bottom
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), pixelBytes);

        const int tilesPerRow = IconSize / 8;
        for (var i = 0; i < IconSize * IconSize; i++)
        {
            int tile = i / 64, m = i % 64;
            var x = (tile % tilesPerRow) * 8 + ((m & 1) | ((m >> 1) & 2) | ((m >> 2) & 4));
            var y = (tile / tilesPerRow) * 8 + (((m >> 1) & 1) | ((m >> 2) & 2) | ((m >> 3) & 4));
            var v = BinaryPrimitives.ReadUInt16LittleEndian(rgb565[(i * 2)..]);

            var o = 54 + (y * IconSize + x) * 4;
            bmp[o] = (byte)((v & 0x1F) * 255 / 31);
            bmp[o + 1] = (byte)(((v >> 5) & 0x3F) * 255 / 63);
            bmp[o + 2] = (byte)((v >> 11) * 255 / 31);
            bmp[o + 3] = 255;
        }
        return bmp;
    }
}
