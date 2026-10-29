using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSrv;
using LibHac.FsSystem;
using LibHac.Ns;
using LibHac.Spl;
using LibHac.Tools.Es;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Path = System.IO.Path;

namespace Zest.Services;

/// <summary>Reads the real title and icon out of NSP/XCI files using the user's own Switch keys.</summary>
public sealed class SwitchMetadata
{
    private readonly KeySet _keys;

    private SwitchMetadata(KeySet keys) => _keys = keys;

    public static SwitchMetadata? TryCreate(AppConfig config)
    {
        if (!File.Exists(config.ProdKeys))
            return null;
        var keys = KeySet.CreateDefaultKeySet();
        ExternalKeyReader.ReadKeyFile(keys, config.ProdKeys, File.Exists(config.TitleKeys) ? config.TitleKeys : null, null, null);
        return new SwitchMetadata(keys);
    }

    /// <summary>Returns the English title and the JPEG icon bytes, or null for formats it can't read (NSZ/XCZ/NRO).</summary>
    public (string? title, byte[]? icon)? Read(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".nsp" or ".xci"))
            return null;

        using var storage = new LocalStorage(path, FileAccess.Read);
        IFileSystem fs;
        if (ext == ".xci")
        {
            fs = new Xci(_keys, storage).OpenPartition(XciPartitionType.Secure);
        }
        else
        {
            var pfs = new PartitionFileSystem();
            pfs.Initialize(storage).ThrowIfFailure();
            fs = pfs;
            ImportTickets(fs);
        }

        using (fs)
        {
            foreach (var entry in fs.EnumerateEntries("/", "*.nca"))
            {
                using var file = new UniqueRef<IFile>();
                fs.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                var nca = new Nca(_keys, file.Release().AsStorage());
                if (nca.Header.ContentType != NcaContentType.Control)
                    continue;

                using var romfs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                return (ReadTitle(romfs), ReadIcon(romfs));
            }
        }
        return (null, null);
    }

    // NSPs carry their title key in a ticket instead of relying on title.keys.
    private void ImportTickets(IFileSystem fs)
    {
        foreach (var entry in fs.EnumerateEntries("/", "*.tik"))
        {
            using var file = new UniqueRef<IFile>();
            if (fs.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsFailure())
                continue;
            var ticket = new Ticket(file.Get.AsStream());
            var titleKey = ticket.GetTitleKey(_keys);
            if (titleKey is not null)
                _keys.ExternalKeySet.Add(new RightsId(ticket.RightsId), new AccessKey(titleKey));
        }
    }

    private static string? ReadTitle(IFileSystem romfs)
    {
        var bytes = ReadAll(romfs, "/control.nacp");
        if (bytes is null)
            return null;

        var nacp = System.Runtime.InteropServices.MemoryMarshal.Read<ApplicationControlProperty>(bytes);
        // Title[0] is American English; fall back to the first language that has a name.
        for (var i = 0; i < 16; i++)
        {
            var name = nacp.Title[i].NameString.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                return name.Trim();
        }
        return null;
    }

    private static byte[]? ReadIcon(IFileSystem romfs)
    {
        foreach (var lang in new[] { "AmericanEnglish", "BritishEnglish", "Japanese" })
        {
            var icon = ReadAll(romfs, $"/icon_{lang}.dat");
            if (icon is not null)
                return icon;
        }
        return romfs.EnumerateEntries("/", "icon_*.dat").Select(e => ReadAll(romfs, e.FullPath)).FirstOrDefault(b => b is not null);
    }

    private static byte[]? ReadAll(IFileSystem fs, string path)
    {
        using var file = new UniqueRef<IFile>();
        if (fs.OpenFile(ref file.Ref, path.ToU8Span(), OpenMode.Read).IsFailure())
            return null;
        file.Get.GetSize(out var size).ThrowIfFailure();
        var buffer = new byte[size];
        file.Get.Read(out _, 0, buffer).ThrowIfFailure();
        return buffer;
    }
}
