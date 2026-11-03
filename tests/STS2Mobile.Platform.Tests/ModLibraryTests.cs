namespace STS2Mobile.Platform.Tests;

public sealed class ModLibraryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mod-library-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private string Add(string id, string? name = null)
    {
        string folder = Path.Combine(_root, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, id + ".json"), System.Text.Json.JsonSerializer.Serialize(new { id, name = name ?? id, has_dll = true }));
        File.WriteAllBytes(Path.Combine(folder, id + ".dll"), [1]);
        return folder;
    }

    [Fact]
    public void MissingDirectoryIsAnEmptyLibrary() => Assert.Empty(ModLibrary.Read(Path.Combine(_root, "missing"), false, false));

    [Fact]
    public void OffNeverClaimsPreparationEvenWhenPreviousOutputExists()
    {
        Add("Example", "Example mod");
        var item = Assert.Single(ModLibrary.Read(_root, false, true));
        Assert.Equal("Example mod", item.Name);
        Assert.Equal("Off for this launch", item.Status);
    }

    [Theory]
    [InlineData(false, "Waiting for preparation")]
    [InlineData(true, "Prepared for this launch")]
    public void PreparationStatusComesFromThisLaunch(bool prepared, string expected)
    {
        Add("Example");
        Assert.Equal(expected, Assert.Single(ModLibrary.Read(_root, true, prepared)).Status);
    }

    [Fact]
    public void BrokenManifestRemainsVisibleForDeletion()
    {
        string folder = Add("Broken");
        File.WriteAllText(Path.Combine(folder, "Broken.json"), "{invalid");
        var item = Assert.Single(ModLibrary.Read(_root, false, false));
        Assert.Equal("Broken", item.Id);
        Assert.True(item.Refused);
        Assert.Contains("manifest could not be read", item.Status);
    }

    [Fact]
    public void MissingDeclaredCodeIsRefusedAndTemporaryFoldersAreHidden()
    {
        string folder = Add("Example");
        File.Delete(Path.Combine(folder, "Example.dll"));
        Directory.CreateDirectory(Path.Combine(_root, ".incoming-test"));
        var item = Assert.Single(ModLibrary.Read(_root, true, true));
        Assert.True(item.Refused);
        Assert.Contains("code file is missing", item.Status);
    }
    [Theory]
    [InlineData("{\"id\":\"Example\",\"has_dll\":\"yes\"}", "must be true or false")]
    [InlineData("{\"id\":\"Example\",\"has_pck\":true}", "prepared pack is missing")]
    public void InvalidInputIsListedWithItsReason(string manifest, string expected)
    {
        string folder = Add("Example");
        File.WriteAllText(Path.Combine(folder, "Example.json"), manifest);
        var item = Assert.Single(ModLibrary.Read(_root, true, true));
        Assert.True(item.Refused);
        Assert.Contains(expected, item.Status);
    }
}
