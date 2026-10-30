using System.IO.Compression;
using System.Text;
using PKHeX.Core;

namespace Zest.Services.Forge;

/// <summary>
/// PKForge's offline Mystery Gift database (ported from PKForge.App's EventArchive): each
/// generation's archive ships next to Zest in forge-events/, is unpacked once into
/// ~/.cache/zest/forge-events/g{n}, and handed to PKHeX and PKForge's gift index.
/// </summary>
public static class EventArchive
{
    private static int _loadedGeneration = -1;
    private static readonly Lock Gate = new();

    /// <summary>False when the archive is missing or unreadable; the gift list then shows PKHeX's built-in cards only.</summary>
    public static bool EnsureLoaded(int generation)
    {
        if (generation is < 4 or > 9)
            return false;
        lock (Gate)
        {
            if (_loadedGeneration == generation)
                return true;
            try
            {
                var target = Extract(generation);
                EncounterEvent.RefreshMGDB(target);
                PKForge.Engine.EventDatabaseService.IndexArchive(target);
                _loadedGeneration = generation;
                return true;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _loadedGeneration = -1;
                return false;
            }
        }
    }

    private static string Extract(int generation)
    {
        var target = Path.Combine(AppPaths.Cache, "forge-events", $"g{generation}");
        if (Directory.Exists(target) && Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any())
            return target;
        Directory.CreateDirectory(target);

        using var asset = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "forge-events", $"events-g{generation}.bin.gz"));
        using var gz = new GZipStream(asset, CompressionMode.Decompress);
        using var reader = new BinaryReader(gz, Encoding.UTF8, leaveOpen: true);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
            var data = reader.ReadBytes(reader.ReadInt32());
            var path = Path.GetFullPath(Path.Combine(target, name));
            if (!path.StartsWith(root, StringComparison.Ordinal))
                continue; // path traversal guard
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        return target;
    }
}
