using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.Ns;
using LibHac.Spl;
using LibHac.Tools.Es;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.Ncm;
using ContentType = LibHac.Ncm.ContentType;
using Path = System.IO.Path;

namespace Zest.Services.Native;

/// <summary>The base game inside an NSP/XCI, found through its signed Application content meta.</summary>
public sealed record SwitchApplication(ulong TitleId, string DisplayVersion, string ProgramNca, long ProgramSize);

/// <summary>What an NSP/XCI holds: a game, an update or DLC, and the game an update or DLC belongs to.</summary>
public sealed record SwitchFileInfo(Zest.Models.ContentKind Kind, ulong TitleId, ulong BaseTitleId, string? Version);

/// <summary>
/// Finds the base game in an NSP/XCI and extracts its ExeFS and RomFS, using the user's own keys.
/// Bundled updates are ignored on purpose: ports name the base versions they accept.
/// </summary>
public sealed class SwitchContent
{
    private readonly KeySet _keys;

    private SwitchContent(KeySet keys) => _keys = keys;

    public static SwitchContent? TryCreate(AppConfig config)
    {
        if (!File.Exists(config.ProdKeys))
            return null;
        var keys = KeySet.CreateDefaultKeySet();
        ExternalKeyReader.ReadKeyFile(keys, config.ProdKeys, File.Exists(config.TitleKeys) ? config.TitleKeys : null, null, null);
        return new SwitchContent(keys);
    }

    /// <summary>The base application in the dump, or null for formats it can't read (NSZ/XCZ/NRO) or a dump with no base game.</summary>
    public SwitchApplication? Identify(string path)
    {
        using var dump = Open(path);
        return dump is null ? null : FindApplication(dump.FileSystem);
    }

    /// <summary>
    /// Says whether the dump is a game, an update or DLC, from its content meta. A file with a base game is a game
    /// even when it also carries an update (cartridge dumps often do). Null for formats it can't read (NSZ/XCZ/NRO).
    /// </summary>
    public SwitchFileInfo? Describe(string path)
    {
        using var dump = Open(path);
        if (dump is null)
            return null;

        SwitchFileInfo? addOn = null;
        foreach (var cnmt in ContentMetas(dump.FileSystem))
        {
            switch (cnmt.Type)
            {
                case ContentMetaType.Application:
                    return new SwitchFileInfo(Zest.Models.ContentKind.Game, cnmt.TitleId, cnmt.TitleId, null);
                case ContentMetaType.Patch:
                    var control = cnmt.ContentEntries.FirstOrDefault(c => c.Type == ContentType.Control);
                    string? version = null;
                    try
                    {
                        if (control is not null)
                            version = ReadDisplayVersion(dump.FileSystem, NcaName(control.NcaId));
                    }
                    catch (Exception e) when (e is LibHac.Common.HorizonResultException or InvalidDataException)
                    {
                        // An update whose control data can't be read still nests; it just shows its raw version.
                    }
                    addOn ??= new SwitchFileInfo(Zest.Models.ContentKind.Update, cnmt.TitleId, cnmt.ApplicationTitleId,
                        string.IsNullOrEmpty(version) ? $"v{cnmt.TitleVersion.Version}" : version);
                    break;
                case ContentMetaType.AddOnContent:
                    addOn ??= new SwitchFileInfo(Zest.Models.ContentKind.Dlc, cnmt.TitleId, cnmt.ApplicationTitleId, null);
                    break;
            }
        }
        return addOn;
    }

    private IEnumerable<Cnmt> ContentMetas(IFileSystem fs)
    {
        foreach (var entry in fs.EnumerateEntries("/", "*.cnmt.nca"))
        {
            using var meta = OpenNca(fs, entry.Name);
            using var metaFs = meta.Nca.OpenFileSystem(0, IntegrityCheckLevel.None);
            foreach (var cnmtEntry in metaFs.EnumerateEntries("/", "*.cnmt"))
            {
                using var cnmtFile = OpenFile(metaFs, cnmtEntry.FullPath);
                yield return new Cnmt(cnmtFile.AsStream());
            }
        }
    }

    /// <summary>
    /// Writes exefs/ and romfs/ of the base game into <paramref name="destination"/>. Everything goes into a
    /// ".partial" folder first, which is renamed only once every file is written, so an interrupted extraction
    /// never looks complete.
    /// </summary>
    public void Extract(string path, string destination, IProgress<(long done, long total)>? progress, CancellationToken cancel)
    {
        using var dump = Open(path) ?? throw new InvalidDataException("Zest can only extract NSP and XCI files.");
        var app = FindApplication(dump.FileSystem) ?? throw new InvalidDataException("No base game was found in this file.");

        var staging = destination + ".partial";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        using var nca = OpenNca(dump.FileSystem, app.ProgramNca);
        using var exefs = nca.Nca.OpenFileSystem(NcaSectionType.Code, IntegrityCheckLevel.ErrorOnInvalid);
        using var romfs = nca.Nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);

