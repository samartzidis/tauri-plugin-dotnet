using Tauri.Plugin.DotNet.Generator;

namespace Tauri.Plugin.DotNet.Generator.Tests;

public class StaleCleanupTests : IDisposable
{
    private readonly string _tempDir;

    public StaleCleanupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BindingGenTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void DeletesStaleTypescriptFiles()
    {
        var stalePath = Path.Combine(_tempDir, "OldService.ts");
        var keepPath = Path.Combine(_tempDir, "ActiveService.ts");
        File.WriteAllText(stalePath, "// stale");
        File.WriteAllText(keepPath, "// keep");

        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(keepPath)
        };

        StringHelpers.CleanupStaleFiles(_tempDir, generated);

        Assert.False(File.Exists(stalePath), "Stale .ts file should be deleted");
        Assert.True(File.Exists(keepPath), "Generated .ts file should be kept");
    }

    [Fact]
    public void KeepsAllGeneratedFiles()
    {
        var file1 = Path.Combine(_tempDir, "Service1.ts");
        var file2 = Path.Combine(_tempDir, "Service2.ts");
        var file3 = Path.Combine(_tempDir, "models.ts");
        File.WriteAllText(file1, "//");
        File.WriteAllText(file2, "//");
        File.WriteAllText(file3, "//");

        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(file1),
            Path.GetFullPath(file2),
            Path.GetFullPath(file3)
        };

        StringHelpers.CleanupStaleFiles(_tempDir, generated);

        Assert.True(File.Exists(file1));
        Assert.True(File.Exists(file2));
        Assert.True(File.Exists(file3));
    }

    [Fact]
    public void IgnoresNonTypescriptFiles()
    {
        var jsFile = Path.Combine(_tempDir, "script.js");
        var jsonFile = Path.Combine(_tempDir, "config.json");
        var txtFile = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(jsFile, "//");
        File.WriteAllText(jsonFile, "{}");
        File.WriteAllText(txtFile, "hello");

        // Empty generated set — all .ts would be deleted, but there are none
        StringHelpers.CleanupStaleFiles(_tempDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.True(File.Exists(jsFile), ".js file should not be touched");
        Assert.True(File.Exists(jsonFile), ".json file should not be touched");
        Assert.True(File.Exists(txtFile), ".txt file should not be touched");
    }

    [Fact]
    public void DeletesMultipleStaleFiles()
    {
        var stale1 = Path.Combine(_tempDir, "Old1.ts");
        var stale2 = Path.Combine(_tempDir, "Old2.ts");
        var stale3 = Path.Combine(_tempDir, "Old3.ts");
        File.WriteAllText(stale1, "//");
        File.WriteAllText(stale2, "//");
        File.WriteAllText(stale3, "//");

        StringHelpers.CleanupStaleFiles(_tempDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.False(File.Exists(stale1));
        Assert.False(File.Exists(stale2));
        Assert.False(File.Exists(stale3));
    }
}
