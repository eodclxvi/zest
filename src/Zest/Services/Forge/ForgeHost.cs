using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;

namespace Zest.Services.Forge;

/// <summary>
/// Wires PKForge's engine services the way its Android app does (MauiProgram), with Zest's
/// data folder in place of the app sandbox: ~/.local/share/zest/forge/{bank,backups,...}.
/// One instance for the app's lifetime; created on first use so the library starts fast.
/// </summary>
public sealed class ForgeHost
{
    private static readonly Lazy<ForgeHost> Instance = new(() => new ForgeHost());

    public static ForgeHost Shared => Instance.Value;

    public static string Root { get; } = Path.Combine(AppPaths.Data, "forge");

    private ForgeHost()
    {
        Directory.CreateDirectory(Root);
        Engine = new SaveEngine();
        Access = new LinuxSaveFileAccess();
        var identities = new JsonSaveIdentityStore(Path.Combine(Root, "save-identities.json"));
        Sessions = new SaveSessionService(Access, Engine, identities);
        Backups = new FileBackupService(Path.Combine(Root, "backups"));
        Writer = new SafeSaveWriter(Engine, Backups, Access, identities);
        Bank = new FileBankService(Path.Combine(Root, "bank"));
        Data = new GameDataService();
        Legality = new LegalityService();
        Legalizer = new LegalizerService();
    }

    public SaveEngine Engine { get; }
    public ISaveFileAccess Access { get; }
    public ISaveSessionService Sessions { get; }
    public IBackupService Backups { get; }
    public ISafeSaveWriter Writer { get; }
    public IBankService Bank { get; }
    public IGameDataService Data { get; }
    public ILegalityService Legality { get; }
    public ILegalizerService Legalizer { get; }
}
