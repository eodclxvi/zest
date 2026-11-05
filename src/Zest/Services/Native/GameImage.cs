using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Spl;
using LibHac.Tools.Es;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.Ncm;

namespace Zest.Services.Native;

/// <summary>
/// The game files a LAS Recomp-family port reads: the program's exefs/ and its RomFS as one image (romfs.bin),
/// with an update layered on top when one is given (BKTR patch). From an NSP or XCI dump plus, optionally, an
/// update NSP.
/// </summary>
public sealed class GameImage : IDisposable
{
    private readonly List<IDisposable> _open = [];
    private readonly Nca _base;
    private readonly Nca? _patch;

    public GameImage(string prodKeys, string titleKeys, string dump, string? update)
    {
        var keys = KeySet.CreateDefaultKeySet();
        ExternalKeyReader.ReadKeyFile(keys, prodKeys, titleKeys, null, null);
        _base = ProgramNca(keys, OpenDump(keys, dump), LibHac.Ncm.ContentMetaType.Application);
        if (update is not null)
            _patch = ProgramNca(keys, OpenDump(keys, update), LibHac.Ncm.ContentMetaType.Patch);
    }

    /// <summary>The main module's build id (first 8 bytes, hex), read without writing anything.</summary>
    public string MainBuildId()
    {
        using var exefs = OpenExeFs();
        using var f = OpenFile(exefs, "/main");
        Span<byte> id = stackalloc byte[8];
        f.Read(out _, 0x40, id).ThrowIfFailure();
        return Convert.ToHexString(id);
    }

    public long RomFsSize()
    {
        using var romfs = OpenRomFs();
        romfs.GetSize(out var size).ThrowIfFailure();
        return size;
    }

    public void WriteExeFs(string dir)
    {
        Directory.CreateDirectory(dir);
        using var exefs = OpenExeFs();
        foreach (var e in exefs.EnumerateEntries("/", "*"))
        {
            if (e.Type != DirectoryEntryType.File)
                continue;
            using var f = OpenFile(exefs, e.FullPath).AsStream();
            using var o = File.Create(System.IO.Path.Combine(dir, e.Name));
            f.CopyTo(o);
        }
    }

