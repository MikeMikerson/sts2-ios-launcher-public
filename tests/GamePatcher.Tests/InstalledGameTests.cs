using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using STS2Mobile.GamePatcher;

namespace STS2Mobile.GamePatcher.Tests;

/// <summary>A fact about the real game. Skipped where this Mac has no Steam install of the supported version.</summary>
public sealed class InstalledGameFactAttribute : FactAttribute
{
    public InstalledGameFactAttribute()
    {
        if (InstalledGame.Data == null) Skip = "No Steam install of the supported game version on this Mac.";
    }
}

internal static class InstalledGame
{
    public static readonly string? Data = Find();

    private static string? Find()
    {
        string folder = Environment.GetEnvironmentVariable("STS2_GAME_DATA") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64");
        string game = Path.Combine(folder, GameProfile.GameAssembly);
        return File.Exists(game) && Sha(game) == GameProfile.Current.Inputs[GameProfile.GameAssembly] ? folder : null;
    }

    public static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>The app's preparation as a build without built-in mods makes it, whatever setup fetched into this checkout.</summary>
    public static GameAssemblyPreparation WithoutBuiltIns(string game, bool includeImportedMods = true) =>
        GameAssemblyPreparation.ForApp(game, includeImportedMods, _ => [], new HashSet<string>());
}

/// <summary>
/// The app's preparation on v0.111.0, pinned to the assembly first played on the phone. Update the pin when a patch changes on purpose, and only
/// after a fight on the phone: nothing else tells whether the new assembly plays.
/// </summary>
public sealed class InstalledGameTests : IDisposable
{
    /// <summary>The patched assembly with no mods, byte for byte the one first played on the phone.</summary>
    private const string PlayedSha256 = "4897d2067b695faf5d193a5e0e4ab763c62c75b157f6f6e6c75ecd2b1c4a800a";

    /// <summary>The same with the launcher's mods switch on and no mods: <see cref="PlayedSha256"/> plus the mods agreement.</summary>
    internal const string ModsOnSha256 = "732c0023e6aaad3e17e1cd68e97fb5d77251afe81c9ec73f3a563da7e5f4ad33";

