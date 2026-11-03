using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using STS2Mobile.Tools;

const string usage = "Usage: SteamAotPatcher model-roots <SteamKit2.dll> <output.json>\n"
    + "       SteamAotPatcher stage <SteamKit2.dll> <new output.dll> <STS2Mobile.SteamWire.dll>";
if (args is not ["model-roots", _, _] and not ["stage", _, _, _])
{
    Console.Error.WriteLine(usage);
    return 2;
}
string hash = CecilHelpers.Sha256(args[1]);
if (!DependencyPins.SteamKit2Sha256.Contains(hash))
    throw new InvalidOperationException("Unsupported SteamKit2 assembly.");
using var assembly = AssemblyDefinition.ReadAssembly(args[1]);
if (args[0] == "stage")
    Stage(assembly, hash, args[1], args[2], args[3]);
else
    InventoryRoots(assembly, hash, args[2]);
return 0;

static void Stage(AssemblyDefinition assembly, string hash, string input, string output, string steamWirePath)
{
    // The ProtoBuf.Serializer calls SteamKit2 3.4.0 makes, each with a static SteamWire adapter.
    const int expectedSerializationCalls = 22;
    // SteamClient's constructor is the only reader of the process start time, which iOS does not provide.
    const int expectedStartTimeReads = 1;
    if (File.Exists(output) || Path.GetFullPath(output).StartsWith(Path.GetDirectoryName(Path.GetFullPath(input))! + Path.DirectorySeparatorChar))
        throw new InvalidOperationException("Use a new staged file outside the package cache.");
    var methods = CecilHelpers.Flatten(assembly.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
    using var wire = AssemblyDefinition.ReadAssembly(steamWirePath);
    var adapters = wire.MainModule.GetType("STS2Mobile.SteamWire.SteamWire").Methods;
    var changes = new List<object>();
    foreach (var method in methods)
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference target || target.DeclaringType.FullName != "ProtoBuf.Serializer") continue;
            if (instruction.OpCode != OpCodes.Call || target is not GenericInstanceMethod original)
                throw new InvalidOperationException("Unexpected protobuf call: " + target.FullName);
            var adapter = adapters.Single(m => m.Name == original.Name && m.Parameters.Count == original.Parameters.Count);
            var replacement = new GenericInstanceMethod(assembly.MainModule.ImportReference(adapter));
            foreach (var argument in original.GenericArguments) replacement.GenericArguments.Add(argument);
            instruction.Operand = replacement;
            changes.Add(new { method = method.FullName, original = original.FullName, replacement = replacement.FullName });
        }
    if (changes.Count != expectedSerializationCalls)
        throw new InvalidOperationException($"Expected exactly {expectedSerializationCalls} serialization calls.");
    int startTimeReads = 0;
    foreach (var method in methods)
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference target || target.FullName != "System.DateTime System.Diagnostics.Process::get_StartTime()") continue;
            if (method.FullName != "System.Void SteamKit2.SteamClient::.ctor(SteamKit2.SteamConfiguration,System.String)")
                throw new InvalidOperationException("Unexpected process start time read: " + method.FullName);
            instruction.OpCode = OpCodes.Call;
            instruction.Operand = assembly.MainModule.ImportReference(adapters.Single(m => m.Name == "ProcessStartTime"));
            changes.Add(new { method = method.FullName, original = target.FullName, replacement = "STS2Mobile.SteamWire.SteamWire::ProcessStartTime (unsupported on iOS)" });
            startTimeReads++;
        }
    if (startTimeReads != expectedStartTimeReads) throw new InvalidOperationException("Expected exactly one process start time read.");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    assembly.Write(output);
    File.WriteAllText(output + ".aot.json", JsonSerializer.Serialize(new
    {
        profile = "steamkit2-3.4.0-static-wire-v1",
        inputSha256 = hash,
        outputSha256 = CecilHelpers.Sha256(output),
        changes,
        limitation = "Static IL transforms only; tests/SteamWire.Tests checks the wire format."
    }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine($"Staged {changes.Count} transformations; the original package is unchanged.");
}

static void InventoryRoots(AssemblyDefinition assembly, string hash, string output)
{
    var types = CecilHelpers.Flatten(assembly.MainModule.Types).ToArray();
    var contracts = types.Where(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "ProtoBuf.ProtoContractAttribute"))
        .ToDictionary(t => t.FullName, StringComparer.Ordinal);
    var roots = new HashSet<string>(StringComparer.Ordinal);
    foreach (string name in new[] { "SteamKit2.Internal.CMsgProtoBufHeader", "SteamKit2.GC.Internal.CMsgProtoBufHeader", "SteamKit2.Internal.ContentManifestSignature" })
    {
        if (!contracts.ContainsKey(name)) throw new InvalidOperationException("Required Steam contract missing: " + name);
        roots.Add(name);
    }
    // Lobby chat, which SteamKit2 has contracts for but no handler: the app's own handler sends and reads it (src/STS2Mobile.Steam/Lobby).
    foreach (string name in new[] { "SteamKit2.Internal.CMsgClientMMSSendLobbyChatMsg", "SteamKit2.Internal.CMsgClientMMSLobbyChatMsg" })
    {
        if (!contracts.ContainsKey(name)) throw new InvalidOperationException("Lobby chat contract missing: " + name);
        roots.Add(name);
    }
    var calls = new List<object>();
    string[] supportedCallers = ["SteamKit2.SteamUser", "SteamKit2.SteamApps", "SteamKit2.SteamContent", "SteamKit2.SteamFriends", "SteamKit2.SteamClient", "SteamKit2.SteamMatchmaking",
        "SteamKit2.Internal.CMClient", "SteamKit2.Internal.MsgHdrProtoBuf", "SteamKit2.DepotManifest", "SteamKit2.Authentication.",
        "SteamKit2.Internal.Authentication", "SteamKit2.Internal.Cloud", "SteamKit2.Internal.ContentServerDirectory", "SteamKit2.Discovery.BasicServerListProto"];
    void Visit(TypeReference type)
    {
        if (contracts.ContainsKey(type.FullName)) roots.Add(type.FullName);
        if (type is GenericInstanceType generic) foreach (var argument in generic.GenericArguments) Visit(argument);
        if (type is TypeSpecification specification) Visit(specification.ElementType);
    }
    foreach (var method in types.SelectMany(t => t.Methods).Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            bool supported = supportedCallers.Any(prefix => prefix.EndsWith('.') ? method.DeclaringType.FullName.StartsWith(prefix, StringComparison.Ordinal)
                : method.DeclaringType.FullName == prefix || method.DeclaringType.FullName.StartsWith(prefix + "/", StringComparison.Ordinal));
            if (supported && instruction.Operand is TypeReference type) Visit(type);
            if (instruction.Operand is MethodReference target)
            {
                if (supported)
                {
                    Visit(target.DeclaringType);
                    if (target is GenericInstanceMethod generic) foreach (var argument in generic.GenericArguments) Visit(argument);
                }
                if (target.DeclaringType.FullName == "ProtoBuf.Serializer")
                    calls.Add(new { method = method.FullName, target = target.FullName });
            }
        }
    File.WriteAllText(output, JsonSerializer.Serialize(new
    {
        inputSha256 = hash,
        contractCount = contracts.Count,
        roots = roots.Order(StringComparer.Ordinal),
        calls
    }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine($"Inventoried {roots.Count} concrete protobuf roots and {calls.Count} serialization calls.");
}
