using System.IO.Compression;
using System.Text;

namespace STS2Mobile.Platform.Tests;

public sealed class ModPackageImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("package-import-").FullName;
    private string Inputs => Path.Combine(_root, "inputs");
    public void Dispose() => Directory.Delete(_root, true);
    private string Zip(params (string Name, byte[] Bytes)[] files)
    {
        string file = Path.Combine(_root, Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var (name, bytes) in files) { using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
        return file;
    }
    private static byte[] Manifest(string id, bool dll = true, bool pack = false) => Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\",\"has_dll\":{dll.ToString().ToLowerInvariant()},\"has_pck\":{pack.ToString().ToLowerInvariant()}}}");
    private ModImportResult Import(string path, bool replace = false) => ModPackageImport.Install(path, Inputs, replace, codeConfirmed: true);

    [Fact]
    public void CodeNeedsItsOwnConfirmationBeforeAnythingIsStored()
    {
        string code = Zip(("Mod.json", Manifest("Mod")), ("Mod.dll", [1]));
        var asked = ModPackageImport.Install(code, Inputs, replaceConfirmed: true, codeConfirmed: false);
        Assert.Equal(ModImportStatus.NeedsCodeConfirmation, asked.Status);
        Assert.Contains("Steam sign-in", asked.Message);
        Assert.False(Directory.Exists(Path.Combine(Inputs, "Mod")));
        Assert.Equal(ModImportStatus.Installed, ModPackageImport.Install(code, Inputs, replaceConfirmed: false, codeConfirmed: true).Status);
        string broken = Zip(("Bad.json", Manifest("Bad")));
        Assert.Equal(ModImportStatus.Refused, ModPackageImport.Install(broken, Inputs, false, codeConfirmed: false).Status);
    }

    [Fact]
    public void CodeInputIsStoredWithoutExecutingOrChangingIt()
    {
        byte[] code = [1, 2, 3]; // Metadata validity belongs to preparation, not archive extraction.
        Assert.Equal(ModImportStatus.Installed, Import(Zip(("Mod/Mod.json", Manifest("Mod")), ("Mod/Mod.dll", code))).Status);
        Assert.Equal(code, File.ReadAllBytes(Path.Combine(Inputs, "Mod", "Mod.dll")));
    }

    [Fact]
    public void ReplacementRequiresConfirmationAndRefusalPreservesOldInputs()
    {
        Import(Zip(("Mod.json", Manifest("Mod")), ("Mod.dll", [1])));
        string update = Zip(("Mod.json", Manifest("Mod")), ("Mod.dll", [2]));
        Assert.Equal(ModImportStatus.NeedsReplaceConfirmation, Import(update).Status);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(Inputs, "Mod", "Mod.dll")));
        Assert.Equal(ModImportStatus.Installed, Import(update, true).Status);
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(Inputs, "Mod", "Mod.dll")));
        string bad = Zip(("Mod.json", Manifest("Mod", pack: true)), ("Mod.dll", [3]), ("Mod.pck", [0]));
        Assert.Equal(ModImportStatus.Refused, Import(bad, true).Status);
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(Inputs, "Mod", "Mod.dll")));
        Assert.Single(Directory.GetDirectories(Inputs));
    }

    [Fact]
    public void InterruptedReplacementRestoresPreviousInputs()
    {
        Import(Zip(("Mod.json", Manifest("Mod")), ("Mod.dll", [1])));
        Directory.Move(Path.Combine(Inputs, "Mod"), Path.Combine(Inputs, ".previous-Mod"));
        ModPackageImport.Recover(Inputs);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(Inputs, "Mod", "Mod.dll")));
        Assert.Single(Directory.GetDirectories(Inputs));
    }

    [Fact]
    public void RemovingResourceInputDoesNotDeleteNewCodeManifest()
    {
        string mods = Path.Combine(_root, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "Art"));
        File.WriteAllText(Path.Combine(mods, ".imported-resources"), "Art\n");
        File.WriteAllText(Path.Combine(mods, "Art", "Art.json"), "new code manifest");
        ModPackageImport.PublishResources(Inputs, mods, ["Art"]);
        Assert.Equal("new code manifest", File.ReadAllText(Path.Combine(mods, "Art", "Art.json")));
    }

    [Fact]
    public void ResourceInputCannotReplacePreparedCodeManifest()
    {
        byte[] pack = TestPack.Build(("images/test.png", new byte[] { 1 }));
        Import(Zip(("Art.json", Manifest("Art", dll: false, pack: true)), ("Art.pck", pack)));
        Assert.Throws<InvalidDataException>(() => ModPackageImport.PublishResources(Inputs, Path.Combine(_root, "mods"), ["Art"]));
    }

    [Theory]
    [InlineData("../Mod.dll")]
    [InlineData("/Mod.dll")]
    [InlineData("sub/../../Mod.dll")]
    [InlineData("C:\\Mod.dll")]
    public void TraversalIsRefused(string name)
    {
        Assert.Equal(ModImportStatus.Refused, Import(Zip(("Mod.json", Manifest("Mod")), (name, [1]))).Status);
    }

    [Fact]
    public void DuplicateEntriesAndUndeclaredDependenciesAreRefused()
    {
        Assert.Equal(ModImportStatus.Refused, Import(Zip(("Mod.json", Manifest("Mod")), ("Mod.dll", [1]), ("mod.dll", [2]))).Status);
        Assert.Equal(ModImportStatus.Refused, Import(Zip(("Mod.json", Manifest("Mod")), ("Mod.dll", [1]), ("Helper.dll", [2]))).Status);
    }

    [Theory]
    [InlineData("sts2")]
    [InlineData("STS2.unmodded")]
    [InlineData("System.Runtime")]
    [InlineData("STS2Mobile.Platform")]
    public void HostAssemblyNamesCannotBeImported(string id) =>
        Assert.Equal(ModImportStatus.Refused, Import(Zip(($"{id}.json", Manifest(id)), ($"{id}.dll", [1]))).Status);

    [Fact]
    public void PackOnlyInputIsPublishedAndDeletionRemovesItsManifest()
    {
        byte[] pack = TestPack.Build(("images/test.png", new byte[] { 1, 2 }));
        Assert.Equal(ModImportStatus.Installed, Import(Zip(("Art.json", Manifest("Art", dll: false, pack: true)), ("Art.pck", pack))).Status);
        string mods = Path.Combine(_root, "mods");
        ModPackageImport.PublishResources(Inputs, mods);
        Assert.Equal(pack, File.ReadAllBytes(Path.Combine(mods, "Art", "Art.pck")));
        Assert.Equal(pack, File.ReadAllBytes(Path.Combine(Inputs, "Art", "Art.pck")));
        ModPackageImport.Delete(Inputs, "Art");
        ModPackageImport.PublishResources(Inputs, mods);
        Assert.False(File.Exists(Path.Combine(mods, "Art", "Art.json")));
    }
}
