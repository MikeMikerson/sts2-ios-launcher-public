using System.Text.Json;
using STS2Mobile.GamePatcher;
using STS2Mobile.Tools;

// The Mac's way to run the phone's preparation (src/STS2Mobile.GamePatcher), for scripts/prepare-game-files.sh: it needs a
// patched assembly to check the game stub against. The input and any mods must match their pins; the output is a new folder.
const string usage = "Usage: FullGamePatcher stage <installed sts2.dll> <new directory> <mods/builtin.json>";
if (args is not ["stage", _, _, _])
{
    Console.Error.WriteLine(usage);
    return 2;
}
string input = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
string hash = CecilHelpers.Sha256(input);
if (hash != DependencyPins.GameAssemblySha256) throw new InvalidOperationException("Unsupported game assembly SHA-256: " + hash);
var installedBoundary = new DirectoryInfo(Path.GetDirectoryName(input)!);
while (installedBoundary.Parent != null && !installedBoundary.Name.EndsWith(".app")) installedBoundary = installedBoundary.Parent;
if (Directory.Exists(output) || output.StartsWith(installedBoundary.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Output must be a new directory outside the installed app.");
Directory.CreateDirectory(output);
var mods = BuiltInModInput.AtCatalogPaths(File.ReadAllText(args[3]));
// With mods, as the phone prepares it with the launcher's mods switch on.
var built = GameAssemblyBuilder.Build(Path.GetDirectoryName(input)!, mods, output, modsOn: mods.Count > 0);
File.WriteAllText(Path.Combine(output, "profile.json"), JsonSerializer.Serialize(new
{
    profile = "v0.111.0-full-game-integration-v2",
    inputSha256 = hash,
    outputSha256 = CecilHelpers.Sha256(Path.Combine(output, GameProfile.GameAssembly)),
    built.RetainedTypes,
    built.ScriptTypes,
    changes = built.Changes,
    mods = built.Mods.Select(mod => new { mod.Id, mod.SourceSha256, mod.WovenSha256, mod.Report.Patches, mod.Report.SteamworksCalls }),
}, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }) + "\n");
Console.WriteLine($"Prepared the game assembly: {built.Changes.Count} changes, {built.Mods.Count} built-in mod(s).");
return 0;
