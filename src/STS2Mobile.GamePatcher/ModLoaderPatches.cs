using Mono.Cecil;
using Mono.Cecil.Cil;

namespace STS2Mobile.GamePatcher;

// Mods: the game's own loader runs (consent, toggles, dependencies, versions, separate modded saves),
// but it can only reach mod code the app prepared and registered; it never reads a mod DLL itself.
// iOS forbids runtime code generation (there is no JIT), so Harmony cannot patch at run time: code
// mods are pinned and their patches woven in when the game is prepared (mods/builtin.json, Weaver).
// .pck mods without scripts are read from the app's own mods folder. Every redirect fails closed.
internal static class ModLoaderPatches
{
    private const string ModManager = "MegaCrit.Sts2.Core.Modding.ModManager";

    public static IEnumerable<PatchStep> Steps =>
    [
        new("Mods from the app's own folder", false, patch =>
        {
            var scan = StateMachineBody(patch.Method(ModManager, "Initialize"));
            Only(scan, i => Calls(i, "Godot.OS", "GetExecutablePath"), "executable path").Operand = patch.Adapter("BuiltInMods", "ModLoaderAnchor");
            patch.Record(scan.FullName, "Look for mods in the app's own mods folder, not inside the read-only app bundle.");
        }),
        new("No Steam Workshop check", true, patch =>
            patch.Complete(patch.Method(ModManager, "CheckSteamBranchSupport"), "No Steam Workshop query on iOS; Workshop mods are never read.")),
        new("Mod code only from the app", true, LoadModsFromTheApp),
        new("Mods screen opens Files", false, patch =>
            patch.Replace(patch.Method("MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen.NModdingScreen", "OnGetModsPressed"),
                "The Steam Workshop button opens the Files picker for a script-free .pck mod instead (the host checks it before installing).", il =>
                {
                    il.Emit(OpCodes.Call, patch.Adapter("ModImportRequests", "Request"));
                    il.Emit(OpCodes.Ret);
                })),
        new("No Harmony at run time", true, patch =>
        {
            var harmony = patch.Method(ModManager, "HasHarmonyPatches");
            var resolve = patch.Method(ModManager, "HandleAssemblyResolveFailure");
            patch.Noop(harmony, "No runtime Harmony patches: iOS forbids runtime code generation, so mod patches are woven in ahead of time.");
            patch.Noop(resolve, "No mod assembly resolution or Harmony type discovery on iOS.");
        }),
    ];

    /// <summary>
    /// For the assembly the launcher loads with mods on only (<c>sts2.dll</c>), so the plain one keeps the played bytes: the game's
    /// mods agreement is the launcher's switch (<see cref="STS2Mobile.Platform.ModLaunchSettings"/>), not its Settings &gt; Mods popup,
    /// whose Load button quits the game. The save file's setting is left as it is. A build without the getter is
    /// left to ask the player itself, with a note.
    /// </summary>
    public static List<PatchChange> AgreeToMods(AssemblyDefinition game, ModuleDefinition platform, bool tolerant)
    {
        var patch = new GamePatch(game, platform) { Tolerant = tolerant };
        MethodDefinition getter;
        try { getter = patch.Method(ModManager, "get_PlayerAgreedToModLoading"); }
        catch (InvalidOperationException error) when (tolerant)
        {
            return [new PatchChange(ModManager + ".PlayerAgreedToModLoading", null, "Not changed: " + error.Message + " The game asks for the mods agreement itself.")];
        }
        patch.Replace(getter, "The launcher's mods switch is the player's agreement; the game's own popup would quit the game to apply it.", il =>
        {
            il.Emit(OpCodes.Call, patch.Adapter("ModLaunchSettings", "PlayerAgreedToModLoading"));
            il.Emit(OpCodes.Ret);
        });
        return patch.Changes;
    }

    private static void LoadModsFromTheApp(GamePatch patch)
    {
        var load = patch.Method(ModManager, "TryLoadMod");
        var fromPath = Only(load, i => Calls(i, "System.Runtime.Loader.AssemblyLoadContext", "LoadFromAssemblyPath"), "assembly load");
        if (fromPath.Previous.OpCode.Code is not (Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3))
            throw new InvalidOperationException("Unexpected assembly path in TryLoadMod.");
        var assemblyPath = fromPath.Previous.GetLocal(load.Body);
        var fileCheck = Only(load, i => Calls(i, "MegaCrit.Sts2.Core.Modding.IModManagerFileIo", "FileExists") && i.Previous.IsLoad(assemblyPath, load.Body), "assembly file check");
        var loadContext = Only(load, i => Calls(i, "System.Runtime.Loader.AssemblyLoadContext", "GetLoadContext"), "load context");
        var harmony = Only(load, i => i.OpCode == OpCodes.Newobj && Calls(i, "HarmonyLib.Harmony", ".ctor"), "Harmony instance");
        var patchAll = Only(load, i => Calls(i, "HarmonyLib.Harmony", "PatchAll"), "Harmony.PatchAll");

        fileCheck.Set(OpCodes.Call, patch.Adapter("BuiltInMods", "AssemblyFileExists"));
        loadContext.Set(OpCodes.Call, patch.Adapter("BuiltInMods", "LoadContext"));
        fromPath.Set(OpCodes.Call, patch.Adapter("BuiltInMods", "LoadFromAssemblyPath"));
        harmony.Set(OpCodes.Call, patch.Adapter("BuiltInMods", "CreatePatcher"));
        patchAll.Set(OpCodes.Call, patch.Adapter("BuiltInMods", "PatchAll"));
        foreach (var local in load.Body.Variables.Where(v => v.VariableType.FullName == "HarmonyLib.Harmony")) local.VariableType = patch.Module.TypeSystem.Object;
        patch.Record(load.FullName, "Mod code comes only from assemblies compiled into the app; a DLL on disk is never read. Harmony patches are woven at build time.");
    }

    private static MethodDefinition StateMachineBody(MethodDefinition method) =>
        method.CustomAttributes.SingleOrDefault(a => a.AttributeType.Name == "AsyncStateMachineAttribute") is { } machine
            ? ((TypeReference)machine.ConstructorArguments[0].Value).Resolve().Methods.Single(m => m.Name == "MoveNext") : method;

    private static Instruction Only(MethodDefinition method, Func<Instruction, bool> match, string what) =>
        method.Body.Instructions.Where(match).ToArray() is [var single] ? single
            : throw new InvalidOperationException($"Expected exactly one {what} in {method.FullName}.");

    private static bool Calls(Instruction i, string type, string name) => i.Operand is MethodReference r && r.DeclaringType.FullName == type && r.Name == name;
}