    /// <summary>Writes romfs.bin through a .part file (a cancelled write leaves nothing behind).</summary>
    public void WriteRomFs(string output, IProgress<(long done, long total)>? progress, CancellationToken cancel)
    {
        using var romfs = OpenRomFs();
        romfs.GetSize(out var size).ThrowIfFailure();
        var partial = output + ".part";
        try
        {
            using (var o = File.Create(partial))
            {
                var buf = new byte[8 << 20];
                for (long off = 0; off < size;)
                {
                    cancel.ThrowIfCancellationRequested();
                    var n = (int)Math.Min(buf.Length, size - off);
                    romfs.Read(off, buf.AsSpan(0, n)).ThrowIfFailure();
                    o.Write(buf, 0, n);
                    off += n;
                    progress?.Report((off, size));
                }
            }
            File.Move(partial, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    /// <summary>A DLC package's data (each Data NCA's RomFS) as &lt;dest&gt;/&lt;title id&gt;.romfs, the image the runtime
    /// serves for its add-on mounts. Returns the title ids written.</summary>
    public static List<ulong> WriteAddOn(string prodKeys, string titleKeys, string nsp, string dest, CancellationToken cancel)
    {
        var keys = KeySet.CreateDefaultKeySet();
        ExternalKeyReader.ReadKeyFile(keys, prodKeys, titleKeys, null, null);
        using var storage = new LocalStorage(nsp, FileAccess.Read);
        var pfs = new PartitionFileSystem();
        pfs.Initialize(storage).ThrowIfFailure();
        foreach (var e in pfs.EnumerateEntries("/", "*.tik"))
        {
            var t = new Ticket(OpenFile(pfs, e.FullPath).AsStream());
            var k = t.GetTitleKey(keys);
            if (k is not null)
                keys.ExternalKeySet.Add(new RightsId(t.RightsId), new AccessKey(k));
        }
        Directory.CreateDirectory(dest);
        var written = new List<ulong>();
        foreach (var e in pfs.EnumerateEntries("/", "*.cnmt.nca"))
        {
            using var mfs = new Nca(keys, OpenFile(pfs, e.FullPath).AsStorage()).OpenFileSystem(0, IntegrityCheckLevel.None);
            foreach (var c in mfs.EnumerateEntries("/", "*.cnmt"))
            {
                var cnmt = new Cnmt(OpenFile(mfs, c.FullPath).AsStream());
                if (cnmt.Type != LibHac.Ncm.ContentMetaType.AddOnContent)
                    continue;
                foreach (var ce in cnmt.ContentEntries.Where(x => x.Type == LibHac.Ncm.ContentType.Data))
                {
                    var nca = new Nca(keys, OpenFile(pfs, "/" + Convert.ToHexStringLower(ce.NcaId) + ".nca").AsStorage());
                    using var rs = nca.OpenStorage(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
                    rs.GetSize(out var size).ThrowIfFailure();
                    var output = System.IO.Path.Combine(dest, $"{cnmt.TitleId:X16}.romfs");
                    using (var o = File.Create(output + ".part"))
                    {
                        var buf = new byte[8 << 20];
                        for (long off = 0; off < size;)
                        {
                            cancel.ThrowIfCancellationRequested();
                            var n = (int)Math.Min(buf.Length, size - off);
                            rs.Read(off, buf.AsSpan(0, n)).ThrowIfFailure();
                            o.Write(buf, 0, n);
                            off += n;
                        }
                    }
                    File.Move(output + ".part", output, overwrite: true);
                    written.Add(cnmt.TitleId);
                }
            }
        }
        return written;
    }

    private IFileSystem OpenExeFs() => _patch is null
        ? _base.OpenFileSystem(NcaSectionType.Code, IntegrityCheckLevel.ErrorOnInvalid)
        : _base.OpenFileSystemWithPatch(_patch, NcaSectionType.Code, IntegrityCheckLevel.ErrorOnInvalid);

    private IStorage OpenRomFs() => _patch is null
        ? _base.OpenStorage(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid)
        : _base.OpenStorageWithPatch(_patch, NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);

    private IFileSystem OpenDump(KeySet keys, string path)
    {
        var storage = new LocalStorage(path, FileAccess.Read);
        _open.Add(storage);
        IFileSystem fs;
        if (path.EndsWith(".xci", StringComparison.OrdinalIgnoreCase))
            fs = new Xci(keys, storage).OpenPartition(XciPartitionType.Secure);
        else
        {
            var pfs = new PartitionFileSystem();
            pfs.Initialize(storage).ThrowIfFailure();
            fs = pfs;
        }
        _open.Add(fs);
        foreach (var e in fs.EnumerateEntries("/", "*.tik"))
        {
            var t = new Ticket(OpenFile(fs, e.FullPath).AsStream());
            var k = t.GetTitleKey(keys);
            if (k is not null)
                keys.ExternalKeySet.Add(new RightsId(t.RightsId), new AccessKey(k));
        }
        return fs;
    }

    private static Nca ProgramNca(KeySet keys, IFileSystem fs, LibHac.Ncm.ContentMetaType type)
    {
        foreach (var e in fs.EnumerateEntries("/", "*.cnmt.nca"))
        {
            using var mfs = new Nca(keys, OpenFile(fs, e.FullPath).AsStorage()).OpenFileSystem(0, IntegrityCheckLevel.None);
            foreach (var c in mfs.EnumerateEntries("/", "*.cnmt"))
            {
                var cnmt = new Cnmt(OpenFile(mfs, c.FullPath).AsStream());
                if (cnmt.Type != type)
                    continue;
                var prog = cnmt.ContentEntries.First(x => x.Type == LibHac.Ncm.ContentType.Program);
                return new Nca(keys, OpenFile(fs, "/" + Convert.ToHexStringLower(prog.NcaId) + ".nca").AsStorage());
            }
        }
        throw new InvalidDataException(type == LibHac.Ncm.ContentMetaType.Patch ? "The update file has no program." : "The dump has no game program.");
    }

    private static IFile OpenFile(IFileSystem fs, string path)
    {
        using var u = new UniqueRef<IFile>();
        fs.OpenFile(ref u.Ref, path.ToU8Span(), OpenMode.Read).ThrowIfFailure();
        return u.Release();
    }

    public void Dispose()
    {
        for (var i = _open.Count - 1; i >= 0; i--)
            _open[i].Dispose();
    }
}
