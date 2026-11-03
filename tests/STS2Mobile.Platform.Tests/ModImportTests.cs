using System.Text;

namespace STS2Mobile.Platform.Tests;

// The pack check for mods added on the phone: only a script-free Godot pack passes.
public sealed class ModImportTests
{
    private static readonly byte[] Art = [0x52, 0x53, 0x52, 0x43, 1, 2, 3, 4];

    [Fact]
    public void ScriptFreePackPasses()
    {
        using var pack = new MemoryStream(TestPack.Build(
            ("images/card.png.import", Text("[remap]\npath=\"res://.godot/imported/card.ctex\"\n")),
            (".godot/imported/card.ctex", Art)));
        ModImport.CheckPack(pack);
    }

    [Theory]
    [InlineData("a GDScript file", "cannot contain scripts")]
    [InlineData("a compiled script", "cannot contain scripts")]
    [InlineData("a script embedded in a scene", "contains a script (GDScript)")]
    [InlineData("a script name split across read blocks", "contains a script")]
    [InlineData("an extension", "cannot contain scripts")]
    [InlineData("the engine's script list", "needs to start")]
    [InlineData("a replaced app scene", "needs to start")]
    [InlineData("a compressed resource", "compressed resource")]
    [InlineData("an encrypted pack", "encrypted")]
    [InlineData("a file removal", "removes")]
    [InlineData("an older pack format", "pack format 2")]
    [InlineData("a damaged pack", "damaged")]
    [InlineData("not a pack", "not a Godot pack")]
    [InlineData("a pack path out of the game", "invalid path")]
    public void UnsafePackIsRefusedWithItsReason(string problem, string reason)
    {
        byte[] bytes = problem switch
        {
            "a GDScript file" => TestPack.Build(("hack.gd", Text("extends Node"))),
            "a compiled script" => TestPack.Build(("hack.gdc", [1, 2])),
            "a script embedded in a scene" => TestPack.Build(
                ("scenes/menu.tscn", Text("[gd_scene]\n[sub_resource type=\"GDScript\" id=\"1\"]\nscript/source = \"OS.execute()\"\n"))),
            "a script name split across read blocks" => TestPack.Build(("big.res", [.. new byte[65536 - 3], .. Text("GDScript")])),
            "an extension" => TestPack.Build(("x.gdextension", [1])),
            "the engine's script list" => TestPack.Build((".godot/global_script_class_cache.cfg", [1])),
            "a replaced app scene" => TestPack.Build(("Launcher.tscn", [1])),
            "a compressed resource" => TestPack.Build(("x.res", Text("RSCCdata"))),
            "an encrypted pack" => TestPack.Make([("a.png", Art)], packFlags: 1),
            "a file removal" => TestPack.Make([("images/card.png", Art)], fileFlags: 2),
            "an older pack format" => TestPack.Make([("a.png", Art)], format: 2),
            "a damaged pack" => TestPack.Build(("a.png", Art))[..120],
            "not a pack" => Text("hello"),
            "a pack path out of the game" => TestPack.Build(("../x.png", Art)),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };

        using var pack = new MemoryStream(bytes);
        var error = Assert.Throws<InvalidDataException>(() => ModImport.CheckPack(pack));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
}
