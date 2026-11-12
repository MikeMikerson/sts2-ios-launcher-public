using System.Security.Cryptography;
using System.Text;
using STS2Mobile.GamePack;

namespace STS2Mobile.GamePack.Tests;

/// <summary>Converting in batches and carrying on after an interruption, on made-up textures with the real encoder.</summary>
public sealed class TextureConversionTests : IDisposable
{
    private const string Pin = "pack-pin";
    private const string Kept = ".godot/imported/kept.png-3.bptc.ctex";
    /// <summary>
    /// What these made-up textures become, from a build whose output matched the Mac's on all the game's textures. It catches a change
    /// to the encoder, but not a missing -ffp-contract=off: these bytes stay the same without it. Only InstalledGameTests catches that.
    /// The texture profile id is written into the overlay manifest, so a new game version's id changes it too.
    /// </summary>
    private const string MadeUpTexturesSha256 = "b70311c48e5add7ed1002764fb767eb4fe540aeb1d9ede5df3462f6cd0bafd37";
    private static readonly TextureConversionOptions Small = new(Workers: 2, LargeThreads: 2, BatchBytes: 64 * 1024, LargeBytes: 48 * 1024);
    private readonly string _work = Directory.CreateTempSubdirectory("texture-conversion-").FullName;
    private readonly string _folder;

    public TextureConversionTests() => _folder = Directory.CreateDirectory(Path.Combine(_work, "game")).FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private string Output => Path.Combine(_folder, TextureConversion.OutputName);
    private string Partial => Output + ".partial";
    private string Journal => Path.Combine(_folder, TextureConversion.JournalName);

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private static string Remap(string kind, string file) => $"[remap]\n\nimporter=\"texture\"\ntype=\"CompressedTexture2D\"\npath.{kind}=\"res://{file}\"\nmetadata={{\n\"imported_formats\": [\"{kind}\"]\n}}\n";

