using System.Reflection;
using Tauri.Plugin.DotNet.Generator;

namespace Tauri.Plugin.DotNet.Generator.Tests;

public class BundleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"BundleTests_{Guid.NewGuid():N}");

    public BundleTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    // Any managed assembly will do as stand-ins for a backend and for Tauri.Plugin.DotNet.
    private static readonly string ManagedAssembly = typeof(BundleTests).Assembly.Location;
    private static readonly string OtherManagedAssembly = typeof(BundleWriter).Assembly.Location;

    private string Path_(string name) => Path.Combine(_directory, name);

    /// <summary>A build output folder: backend + runtime config + host assembly (+ symbols for the backend).</summary>
    private string MakeBuildOutput(bool runtimeConfig = true, bool hostAssembly = true, bool symbols = true)
    {
        File.Copy(ManagedAssembly, Path_("Backend.dll"));
        if (runtimeConfig)
            File.WriteAllText(Path_("Backend.runtimeconfig.json"), "{}");
        if (hostAssembly)
            File.Copy(OtherManagedAssembly, Path_("Tauri.Plugin.DotNet.dll"));
        if (symbols)
            File.WriteAllBytes(Path_("Backend.pdb"), [4]);
        return Path_("Backend.dll");
    }

    // The same bytes as GOLDEN in the plugin's src/bundle.rs: both sides pin the format with one literal.
    private static readonly byte[] Golden =
    [
        0x54, 0x44, 0x4E, 0x42, 0x4E, 0x44, 0x4C, 0x31,
        0x01, 0x00, 0x00, 0x00, 0x42,
        0x04, 0x00, 0x00, 0x00,
        0x01, 0x12, 0x00, 0x00, 0x00,
        0x72, 0x75, 0x6E, 0x74, 0x69, 0x6D, 0x65, 0x63, 0x6F, 0x6E, 0x66, 0x69, 0x67, 0x2E, 0x6A, 0x73, 0x6F, 0x6E,
        0x02, 0x00, 0x00, 0x00, 0x7B, 0x7D,
        0x02, 0x05, 0x00, 0x00, 0x00, 0x42, 0x2E, 0x64, 0x6C, 0x6C,
        0x01, 0x00, 0x00, 0x00, 0x03,
        0x02, 0x17, 0x00, 0x00, 0x00,
        0x54, 0x61, 0x75, 0x72, 0x69, 0x2E, 0x50, 0x6C, 0x75, 0x67, 0x69, 0x6E, 0x2E, 0x44, 0x6F, 0x74, 0x4E, 0x65, 0x74, 0x2E, 0x64, 0x6C, 0x6C,
        0x02, 0x00, 0x00, 0x00, 0x01, 0x02,
        0x03, 0x05, 0x00, 0x00, 0x00, 0x42, 0x2E, 0x70, 0x64, 0x62,
        0x01, 0x00, 0x00, 0x00, 0x04,
    ];

    private static BundleWriter.BundleContents GoldenContents(bool shuffled = false)
    {
        var assemblies = new List<(string, byte[])> { ("B.dll", [3]), ("Tauri.Plugin.DotNet.dll", [1, 2]) };
        if (shuffled)
            assemblies.Reverse();
        return new BundleWriter.BundleContents("B", "{}"u8.ToArray(), assemblies, [("B.pdb", [4])]);
    }

    #region Write

    [Fact]
    public void Write_produces_exactly_the_documented_bytes()
    {
        Assert.Equal(Golden, BundleWriter.Write(GoldenContents()));
    }

    [Fact]
    public void Write_orders_entries_by_name_so_the_same_input_gives_the_same_bytes()
    {
        Assert.Equal(Golden, BundleWriter.Write(GoldenContents(shuffled: true)));
    }

    [Fact]
    public void Write_encodes_non_ascii_names_as_utf8_with_a_byte_length()
    {
        var contents = new BundleWriter.BundleContents("Bäck", "{}"u8.ToArray(), [("Tauri.Plugin.DotNet.dll", [1])], []);

        var bytes = BundleWriter.Write(contents);

        // "Bäck" is 5 bytes in UTF-8: the length field says so, not 4 characters.
        Assert.Equal(5u, BitConverter.ToUInt32(bytes, 8));
    }

    #endregion

    #region Collect

    [Fact]
    public void Collect_reads_the_runtime_config_managed_assemblies_and_their_symbols()
    {
        var backend = MakeBuildOutput();
        var warnings = new List<string>();

        var contents = BundleWriter.Collect(backend, warnings);

        Assert.Equal(typeof(BundleTests).Assembly.GetName().Name, contents.BackendName);
        Assert.Equal("{}"u8.ToArray(), contents.RuntimeConfig);
        Assert.Equal(["Backend.dll", "Tauri.Plugin.DotNet.dll"], contents.Assemblies.Select(a => a.FileName).OrderBy(n => n).ToArray());
        Assert.Equal(new byte[] { 4 }, Assert.Single(contents.Symbols).Data);
        Assert.Equal("Backend.pdb", contents.Symbols[0].FileName);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Collect_skips_native_libraries_with_a_warning()
    {
        var backend = MakeBuildOutput();
        File.WriteAllBytes(Path_("native.dll"), [0x4D, 0x5A, 0x00, 0x01, 0x02]); // not a .NET assembly
        var warnings = new List<string>();

        var contents = BundleWriter.Collect(backend, warnings);

        Assert.DoesNotContain(contents.Assemblies, a => a.FileName == "native.dll");
        Assert.Contains(warnings, w => w.Contains("native.dll") && w.Contains("not a managed assembly"));
    }

    [Fact]
    public void Collect_warns_about_folders_with_libraries_it_cannot_embed()
    {
        var backend = MakeBuildOutput();
        Directory.CreateDirectory(Path_("runtimes/win-x64/native"));
        File.WriteAllBytes(Path_("runtimes/win-x64/native/x.dll"), [1]);
        Directory.CreateDirectory(Path_("empty-folder"));
        var warnings = new List<string>();

        BundleWriter.Collect(backend, warnings);

        var warning = Assert.Single(warnings);
        Assert.Contains("'runtimes'", warning);
    }

    [Fact]
    public void Collect_needs_a_runtime_config_and_says_how_to_get_one()
    {
        var backend = MakeBuildOutput(runtimeConfig: false);

        var ex = Assert.Throws<InvalidOperationException>(() => BundleWriter.Collect(backend, []));

        Assert.Contains("Backend.runtimeconfig.json", ex.Message);
        Assert.Contains("EnableDynamicLoading", ex.Message);
    }

    [Fact]
    public void Collect_needs_the_host_assembly_and_says_what_to_check()
    {
        var backend = MakeBuildOutput(hostAssembly: false);

        var ex = Assert.Throws<InvalidOperationException>(() => BundleWriter.Collect(backend, []));

        Assert.Contains("Tauri.Plugin.DotNet.dll", ex.Message);
        Assert.Contains("CopyLocalLockFileAssemblies", ex.Message);
    }

    [Fact]
    public void Collect_works_without_any_symbols()
    {
        var backend = MakeBuildOutput(symbols: false);

        Assert.Empty(BundleWriter.Collect(backend, []).Symbols);
    }

    #endregion

    #region Run

    [Fact]
    public void Run_writes_the_bundle_and_reports_it()
    {
        var backend = MakeBuildOutput();
        var output = Path_("out/backend.tdnbundle");

        var exitCode = BundleWriter.Run([backend, output]);

        Assert.Equal(0, exitCode);
        var bytes = File.ReadAllBytes(output);
        Assert.Equal("TDNBNDL1"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public void Run_leaves_an_up_to_date_bundle_alone_so_cargo_does_not_rebuild()
    {
        var backend = MakeBuildOutput();
        var output = Path_("backend.tdnbundle");
        BundleWriter.Run([backend, output]);
        var marker = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(output, marker);

        BundleWriter.Run([backend, output]);

        Assert.Equal(marker, File.GetLastWriteTimeUtc(output));
    }

    [Fact]
    public void Run_rewrites_the_bundle_when_the_backend_changed()
    {
        var backend = MakeBuildOutput();
        var output = Path_("backend.tdnbundle");
        BundleWriter.Run([backend, output]);
        var before = File.ReadAllBytes(output);

        File.WriteAllText(Path_("Backend.runtimeconfig.json"), "{\"changed\":true}");
        BundleWriter.Run([backend, output]);

        Assert.NotEqual(before, File.ReadAllBytes(output));
    }

    [Fact]
    public void Run_fails_with_a_message_instead_of_writing_a_partial_bundle()
    {
        var backend = MakeBuildOutput(runtimeConfig: false);
        var output = Path_("backend.tdnbundle");

        Assert.Equal(1, BundleWriter.Run([backend, output]));
        Assert.False(File.Exists(output));
        Assert.Equal(1, BundleWriter.Run([Path_("missing.dll"), output]));
        Assert.Equal(1, BundleWriter.Run([backend]));
    }

    [Fact]
    public void Main_routes_the_bundle_command()
    {
        Assert.Equal(1, Program.Main(["bundle"]));
        Assert.Equal(0, Program.Main(["bundle", MakeBuildOutput(), Path_("via-main.tdnbundle")]));
        Assert.True(File.Exists(Path_("via-main.tdnbundle")));
    }

    #endregion
}
