using System.IO.Compression;
using System.Text;

namespace STS2Mobile.Platform.Tests;

public sealed class PickedModImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("picked-import-").FullName;
    private string Inputs => Path.Combine(_root, "inputs");
    public void Dispose() => Directory.Delete(_root, true);

    private string CodeMod(byte dll)
    {
        string file = Path.Combine(_root, Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        using (var manifest = zip.CreateEntry("Mod.json").Open()) manifest.Write(Encoding.UTF8.GetBytes("{\"id\":\"Mod\",\"has_dll\":true,\"has_pck\":false}"));
        using (var code = zip.CreateEntry("Mod.dll").Open()) code.WriteByte(dll);
        return file;
    }

    [Fact]
    public async Task EachConfirmationIsAskedOnceAndThenTheModIsInstalled()
    {
        var asked = new List<ModImportStatus>();
        Task<bool> Yes(ModImportResult question) { asked.Add(question.Status); return Task.FromResult(true); }
        var first = await PickedModImport.InstallAsync(CodeMod(1), Inputs, Yes);
        var second = await PickedModImport.InstallAsync(CodeMod(2), Inputs, Yes);
        Assert.Equal(ModImportStatus.Installed, first!.Status);
        Assert.Equal(ModImportStatus.Installed, second!.Status);
        Assert.Equal([ModImportStatus.NeedsCodeConfirmation, ModImportStatus.NeedsCodeConfirmation, ModImportStatus.NeedsReplaceConfirmation], asked);
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(Inputs, "Mod", "Mod.dll")));
    }

    [Fact]
    public async Task ANoStopsTheImportWithNothingStored()
    {
        var result = await PickedModImport.InstallAsync(CodeMod(1), Inputs, _ => Task.FromResult(false));
        Assert.Null(result);
        Assert.False(Directory.Exists(Path.Combine(Inputs, "Mod")));
    }

    [Fact]
    public void OnlyTheCopyInTheTemporaryFolderIsDeleted()
    {
        string temporary = Directory.CreateDirectory(Path.Combine(_root, "tmp")).FullName;
        string sibling = Directory.CreateDirectory(Path.Combine(_root, "tmp-other")).FullName;
        string copy = Path.Combine(temporary, "mod.zip"), elsewhere = Path.Combine(sibling, "mod.zip");
        File.WriteAllText(copy, "x");
        File.WriteAllText(elsewhere, "x");
        Assert.Null(PickedModImport.Discard(copy, temporary + Path.DirectorySeparatorChar));
        Assert.Null(PickedModImport.Discard(elsewhere, temporary));
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(elsewhere));
        Assert.Null(PickedModImport.Discard(copy, temporary));
    }
}
