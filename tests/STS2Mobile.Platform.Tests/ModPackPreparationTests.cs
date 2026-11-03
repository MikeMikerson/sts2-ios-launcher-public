using System.Security.Cryptography;
using System.Text;

namespace STS2Mobile.Platform.Tests;

public sealed class ModPackPreparationTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("sts2-pack-preparation-").FullName;
    private string Input => Path.Combine(_folder, "input.pck");
    private string Output => Path.Combine(_folder, "output.pck");
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void UnknownResourcePackIsCopiedWithoutChangingEitherContent()
    {
        byte[] pack = TestPack.Build(("images/card.png", [1, 2, 3]));
        File.WriteAllBytes(Input, pack);
        Assert.Empty(ModPackPreparation.Prepare(Input, Output));
        Assert.Equal(pack, File.ReadAllBytes(Input));
        Assert.Equal(pack, File.ReadAllBytes(Output));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Theory]
    [InlineData("project.binary")]
    [InlineData(".godot/global_script_class_cache.cfg")]
    [InlineData("QuickRestart.cs")]
    [InlineData("lib.dll")]
    public void CleanupDoesNotApplyToUnknownExports(string path)
    {
        byte[] pack = TestPack.Build((path, Encoding.UTF8.GetBytes("export clutter")));
        File.WriteAllBytes(Input, pack);
        File.WriteAllText(Output, "previous valid output");
        Assert.Throws<InvalidDataException>(() => ModPackPreparation.Prepare(Input, Output));
        Assert.Equal(pack, File.ReadAllBytes(Input));
        Assert.Equal("previous valid output", File.ReadAllText(Output));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void CorruptInputLeavesNoPartialOutput()
    {
        File.WriteAllBytes(Input, [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => ModPackPreparation.Prepare(Input, Output));
        Assert.Equal([Input], Directory.GetFiles(_folder));
    }

    [Fact]
    public void InputCannotBeItsOwnOutput()
    {
        byte[] pack = TestPack.Build(("images/card.png", [1, 2, 3]));
        File.WriteAllBytes(Input, pack);
        Assert.Throws<ArgumentException>(() => ModPackPreparation.Prepare(Input, Input));
        Assert.Equal(pack, File.ReadAllBytes(Input));
    }

    [Fact]
    public void InspectedQuickRestartPackRetainsOnlyTheFourResourcesWhenLocalFixtureExists()
    {
        string? root = FindRepository();
        string? fixture = root == null ? null : Path.Combine(root, ".work/mods-spike/download/QuickRestart/QuickRestart/QuickRestart.pck");
        if (fixture == null || !File.Exists(fixture)) return;
        string before = Hash(fixture);
        var omitted = ModPackPreparation.Prepare(fixture, Output);
        Assert.Equal(21, omitted.Count);
        Assert.Contains("project.binary", omitted);
        Assert.Contains("QuickRestart.cs", omitted);
        Assert.Equal(new[]
        {
            ".godot/imported/mod_image.png-262ca25173881db5a1d83649999dd78f.ctex",
            "QuickRestart/localization/eng/gameplay_ui.json",
            "QuickRestart/localization/zhs/gameplay_ui.json",
            "QuickRestart/mod_image.png.import",
        }, TestPack.Paths(File.ReadAllBytes(Output)));
        using var prepared = File.OpenRead(Output);
        ModImport.CheckPack(prepared);
        Assert.Equal(before, Hash(fixture));
        string outputHash = Hash(Output);
        Assert.Equal(omitted, ModPackPreparation.Prepare(fixture, Output));
        Assert.Equal(outputHash, Hash(Output));
    }

    [Theory]
    [InlineData("PROJECT.BINARY")]
    [InlineData(".GODOT/GLOBAL_SCRIPT_CLASS_CACHE.CFG")]
    [InlineData("ADDONS/resource.png")]
    [InlineData("res://Launcher.tscn")]
    [InlineData("images\\..\\project.binary")]
    [InlineData("images/a:card.png")]
    [InlineData("images/card.png\0hidden")]
    [InlineData("./project.binary")]
    [InlineData("images//card.png")]
    [InlineData("RES://images/card.png")]
    public void AmbiguousOrProtectedPackPathsAreRefused(string path)
    {
        using var pack = new MemoryStream(TestPack.Build((path, [1, 2, 3])));
        Assert.Throws<InvalidDataException>(() => ModImport.CheckPack(pack));
    }

    [Theory]
    [InlineData("images/card.png", "images/card.png")]
    [InlineData("images/card.png", "res://images/card.png")]
    [InlineData("images/card.png", "IMAGES/CARD.PNG")]
    public void DuplicateNormalizedPackPathsAreRefused(string first, string second)
    {
        using var pack = new MemoryStream(TestPack.Build((first, [1]), (second, [2])));
        Assert.Contains("duplicate", Assert.Throws<InvalidDataException>(() => ModImport.CheckPack(pack)).Message);
    }

    [Theory]
    [InlineData("base overflow")]
    [InlineData("offset overflow")]
    [InlineData("size overflow")]
    [InlineData("header data")]
    [InlineData("directory data")]
    [InlineData("directory in header")]
    [InlineData("truncated path")]
    [InlineData("unknown pack flags")]
    [InlineData("unknown entry flags")]
    [InlineData("invalid utf8")]
    public void MalformedDirectoryIsRefused(string fault)
    {
        byte[] bytes = TestPack.Build(("images/card.png", [1, 2, 3]));
        using var pack = new MemoryStream(bytes);
        using var writer = new BinaryWriter(pack, Encoding.UTF8, leaveOpen: true);
        long directory = BitConverter.ToInt64(bytes, 32);
        int nameLength = BitConverter.ToInt32(bytes, (int)directory + 4);
        long offsetAt = directory + 8 + nameLength;
        switch (fault)
        {
            case "base overflow": pack.Position = 24; writer.Write(ulong.MaxValue); break;
            case "offset overflow": pack.Position = offsetAt; writer.Write(ulong.MaxValue - 100); break;
            case "size overflow": pack.Position = offsetAt + 8; writer.Write(ulong.MaxValue); break;
            case "header data": pack.Position = 20; writer.Write(0u); break;
            case "directory data": pack.Position = offsetAt; writer.Write(directory - 104); break;
            case "directory in header": pack.Position = 32; writer.Write(40L); break;
            case "truncated path": pack.Position = directory + 4; writer.Write(4096u); break;
            case "unknown pack flags": pack.Position = 20; writer.Write(6u); break;
            case "unknown entry flags": pack.Position = offsetAt + 32; writer.Write(4u); break;
            case "invalid utf8": pack.Position = directory + 8; writer.Write((byte)0xff); break;
        }
        Assert.Throws<InvalidDataException>(() => ModImport.CheckPack(pack));
    }

    [Fact]
    public void ResourceSchemePathsRemainSupported()
    {
        using var pack = new MemoryStream(TestPack.Build(("res://images/card.png", [1, 2, 3])));
        ModImport.CheckPack(pack);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string? FindRepository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "STS2Mobile.slnx"))) return folder.FullName;
        return null;
    }
}
