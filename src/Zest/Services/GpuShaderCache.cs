using System.Diagnostics;

namespace Zest.Services;

/// <summary>
/// The NVIDIA driver keeps compiled shaders and pipelines in ~/.cache/nvidia/GLCache, capped at 1 GB per app by
/// default and pruned past that. Switch games (Hookshot's Ocarina of Time, Lichii, Eden, the recomps) outgrow it: the
/// cache sat at its cap and Hookshot rebuilt the same ~2,400 pipelines (~19 s) at every start, and things seen
/// before were compiled again in play. Games Zest starts get a 10 GB cache that isn't pruned. Values already set in
/// Zest's own environment win; other drivers ignore these variables.
/// </summary>
public static class GpuShaderCache
{
    public const long SizeBytes = 10L * 1024 * 1024 * 1024;

    public static void Apply(ProcessStartInfo psi)
    {
        Set(psi, "__GL_SHADER_DISK_CACHE", "1");
        Set(psi, "__GL_SHADER_DISK_CACHE_SIZE", SizeBytes.ToString());
        Set(psi, "__GL_SHADER_DISK_CACHE_SKIP_CLEANUP", "1");
    }

    private static void Set(ProcessStartInfo psi, string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            psi.Environment[name] = value;
    }
}