        try
        {
            var total = TotalSize(exefs) + TotalSize(romfs);
            long done = 0;
            CopyTree(exefs, Path.Combine(staging, "exefs"), total, ref done, progress, cancel);
            CopyTree(romfs, Path.Combine(staging, "romfs"), total, ref done, progress, cancel);
        }
        catch
        {
            // Don't leave gigabytes of a half-written copy behind.
            Directory.Delete(staging, recursive: true);
            throw;
        }

        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.Move(staging, destination);
    }

    private SwitchApplication? FindApplication(IFileSystem fs)
    {
        foreach (var cnmt in ContentMetas(fs))
        {
            if (cnmt.Type != ContentMetaType.Application)
                continue;

            var program = cnmt.ContentEntries.FirstOrDefault(c => c.Type == ContentType.Program);
            var control = cnmt.ContentEntries.FirstOrDefault(c => c.Type == ContentType.Control);
            if (program is null || control is null)
                continue;

            return new SwitchApplication(cnmt.TitleId, ReadDisplayVersion(fs, NcaName(control.NcaId)),
                NcaName(program.NcaId), program.Size);
        }
        return null;
    }

    private string ReadDisplayVersion(IFileSystem fs, string controlNca)
    {
        using var nca = OpenNca(fs, controlNca);
        using var romfs = nca.Nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
        using var file = OpenFile(romfs, "/control.nacp");
        file.GetSize(out var size).ThrowIfFailure();
        var bytes = new byte[size];
        file.Read(out _, 0, bytes).ThrowIfFailure();
        var nacp = System.Runtime.InteropServices.MemoryMarshal.Read<ApplicationControlProperty>(bytes);
        return nacp.DisplayVersionString.ToString().Trim();
    }

    private static string NcaName(byte[] id) => Convert.ToHexStringLower(id) + ".nca";

    private OpenedNca OpenNca(IFileSystem fs, string name)
    {
        var file = OpenFile(fs, "/" + name);
        return new OpenedNca(new Nca(_keys, file.AsStorage()), file);
    }

    // Nca isn't disposable itself; this closes the NCA file it reads from.
    private sealed class OpenedNca(Nca nca, IFile file) : IDisposable
    {
        public Nca Nca { get; } = nca;

        public void Dispose() => file.Dispose();
    }

    private static IFile OpenFile(IFileSystem fs, string path)
    {
        using var file = new UniqueRef<IFile>();
        fs.OpenFile(ref file.Ref, path.ToU8Span(), OpenMode.Read).ThrowIfFailure();
        return file.Release();
    }

    private static long TotalSize(IFileSystem fs) =>
        fs.EnumerateEntries("/", "*", SearchOptions.RecurseSubdirectories)
            .Where(e => e.Type == DirectoryEntryType.File)
            .Sum(e => e.Size);

    private static void CopyTree(IFileSystem fs, string target, long total, ref long done,
        IProgress<(long, long)>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(target);
        var buffer = new byte[4 << 20];
        foreach (var entry in fs.EnumerateEntries("/", "*", SearchOptions.RecurseSubdirectories))
        {
            cancel.ThrowIfCancellationRequested();
            var local = Path.Combine(target, entry.FullPath.TrimStart('/'));
            if (entry.Type == DirectoryEntryType.Directory)
            {
                Directory.CreateDirectory(local);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            using var source = OpenFile(fs, entry.FullPath);
            using var output = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            for (long offset = 0; offset < entry.Size;)
            {
                cancel.ThrowIfCancellationRequested();
                var chunk = (int)Math.Min(buffer.Length, entry.Size - offset);
                source.Read(out var read, offset, buffer.AsSpan(0, chunk)).ThrowIfFailure();
                if (read == 0)
                    throw new EndOfStreamException($"{entry.FullPath} ended early.");
                output.Write(buffer, 0, (int)read);
                offset += read;
                done += read;
                progress?.Report((done, total));
            }
        }
    }

    // Owns the storage and partition so both are released together.
    private sealed class Dump(IStorage storage, IFileSystem fileSystem) : IDisposable
    {
        public IFileSystem FileSystem { get; } = fileSystem;

        public void Dispose()
        {
            FileSystem.Dispose();
            storage.Dispose();
        }
    }

    private Dump? Open(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".nsp" or ".xci"))
            return null;

        var storage = new LocalStorage(path, FileAccess.Read);
        try
        {
            if (ext == ".xci")
                return new Dump(storage, new Xci(_keys, storage).OpenPartition(XciPartitionType.Secure));

            var pfs = new PartitionFileSystem();
            pfs.Initialize(storage).ThrowIfFailure();
            ImportTickets(pfs);
            return new Dump(storage, pfs);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
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
}