    private readonly string _work = Directory.CreateTempSubdirectory("installed-game-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private string GameFolderWithOnlyWhatThePhoneHas()
    {
        string game = Path.Combine(_work, "game");
        Directory.CreateDirectory(Path.Combine(game, GameFolder.Original));
        foreach (string name in GameProfile.Current.Inputs.Keys) File.Copy(Path.Combine(InstalledGame.Data!, name), Path.Combine(game, GameFolder.Original, name));
        foreach (string name in GameProfile.Current.Unchanged.Keys) File.Copy(Path.Combine(InstalledGame.Data!, name), Path.Combine(game, name));
        return game;
    }

    [QuickRestartImportFact]
    public void ShippedQuickRestartZipPreparesCachesAndCanBeRemovedWithoutMacStaging()
    {
        string game = GameFolderWithOnlyWhatThePhoneHas();
        string inputs = Path.Combine(_work, STS2Mobile.Platform.ModPackageImport.FolderName);
        var imported = STS2Mobile.Platform.ModPackageImport.Install(Environment.GetEnvironmentVariable("STS2_TEST_MOD_ZIP")!, inputs, false, codeConfirmed: true);
        Assert.Equal(STS2Mobile.Platform.ModImportStatus.Installed, imported.Status);
        var prepared = InstalledGame.WithoutBuiltIns(game).Run();
        Assert.Equal(PreparationStatus.Prepared, prepared.Status);
        var mods = STS2Mobile.Platform.BuiltInMods.ParseCatalog(File.ReadAllText(Path.Combine(game, GameFolder.ModCatalog)));
        var mod = Assert.Single(mods);
        Assert.Equal("QuickRestart", mod.Id);
        Assert.Equal(3, mod.Patches);
        Assert.Equal(PlayedSha256, InstalledGame.Sha(Path.Combine(game, "sts2.unmodded.dll")));
        Assert.Equal(PreparationStatus.Cached, InstalledGame.WithoutBuiltIns(game).Run().Status);
        Assert.Equal(PreparationStatus.Prepared, InstalledGame.WithoutBuiltIns(game, includeImportedMods: false).Run().Status);
        Assert.Equal(PlayedSha256, InstalledGame.Sha(Path.Combine(game, "sts2.dll")));
        Assert.Equal(PreparationStatus.Prepared, InstalledGame.WithoutBuiltIns(game).Run().Status);
        STS2Mobile.Platform.ModPackageImport.Delete(inputs, "QuickRestart");
        Assert.Equal(PreparationStatus.Prepared, InstalledGame.WithoutBuiltIns(game).Run().Status);
        Assert.Empty(STS2Mobile.Platform.BuiltInMods.ParseCatalog(File.ReadAllText(Path.Combine(game, GameFolder.ModCatalog))));
        Assert.Equal(PlayedSha256, InstalledGame.Sha(Path.Combine(game, "sts2.dll")));
    }

    [InstalledGameFact]
    public void TheAppsPreparationGivesTheMacPipelinesOutput()
    {
        string game = GameFolderWithOnlyWhatThePhoneHas();
        var result = InstalledGame.WithoutBuiltIns(game).Run();
        Assert.Equal(PreparationStatus.Prepared, result.Status);
        Assert.Equal(ModsOnSha256, result.OutputSha256);
        Assert.Equal(ModsOnSha256, InstalledGame.Sha(Path.Combine(game, GameProfile.GameAssembly)));
        Assert.Equal(PlayedSha256, InstalledGame.Sha(Path.Combine(game, "sts2.unmodded.dll")));
        Assert.Equal("{\n  \"mods\": []\n}\n", File.ReadAllText(Path.Combine(game, GameFolder.ModCatalog)));
        Assert.Equal(PreparationStatus.Cached, InstalledGame.WithoutBuiltIns(game).Run().Status);
    }

    [InstalledGameFact]
    public void TheMacToolsInputFolderGivesTheSameBytes()
    {
        string output = Path.Combine(_work, "mac");
        Directory.CreateDirectory(output);
        GameAssemblyBuilder.Build(InstalledGame.Data!, [], output);
        Assert.Equal(PlayedSha256, InstalledGame.Sha(Path.Combine(output, GameProfile.GameAssembly)));
    }

    [InstalledGameFact]
    public void ModsOffIgnoresABrokenImportedModAndStillPreparesThePlainGame()
    {
        string game = GameFolderWithOnlyWhatThePhoneHas();
        string imported = Path.Combine(_work, "mod-inputs", "Broken");
        Directory.CreateDirectory(imported);
        File.WriteAllText(Path.Combine(imported, "Broken.json"), "{\"id\":\"Broken\",\"has_dll\":true}");
        Assert.Equal(PreparationStatus.Refused, InstalledGame.WithoutBuiltIns(game).Run().Status);
        Assert.Equal(PreparationStatus.Prepared, InstalledGame.WithoutBuiltIns(game, includeImportedMods: false).Run().Status);
        Assert.Equal(PlayedSha256, InstalledGame.Sha(Path.Combine(game, "sts2.unmodded.dll")));
        Assert.Equal(PreparationStatus.Cached, InstalledGame.WithoutBuiltIns(game, includeImportedMods: false).Run().Status);
        Assert.Equal(PreparationStatus.Refused, InstalledGame.WithoutBuiltIns(game).Run().Status);
        Assert.Equal(PlayedSha256, InstalledGame.Sha(Path.Combine(game, "sts2.unmodded.dll")));
    }

    /// <summary>
    /// The interpreter aborts on a local declared as <c>valuetype System.Int32</c> (on Continue). A dump of Cecil's type
    /// names cannot tell that from <c>int32</c>, so this reads the signatures themselves.
    /// </summary>
    [InstalledGameFact]
    public void NoSignatureNamesAPrimitiveAsAValueType()
    {
        string output = Path.Combine(_work, "signatures");
        Directory.CreateDirectory(output);
        GameAssemblyBuilder.Build(InstalledGame.Data!, [], output);
        using var file = new PEReader(File.OpenRead(Path.Combine(output, GameProfile.GameAssembly)));
        var metadata = file.GetMetadataReader();
        var types = new SignatureNames();
        var found = new List<string>();
        void Look(string where, IEnumerable<string> names) => found.AddRange(names.Where(name => name.Contains("valuetype System.Int32")).Select(name => where + ": " + name));
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            string name = metadata.GetString(method.Name);
            var signature = method.DecodeSignature(types, null);
            Look(name, signature.ParameterTypes.Add(signature.ReturnType));
            if (method.RelativeVirtualAddress == 0) continue;
            var body = file.GetMethodBody(method.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil) Look(name, metadata.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(types, null));
        }
        Assert.Empty(found);
    }

    private sealed class SignatureNames : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string element, ArrayShape shape) => element + "[,]";
        public string GetByReferenceType(string element) => element + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string generic, ImmutableArray<string> arguments) => generic + "<" + string.Join(",", arguments) + ">";
        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? context, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodified, bool isRequired) => unmodified;
        public string GetPinnedType(string element) => element;
        public string GetPointerType(string element) => element + "*";
        public string GetPrimitiveType(PrimitiveTypeCode code) => code.ToString();
        public string GetSZArrayType(string element) => element + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawKind)
        {
            var type = reader.GetTypeReference(handle);
            return (rawKind == 17 ? "valuetype " : "class ") + reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    }
}

public sealed class QuickRestartImportFactAttribute : FactAttribute
{
    public QuickRestartImportFactAttribute()
    {
        if (InstalledGame.Data == null || !File.Exists(Environment.GetEnvironmentVariable("STS2_TEST_MOD_ZIP")))
            Skip = "Requires supported game files and STS2_TEST_MOD_ZIP pointing at the inspected Quick Restart release.";
    }
}
