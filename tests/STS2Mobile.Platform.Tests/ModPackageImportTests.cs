using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using STS2Mobile.ModApi;
using System.Text;

namespace STS2Mobile.Platform.Tests;

public sealed class ModPackageImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("package-import-").FullName;
    private sealed class Permissions : Dictionary<string, string>, IRelayPermissions
    {
        public string? Read(string id) => this.GetValueOrDefault(id);
        public void Write(string id, string decision) => this[id] = decision;
    }
    private readonly Permissions _permissions = new();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RelayChoiceFollowsTheInstalledModAndItsHost(bool allow)
    {
        string Package(string id, string host) => Zip((id + ".json", Manifest(id)), (id + ".dll", [1]),
            (id + ".cfg", Encoding.UTF8.GetBytes("relay = wss://" + host)));
        string archive = Package("Mod", "relay.example.test");
        var prompts = new List<string>();
        Task<bool> Ask(ModImportResult question)
        {
            if (question.Status != ModImportStatus.NeedsRelayConfirmation) return Task.FromResult(true);
            prompts.Add(question.RelayHost!);
            Assert.Contains(question.RelayHost!, question.Message);
            return Task.FromResult(allow);
        }
        Assert.Equal(ModImportStatus.Installed, (await PickedModImport.InstallAsync(archive, Inputs, Ask, _permissions))?.Status);
        Assert.Equal(allow ? "relay.example.test" : null, RelaySettings.ApprovedHost(Inputs, "Mod", _permissions));
        await PickedModImport.InstallAsync(archive, Inputs, Ask, _permissions);
        Assert.Single(prompts);
        await PickedModImport.InstallAsync(Package("Other", "relay.example.test"), Inputs, Ask, _permissions);
        await PickedModImport.InstallAsync(Package("Mod", "new.example.test"), Inputs, Ask, _permissions);
        Assert.Equal(new[] { "relay.example.test", "relay.example.test", "new.example.test" }, prompts);
        File.WriteAllText(Path.Combine(Inputs, "Mod", "Mod.cfg"), "relay = wss://another.example.test");
        Assert.Null(RelaySettings.ApprovedHost(Inputs, "Mod", _permissions));
    }

    [Fact]
    public void OnlyTheApprovedCallingAssemblyReceivesItsRelay()
    {
        Func<IRelayTransport?> ModCaller(string id)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(id), AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule(id).DefineType("Caller", TypeAttributes.Public);
            var method = type.DefineMethod("Get", MethodAttributes.Public | MethodAttributes.Static, typeof(IRelayTransport), Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Call, typeof(Multiplayer).GetMethod(nameof(Multiplayer.GetRelay))!);
            il.Emit(OpCodes.Ret);
            return type.CreateType()!.GetMethod("Get")!.CreateDelegate<Func<IRelayTransport?>>();
        }
        var approved = ModCaller("AllowedMod");
        var denied = ModCaller("DeniedMod");
        foreach (string id in new[] { "AllowedMod", "DeniedMod" })
        {
            var archive = Zip((id + ".json", Manifest(id)), (id + ".dll", [1]), (id + ".cfg", "relay = wss://relay.example.test"u8.ToArray()));
            Assert.Equal(ModImportStatus.Installed,
                ModPackageImport.Install(archive, Inputs, false, true, new RelayDecision(id, "relay.example.test", id == "AllowedMod"), _permissions).Status);
        }
        PhoneRelayConnector.Register(Inputs, [approved.Method.Module.Assembly, denied.Method.Module.Assembly], _permissions);
        try
        {
            Assert.NotNull(approved());
            Assert.Null(denied());
            Assert.Null(Multiplayer.GetRelay());
        }
        finally { PhoneRelayConnector.Register(Inputs, [], _permissions); }
    }

    [Fact]
    public void ImportedFilesCannotGrantRelayApproval()
    {
        var archive = Zip(("Mod.json", Manifest("Mod")), ("Mod.dll", [1]), (".relay-decision", "+relay.example.test"u8.ToArray()));
        Assert.Equal(ModImportStatus.Refused, Import(archive).Status);
        archive = Zip(("Mod.json", Manifest("Mod")), ("Mod.dll", [1]), ("Mod.cfg", "relay = wss://relay.example.test"u8.ToArray()));
        foreach (var decision in new[] { new RelayDecision("Other", "relay.example.test", true), new RelayDecision("Mod", "other.example.test", true) })
            Assert.Equal(ModImportStatus.NeedsRelayConfirmation, ModPackageImport.Install(archive, Inputs, false, true, decision, _permissions).Status);
        Assert.Null(RelaySettings.ApprovedHost(Inputs, "Mod", _permissions));
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