    /// <summary>A .ctex with blocks that come out the same everywhere: smooth endpoints and scattered selectors, so the encoder has real choices to make.</summary>
    private static byte[] Texture(uint format, int size, bool mips, uint seed)
    {
        int blockBytes = format == 17 ? 8 : 16;
        var data = new List<byte>();
        data.AddRange(Encoding.ASCII.GetBytes("GST2"));
        foreach (uint word in new uint[] { 1, (uint)size, (uint)size, 0, 0, 0, 0, 0 }) data.AddRange(BitConverter.GetBytes(word));
        data.AddRange(BitConverter.GetBytes(0u));
        data.AddRange(BitConverter.GetBytes((ushort)size));
        data.AddRange(BitConverter.GetBytes((ushort)size));
        data.AddRange(BitConverter.GetBytes(mips ? 1u : 0u));
        data.AddRange(BitConverter.GetBytes(format));
        uint state = seed;
        byte Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (byte)state;
        }
        for (int mip = size; ; mip = Math.Max(1, mip / 2))
        {
            int blocks = (mip + 3) / 4;
            for (int y = 0; y < blocks; y++)
                for (int x = 0; x < blocks; x++)
                {
                    if (format == 22)
                    {
                        // Mode 6 of BC7: one subset, a smooth pair of endpoints and scattered indices.
                        byte tone = (byte)(x * 255 / blocks);
                        data.AddRange([0x40, tone, (byte)(y * 255 / blocks), (byte)(255 - tone), Next(), Next(), Next(), Next()]);
                        for (int index = 0; index < blockBytes - 8; index++) data.Add(Next());
                        continue;
                    }
                    if (blockBytes == 16)
                    {
                        // DXT5's alpha block, then a colour block like DXT1's.
                        data.AddRange([(byte)(x * 255 / blocks), (byte)(y * 255 / blocks)]);
                        for (int index = 0; index < 6; index++) data.Add(Next());
                    }
                    ushort first = (ushort)(((x * 31 / blocks) << 11) | ((y * 63 / blocks) << 5) | 8);
                    ushort second = (ushort)((((blocks - x) * 31 / blocks) << 11) | ((y * 63 / blocks) << 5) | 24);
                    data.AddRange(BitConverter.GetBytes(Math.Max(first, second)));
                    data.AddRange(BitConverter.GetBytes(Math.Min(first, second)));
                    for (int index = 0; index < 4; index++) data.Add(Next());
                }
            if (!mips || mip == 1) break;
        }
        return data.ToArray();
    }

    private string WritePack(string name, params (string Name, byte[] Data)[] files)
    {
        string path = Path.Combine(_work, name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        var writer = new PackWriter(stream);
        foreach (var (entry, data) in files) writer.Add(entry, data);
        writer.Finish();
        return path;
    }

    /// <summary>Six textures: small ones to go side by side, a large one to go alone, and each source format.</summary>
    private string Game() => WritePack("game.pck",
        ("images/c.png.import", Text(Remap("s3tc", ".godot/imported/c.png-1.s3tc.ctex"))),
        (".godot/imported/c.png-1.s3tc.ctex", Texture(17, 64, mips: true, 11)),
        ("images/a.png.import", Text(Remap("bptc", ".godot/imported/a.png-2.bptc.ctex"))),
        (".godot/imported/a.png-2.bptc.ctex", Texture(22, 64, mips: true, 22)),
        ("images/kept.png.import", Text(Remap("bptc", Kept))),
        (Kept, Texture(22, 8, mips: false, 33)),
        ("images/b.png.import", Text(Remap("s3tc", ".godot/imported/b.png-4.s3tc.ctex"))),
        (".godot/imported/b.png-4.s3tc.ctex", Texture(19, 64, mips: true, 44)),
        ("images/d.png.import", Text(Remap("s3tc", ".godot/imported/d.png-5.s3tc.ctex"))),
        (".godot/imported/d.png-5.s3tc.ctex", Texture(18, 32, mips: true, 55)),
        ("images/big.png.import", Text(Remap("bptc", ".godot/imported/big.png-6.bptc.ctex"))),
        (".godot/imported/big.png-6.bptc.ctex", Texture(22, 256, mips: false, 66)),
        ("images/e.png.import", Text(Remap("bptc", ".godot/imported/e.png-7.bptc.ctex"))),
        (".godot/imported/e.png-7.bptc.ctex", Texture(22, 128, mips: true, 77)),
        ("fonts/font.ttf.import", Text("[remap]\n\nimporter=\"font_data_dynamic\"\npath=\"res://.godot/imported/font.fontdata\"\n")),
        ("addons/sentry/logo.png.import", Text(Remap("bptc", ".godot/imported/logo.png-8.bptc.ctex"))),
        ("project.binary", Text("settings")));

    private TextureConversion Conversion(string pin = Pin, TextureConversionOptions? options = null, Action<int, int>? progress = null) =>
        new(_folder, pin, [Kept], options ?? Small, progress);

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>A run that stops when <paramref name="stopAfter"/> textures are done, as a crash would.</summary>
    private void RunUntil(string game, int stopAfter) =>
        Assert.Throws<IOException>(() => Conversion(progress: (done, _) =>
        {
            if (done >= stopAfter) throw new IOException("stopped");
        }).Run(game));

    [Fact]
    public void ItHoldsTheListThenARemapAndATextureForEach()
    {
        var result = Conversion().Run(Game());
        Assert.Equal(6, result.Total);
        using var pack = new PackReader(Output);
        Assert.Equal(
        [
            GamePackBuilder.TextureManifest,
            "images/a.png.import", ".godot/imported/a.png-2.astc.ctex",
            "images/b.png.import", ".godot/imported/b.png-4.astc.ctex",
            "images/big.png.import", ".godot/imported/big.png-6.astc.ctex",
            "images/c.png.import", ".godot/imported/c.png-1.astc.ctex",
            "images/d.png.import", ".godot/imported/d.png-5.astc.ctex",
            "images/e.png.import", ".godot/imported/e.png-7.astc.ctex",
        ], pack.Entries.Select(entry => entry.Name));
        var entries = pack.Entries.ToDictionary(entry => entry.Name);
        Assert.Equal(Remap("astc", ".godot/imported/a.png-2.astc.ctex").Replace("[\"astc\"]", "[\"bptc\"]"), Encoding.UTF8.GetString(pack.Read(entries["images/a.png.import"])));
        Assert.StartsWith(
            "{\"profile\": \"" + GamePackBuilder.TextureProfile + "\", \"assetPackSha256\": \"pack-pin\", \"textures\": [{\"source\": \".godot/imported/a.png-2.bptc.ctex\", \"import\": \"images/a.png.import\", \"output\": \".godot/imported/a.png-2.astc.ctex\"}, ",
            Encoding.UTF8.GetString(pack.Read(entries[GamePackBuilder.TextureManifest])));
    }

    [Fact]
    public void TheOutputIsWhatThePinSays()
    {
        Conversion().Run(Game());
        Assert.Equal(MadeUpTexturesSha256, Sha(Output));
    }

    [Fact]
    public void TheHeaderNamesAstcAndDxt1Doubles()
    {
        Conversion().Run(Game());
        using var pack = new PackReader(Output);
        byte[] texture = pack.Read(pack.Entries.Single(entry => entry.Name == ".godot/imported/c.png-1.astc.ctex"));
        Assert.Equal(35u, BitConverter.ToUInt32(texture, 36 + 12));
        Assert.Equal(Texture(17, 64, mips: true, 11).Length * 2 - 36 - 16, texture.Length);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(1, 4)]
    public void HowTheCoresAreUsedDoesNotChangeTheBytes(int workers, int largeThreads)
    {
        Conversion(options: Small with { Workers = workers, LargeThreads = largeThreads }).Run(Game());
        Assert.Equal(MadeUpTexturesSha256, Sha(Output));
    }

    [Fact]
    public void ItsPackIsWhatTheBuilderTakes()
    {
        string game = WritePack("full.pck",
            (".godot/uid_cache.bin", Text("uids")),
            (".godot/global_script_class_cache.cfg", Text("classes")),
            ("project.binary", Text("settings")),
            ("scenes/sparks.tscn", Text("[sub_resource type=\"ParticleProcessMaterial\" id=\"M\"]\nparticle_flag_disable_z = true\n\n[node name=\"V\" type=\"Node2D\"]\n")),
            ("images/a.png.import", Text(Remap("bptc", ".godot/imported/a.png-2.bptc.ctex"))),
            (".godot/imported/a.png-2.bptc.ctex", Texture(22, 64, mips: true, 22)),
            ("images/kept.png.import", Text(Remap("bptc", Kept))),
            (Kept, Texture(22, 8, mips: false, 33)));
        Conversion().Run(game);
        string built = Path.Combine(_work, "built.pck");
        GamePackBuilder.Build(game, Output, Pin, built);
        using var pack = new PackReader(built);
        var names = pack.Entries.Select(entry => entry.Name).ToList();
        Assert.Contains(".godot/imported/a.png-2.astc.ctex", names);
        Assert.DoesNotContain(".godot/imported/a.png-2.bptc.ctex", names);
        Assert.Contains(Kept, names);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void AnInterruptedRunCarriesOnAndMakesTheSameBytes(int stopAfter)
    {
        string game = Game();
        RunUntil(game, stopAfter);
        Assert.False(File.Exists(Output));
        Assert.True(File.Exists(Partial));
        var result = Conversion().Run(game);
        // A batch can hold more than one texture, and the last can finish them all.
        Assert.InRange(result.Resumed, stopAfter, 6);
        Assert.False(File.Exists(Partial));
        Assert.Equal(MadeUpTexturesSha256, Sha(Output));
    }

    [Fact]
    public void TornProgressIsDroppedAndNothingAfterItIsTrusted()
    {
        string game = Game();
        RunUntil(game, 3);
        string[] lines = File.ReadAllLines(Journal);
        // Half a texture's lines and half a line, with data past what the journal promises.
        File.WriteAllText(Journal, string.Join('\n', lines[..^1]) + "\n.godot/imported/half");
        File.AppendAllText(Partial, "bytes no line speaks for");
        var result = Conversion().Run(game);
        Assert.Equal((lines.Length - 3) / 2, result.Resumed);
        Assert.Equal(MadeUpTexturesSha256, Sha(Output));
    }

    [Fact]
    public void AnInterruptedResumeCarriesOnAgain()
    {
        string game = Game();
        RunUntil(game, 1);
        RunUntil(game, 3);
        Assert.InRange(Conversion().Run(game).Resumed, 3, 5);
        Assert.Equal(MadeUpTexturesSha256, Sha(Output));
    }

    [Fact]
    public void ALostPartialFileStartsAgain()
    {
        string game = Game();
        RunUntil(game, 3);
        File.Delete(Partial);
        Assert.Equal(0, Conversion().Run(game).Resumed);
        Assert.Equal(MadeUpTexturesSha256, Sha(Output));
    }

    [Fact]
    public void AShortPartialFileStartsAgain()
    {
        string game = Game();
        RunUntil(game, 3);
        using (var stream = File.OpenWrite(Partial)) stream.SetLength(1000);
        Assert.Equal(0, Conversion().Run(game).Resumed);
        Assert.Equal(MadeUpTexturesSha256, Sha(Output));
    }

    [Fact]
    public void AFinishedPackIsNotMadeAgain()
    {
        string game = Game();
        Conversion().Run(game);
        var modified = File.GetLastWriteTimeUtc(Output);
        var again = Conversion().Run(game);
        Assert.True(again.AlreadyDone);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Output));
    }

    [Fact]
    public void ProgressFromAnotherGameIsNotUsed()
    {
        string game = Game();
        RunUntil(game, 3);
        Assert.Equal(0, Conversion(pin: "another pin").Run(game).Resumed);
        Conversion(pin: "another pin").Run(game);
        var again = Conversion(pin: Pin).Run(game);
        Assert.False(again.AlreadyDone);
        Assert.Equal(MadeUpTexturesSha256, Sha(Output));
    }

    [Fact]
    public void ADamagedJournalStartsAgain()
    {
        string game = Game();
        RunUntil(game, 3);
        File.WriteAllText(Journal, "not a journal\n");
        Assert.Equal(0, Conversion().Run(game).Resumed);
        Assert.Equal(MadeUpTexturesSha256, Sha(Output));
    }

    [Fact]
    public void ATextureThatCannotBeConvertedStopsTheRun()
    {
        string game = WritePack("bad.pck",
            ("images/a.png.import", Text(Remap("bptc", ".godot/imported/a.png-2.bptc.ctex"))),
            (".godot/imported/a.png-2.bptc.ctex", Text("not a texture at all, and long enough to read past the header of one, which has 52 bytes")),
            ("images/kept.png.import", Text(Remap("bptc", Kept))),
            (Kept, Texture(22, 8, mips: false, 33)));
        Assert.Throws<PackException>(() => Conversion().Run(game));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void AKeptTextureMustBeInTheGame()
    {
        string game = WritePack("nokept.pck",
            ("images/a.png.import", Text(Remap("bptc", ".godot/imported/a.png-2.bptc.ctex"))),
            (".godot/imported/a.png-2.bptc.ctex", Texture(22, 8, mips: false, 1)));
        Assert.Throws<PackException>(() => Conversion().Run(game));
    }

    [Fact]
    public void ARemapWithTwoBlockCompressedFilesIsRefused()
    {
        string game = WritePack("two.pck",
            ("images/a.png.import", Text(Remap("bptc", ".godot/imported/a.png-2.bptc.ctex") + "path.s3tc=\"res://.godot/imported/a.png-2.s3tc.ctex\"\n")),
            (".godot/imported/a.png-2.bptc.ctex", Texture(22, 8, mips: false, 1)),
            (Kept, Texture(22, 8, mips: false, 33)));
        Assert.Throws<PackException>(() => Conversion().Run(game));
    }
}
