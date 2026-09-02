using SmokeSolver.Cli;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// The running ceiling on the solve-result cache.
/// </summary>
// The startup pruner only drops results older than 30 days, so a container that
// stays up never prunes at all - and the cache key is the clicked target to a
// tenth of a unit, so walking that space writes megabytes per request onto a
// disk this process shares with everything else on the host.
public class CacheBudgetTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "smokesolver-cache-" + Guid.NewGuid().ToString("N"));

    string CacheDir => Path.Combine(root, "data", "cache");

    public CacheBudgetTests() => Directory.CreateDirectory(CacheDir);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    string WriteResult(string name, int bytes, DateTime written)
    {
        var path = Path.Combine(CacheDir, name + ".json");
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    [Fact]
    public void EvictsOldestResultsUntilUnderBudget()
    {
        var now = DateTime.UtcNow;
        var oldest = WriteResult("a", 1000, now.AddHours(-3));
        var middle = WriteResult("b", 1000, now.AddHours(-2));
        var newest = WriteResult("c", 1000, now.AddHours(-1));

        MapRegistry.EnforceCacheBudget(root, budgetBytes: 2000);

        Assert.False(File.Exists(oldest), "the oldest result should have been evicted first");
        Assert.True(File.Exists(middle), "eviction should stop as soon as it is under budget");
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void LeavesEverythingAloneWhenUnderBudget()
    {
        var a = WriteResult("a", 1000, DateTime.UtcNow.AddHours(-3));
        var b = WriteResult("b", 1000, DateTime.UtcNow);

        MapRegistry.EnforceCacheBudget(root, budgetBytes: 1024 * 1024);

        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void NeverEvictsTheCompressedMeshBlobs()
    {
        // Those are the maps themselves: bounded by the number of extracted
        // maps, and minutes of recompression each to rebuild.
        var mesh = Path.Combine(CacheDir, "de_dust2-1234.mesh.br");
        File.WriteAllBytes(mesh, new byte[8000]);
        var result = WriteResult("a", 1000, DateTime.UtcNow.AddHours(-3));

        MapRegistry.EnforceCacheBudget(root, budgetBytes: 500);

        Assert.True(File.Exists(mesh), "mesh blobs are not solve results and must survive eviction");
        Assert.False(File.Exists(result));
    }

    [Fact]
    public void AMissingCacheDirectoryIsNotAnError()
    {
        Directory.Delete(CacheDir);
        MapRegistry.EnforceCacheBudget(root, budgetBytes: 1);
    }
}
