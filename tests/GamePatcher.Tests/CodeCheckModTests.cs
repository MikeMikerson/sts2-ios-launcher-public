using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace STS2Mobile.GamePatcher.Tests;

/// <summary>
/// The code check before a download weaves the built-in mods as preparation does: an optional mod that does not fit is named as left
/// out with why, and any other mod that does not fit refuses the build.
/// </summary>
public sealed class CodeCheckModTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("code-check-mods-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    /// <summary>A build's code alone, as the app fetches it before the rest: <c>sts2.dll</c> and the reporting SDK's assemblies beside it.</summary>
    private string CodeOnly(string version)
    {
        string folder = GameAssemblies.Folder(version)!, original = Path.Combine(_work, "code-" + version, GameFolder.Original);
        Directory.CreateDirectory(original);
        foreach (string name in GameProfile.ReportingAssemblies.Append(GameProfile.GameAssembly))
            if (File.Exists(Path.Combine(folder, name))) File.Copy(Path.Combine(folder, name), Path.Combine(original, name));
        return original;
    }

    private static readonly byte[] Dll = Encoding.UTF8.GetBytes("mod code");
    private static readonly byte[] Manifest = Encoding.UTF8.GetBytes("""{"id":"Opt","name":"Optional Mod","has_dll":true}""");

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>One mod whose pin its copy does not match, which stands in for a mod that does not fit this game.</summary>
    private static Func<string, IReadOnlyList<BuiltInModInput>> Mismatched(bool optional)
    {
        string catalog = JsonSerializer.Serialize(new
        {
            mods = new[] { new { id = "Opt", optional, dll = "/nowhere/Opt.dll", manifest = "/nowhere/Opt.json", dllSha256 = new string('0', 64), manifestSha256 = Sha(Manifest) } },
        });
        return original => BuiltInModInput.InOriginalFolder(original, catalog, name => name switch
        {
            "Opt.dll" => new MemoryStream(Dll),
            "Opt.json" => new MemoryStream(Manifest),
            _ => null,
        });
    }

    [GameAssemblyFact("0.113.1")]
    public void AnOptionalModThatDoesNotFitIsLeftOutByItsName()
    {
        var result = GameAssemblyPreparation.CheckCode(CodeOnly("0.113.1"), null, Mismatched(optional: true), new HashSet<string> { "Opt" });
        Assert.Null(result.Reason);
        var left = Assert.Single(result.LeftOut!);
        Assert.Equal(("Opt", "Optional Mod"), (left.Id, left.Name));
        Assert.StartsWith("DLL hash ", left.Reason);
    }

    [GameAssemblyFact("0.113.1")]
    public void AModTheGameCannotStartWithoutThatDoesNotFitRefusesTheBuild()
    {
        var result = GameAssemblyPreparation.CheckCode(CodeOnly("0.113.1"), null, Mismatched(optional: false), new HashSet<string>());
        Assert.StartsWith("the built-in mods could not be prepared for it (Opt: DLL hash ", result.Reason);
    }
}
