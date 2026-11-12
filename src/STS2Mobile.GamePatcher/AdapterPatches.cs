using Mono.Cecil;
using Mono.Cecil.Cil;

namespace STS2Mobile.GamePatcher;

internal static class AdapterPatches
{
    // The metrics uploader, the daily run's time server request and the feedback screen.
    private const int ExpectedHttpClients = 3;

    public static IEnumerable<PatchStep> Steps(ModuleDefinition gameAdapters) =>
    [
        new("The game's web requests go through the app's guard", true, GuardHttp),
        new("Save upgrades without run-time JSON code", true, StaticJsonNodes),
        new("Texture atlases read without run-time JSON code", true, patch => UseAtlasAdapter(patch, gameAdapters)),
    ];

    private static void GuardHttp(GamePatch patch)
    {
        int clients = 0;
        foreach (var method in patch.MethodsWithBodies)
            foreach (var instruction in method.Body.Instructions)
                if (instruction.OpCode == OpCodes.Newobj && instruction.Operand is MethodReference target && target.DeclaringType.FullName == "System.Net.Http.HttpClient")
                {
                    if (target.Parameters.Count != 0) throw new InvalidOperationException("Unexpected HTTP constructor: " + method.FullName);
                    instruction.Set(OpCodes.Call, patch.Adapter("GameHttp", "CreateClient"));
                    patch.Record(method.FullName, "Guard game HTTP including environment URL overrides.");
                    clients++;
                }
        patch.Expect(clients, ExpectedHttpClients, "game HTTP clients");
    }

    private static void StaticJsonNodes(GamePatch patch)
    {
        foreach (var method in patch.MethodsWithBodies)
            foreach (var instruction in method.Body.Instructions)
                if (instruction.Operand is GenericInstanceMethod generic && generic.DeclaringType.FullName == "System.Text.Json.Nodes.JsonArray" && generic.Name == "Add")
                {
                    string argument = generic.GenericArguments.Single().FullName;
                    string adapter = argument switch
                    {
                        "System.String" => "AddString",
                        "System.Text.Json.Nodes.JsonObject" => "AddObject",
                        _ => throw new InvalidOperationException("Unexpected generic JsonArray.Add: " + argument)
                    };
                    instruction.Set(OpCodes.Call, patch.Adapter("JsonNodes", adapter));
                    patch.Record(method.FullName, "Use static JsonNode creation in save migrations.");
                }
    }

    private static void UseAtlasAdapter(GamePatch patch, ModuleDefinition gameAdapters)
    {
        var deserialize = patch.Module.ImportReference(gameAdapters.GetType("STS2Mobile.GameAdapters.AtlasJson").Methods.Single(m => m.Name == "Deserialize"));
        // v0.113.0 renamed LoadAtlasInternal to BuildAtlasData, which returns the atlas instead of storing it; the deserialize call is the same.
        var atlas = patch.Method("MegaCrit.Sts2.Core.Assets.AtlasManager", "BuildAtlasData", "LoadAtlasInternal");
        var call = atlas.Body.Instructions.Single(i => i.Operand is GenericInstanceMethod m && m.DeclaringType.FullName == "System.Text.Json.JsonSerializer" && m.Name == "Deserialize");
        call.Set(OpCodes.Call, deserialize);
        patch.Record(atlas.FullName, "Use source-generated atlas JSON metadata instead of reflection-based serialization.");
    }
}
