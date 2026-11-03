using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace STS2Mobile.GamePatcher;

/// <summary>A reason the weaver refuses a mod. The stage stops; nothing is written.</summary>
public sealed class WeaveException(string message) : Exception(message);

/// <summary>One prefix or postfix woven into a game method.</summary>
public sealed record WovenPatch(string Target, string Kind, string Patch);

/// <summary>What weaving one mod did, for the staging profile.</summary>
public sealed record WeaveReport(string ModId, IReadOnlyList<WovenPatch> Patches, IReadOnlyList<string> SteamworksCalls);

/// <summary>
/// Turns a desktop code mod into one the phone can run. iOS forbids runtime code generation (there is
/// no JIT), and Harmony needs to emit code, so it cannot patch anything on iOS. Instead every supported Harmony patch is
/// written into the game method it targets, the same body change Harmony makes at run time, and the
/// mod's own Harmony calls become confirmations that this happened. Anything outside this
/// subset stops the stage with its reason instead of being approximated.
///
/// Supported: class-level [HarmonyPatch(typeof(T), "Method")] (optionally with argument types, or
/// split over two attributes), a static Prefix (void or bool) and Postfix (void) per patch class,
/// found by name or by [HarmonyPrefix]/[HarmonyPostfix], with arguments __instance, __result (by
/// reference or, in a postfix, by value), ___field reads or references, and the original method's
/// arguments by name. Empty [HarmonyPatch] containers may group nested patch classes.
/// </summary>
public sealed class Weaver
{
    private const string HarmonyScope = "0Harmony";
    private static readonly HashSet<string> RefusedPatchMethodNames = ["Transpiler", "Finalizer", "Prepare", "Cleanup", "TargetMethod", "TargetMethods"];
    // Godot classes that reach the network, other processes, other code or the outside of the app.
    private static readonly HashSet<string> RefusedGodotTypes = [
        "Godot.HttpRequest", "Godot.HttpClient", "Godot.StreamPeerTcp", "Godot.StreamPeerTls", "Godot.TcpServer", "Godot.PacketPeerUdp",
        "Godot.UdpServer", "Godot.WebSocketPeer", "Godot.WebSocketMultiplayerPeer", "Godot.ENetConnection", "Godot.ENetMultiplayerPeer",
        "Godot.ENetPacketPeer", "Godot.WebRtcPeerConnection", "Godot.WebRtcMultiplayerPeer", "Godot.WebRtcDataChannel", "Godot.DtlsServer",
        "Godot.PacketPeerDtls", "Godot.Upnp", "Godot.UpnpDevice", "Godot.JavaScriptBridge", "Godot.GDExtensionManager", "Godot.Expression"];
    private static readonly HashSet<string> RefusedGodotMethods = [
        "Godot.OS::ShellOpen", "Godot.OS::ShellShowInFileManager", "Godot.OS::Execute", "Godot.OS::ExecuteWithPipe", "Godot.OS::CreateProcess",
        "Godot.OS::CreateInstance", "Godot.OS::Kill", "Godot.ProjectSettings::LoadResourcePack"];

    private readonly AssemblyDefinition _game;
    private readonly ModuleDefinition _platform;
    private readonly Dictionary<string, TypeDefinition> _gameTypes;
    private readonly HashSet<string> _wovenTargets = [];

    /// <param name="game">The staged game assembly; woven in place, written by the caller.</param>
    /// <param name="platform">STS2Mobile.Platform, which holds the BuiltInMods adapters.</param>
    public Weaver(AssemblyDefinition game, ModuleDefinition platform)
    {
        _game = game;
        _platform = platform;
        _gameTypes = CecilWalk.Flatten(game.MainModule.Types).ToDictionary(t => t.FullName, StringComparer.Ordinal);
    }

    /// <summary>Weaves one mod. The returned assembly is the mod copy to compile into the app.</summary>
    public (AssemblyDefinition Mod, WeaveReport Report) Weave(string id, AssemblyDefinition mod, string sourceSha256)
    {
        var module = mod.MainModule;
        if (mod.Name.Name != id) throw new WeaveException($"{id}: the assembly is named {mod.Name.Name}; the game loads <id>.dll.");
        var steamworks = Audit(id, module);
        var patches = new List<WovenPatch>();
        foreach (var type in CecilWalk.Flatten(module.Types).ToArray())
        {
            var classAttributes = type.CustomAttributes.Where(IsHarmony).ToArray();
            foreach (var method in type.Methods)
                foreach (var attribute in method.CustomAttributes.Where(IsHarmony))
                    if (attribute.AttributeType.FullName is not ("HarmonyLib.HarmonyPrefix" or "HarmonyLib.HarmonyPostfix") || classAttributes.Length == 0)
                        throw new WeaveException($"{id}: {method.FullName} uses [{attribute.AttributeType.Name}], which is not supported on iOS.");
            foreach (var attribute in classAttributes)
                if (attribute.AttributeType.FullName != "HarmonyLib.HarmonyPatch")
                    throw new WeaveException($"{id}: {type.FullName} uses [{attribute.AttributeType.Name}], which is not supported on iOS.");
            if (classAttributes.Length == 0) continue;
            MethodDefinition? prefix = null, postfix = null;
            foreach (var method in type.Methods)
            {
                if (RefusedPatchMethodNames.Contains(method.Name))
                    throw new WeaveException($"{id}: {type.FullName}.{method.Name} would be a Harmony {method.Name}, which is not supported on iOS.");
                bool isPrefix = method.Name == "Prefix" || method.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPrefix");
                bool isPostfix = method.Name == "Postfix" || method.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPostfix");
                if (isPrefix && isPostfix) throw new WeaveException($"{id}: {method.FullName} is marked as both prefix and postfix.");
                if (isPrefix) prefix = prefix == null ? method : throw new WeaveException($"{id}: {type.FullName} has more than one prefix.");
                if (isPostfix) postfix = postfix == null ? method : throw new WeaveException($"{id}: {type.FullName} has more than one postfix.");
            }
            // A bare container contributes no target to its nested classes. Still inspect its
            // methods above so unsupported TargetMethods/Prepare hooks cannot disappear silently.
            if (prefix == null && postfix == null && classAttributes.All(a =>
                a.ConstructorArguments.Count == 0 && !a.HasProperties && !a.HasFields)) continue;
            var target = ResolveTarget(id, type, classAttributes);
            if (prefix == null && postfix == null) throw new WeaveException($"{id}: {type.FullName} patches {target.FullName} but has no prefix or postfix.");
            if (!_wovenTargets.Add(target.FullName))
                throw new WeaveException($"{id}: {target.FullName} is patched more than once; patch ordering is not supported on iOS.");
            WeaveInto(id, target, prefix, postfix);
            MakeReachable(type);
            if (prefix != null) { prefix.IsPublic = true; patches.Add(new WovenPatch(target.FullName, "prefix", prefix.FullName)); }
            if (postfix != null) { postfix.IsPublic = true; patches.Add(new WovenPatch(target.FullName, "postfix", postfix.FullName)); }
        }
        RemoveHarmony(id, mod);
        CheckGameReferences(id, module);
        var mark = _platform.GetType("STS2Mobile.Platform.WovenModAttribute") ?? throw new WeaveException("STS2Mobile.Platform lacks WovenModAttribute.");
        var markAttribute = new CustomAttribute(module.ImportReference(mark.Methods.Single(m => m.IsConstructor && m.Parameters.Count == 3)));
        markAttribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, id));
        markAttribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, sourceSha256));
        markAttribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.Int32, patches.Count));
        mod.CustomAttributes.Add(markAttribute);
        return (mod, new WeaveReport(id, patches, steamworks));
    }

    /// <summary>Refuses code that could reach the network or load other code; reports Steamworks use.</summary>
    private static List<string> Audit(string id, ModuleDefinition module)
    {
        var steamworks = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in CecilWalk.Flatten(module.Types))
            foreach (var method in type.Methods)
            {
                if (method.IsPInvokeImpl || method.HasPInvokeInfo) throw new WeaveException($"{id}: {method.FullName} calls native code directly.");
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    var (declaring, member) = instruction.Operand switch
                    {
                        MethodReference m => (m.DeclaringType, m.Name),
                        FieldReference f => (f.DeclaringType, f.Name),
                        TypeReference t => (t, ""),
                        _ => ((TypeReference?)null, "")
                    };
                    if (declaring == null) continue;
                    var element = declaring.GetElementType();
                    string ns = element.Namespace, full = element.FullName;
                    if (ns == "System.Net" || ns.StartsWith("System.Net.") || ns == "System.Reflection.Emit" || ns == "System.Runtime.Loader"
                        || full == "System.Diagnostics.Process" || full == "System.Diagnostics.ProcessStartInfo"
                        || (full == "System.Reflection.Assembly" && member is "Load" or "LoadFrom" or "LoadFile" or "UnsafeLoadFrom" or "LoadWithPartialName")
                        || RefusedGodotTypes.Contains(full) || RefusedGodotMethods.Contains(full + "::" + member))
                        throw new WeaveException($"{id}: {method.FullName} uses {full}{(member.Length > 0 ? "." + member : "")}, which the network and code-loading rules refuse.");
                    if (element.Scope.Name == "Steamworks.NET") steamworks.Add(full + (member.Length > 0 ? "." + member : ""));
                }
            }
        return [.. steamworks];
    }

    private MethodDefinition ResolveTarget(string id, TypeDefinition patchType, CustomAttribute[] attributes)
    {
        TypeReference? declaring = null;
        string? name = null;
        TypeReference[]? argumentTypes = null;
        foreach (var attribute in attributes)
        {
            if (attribute.HasProperties || attribute.HasFields) throw new WeaveException($"{id}: {patchType.FullName} sets named [HarmonyPatch] options.");
            foreach (var argument in attribute.ConstructorArguments)
            {
                switch (argument.Value)
                {
                    case TypeReference named when declaring == null: declaring = named; break;
                    case string text when name == null: name = text; break;
                    case CustomAttributeArgument[] list when argumentTypes == null && list.All(a => a.Value is TypeReference):
                        argumentTypes = list.Select(a => (TypeReference)a.Value).ToArray(); break;
                    default:
                        throw new WeaveException($"{id}: {patchType.FullName} has a [HarmonyPatch] form that is not supported on iOS ({argument.Type.FullName}).");
                }
            }
        }
        if (declaring == null || name == null) throw new WeaveException($"{id}: {patchType.FullName} does not name a target type and method.");
        if (!_gameTypes.TryGetValue(declaring.FullName, out var type))
            throw new WeaveException($"{id}: {patchType.FullName} targets {declaring.FullName}, which is not in the game.");
        // Harmony's AccessTools.Method looks at the type itself first, then at its base types.
        for (var current = type; current != null; current = current.BaseType is { } b && _gameTypes.TryGetValue(b.FullName, out var next) ? next : null)
        {
            var candidates = current.Methods.Where(m => m.Name == name && !m.IsConstructor &&
                (argumentTypes == null || m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(argumentTypes.Select(t => t.FullName)))).ToArray();
            if (candidates.Length > 1) throw new WeaveException($"{id}: {declaring.FullName}.{name} is overloaded; name its argument types.");
            if (candidates.Length == 1)
            {
                var target = candidates[0];
                if (!target.HasBody || target.HasGenericParameters || target.DeclaringType.HasGenericParameters)
                    throw new WeaveException($"{id}: {target.FullName} has no body or is generic; it cannot be patched on iOS.");
                return target;
            }
        }
        throw new WeaveException($"{id}: {patchType.FullName} targets {declaring.FullName}.{name}, which is not in the game.");
    }

    private void WeaveInto(string id, MethodDefinition target, MethodDefinition? prefix, MethodDefinition? postfix)
    {
        var body = target.Body;
        if (body.Instructions.Any(i => i.OpCode == OpCodes.Tail)) throw new WeaveException($"{id}: {target.FullName} uses tail calls.");
        if (prefix != null && prefix.ReturnType.MetadataType is not (MetadataType.Void or MetadataType.Boolean))
            throw new WeaveException($"{id}: prefix {prefix.FullName} must return void or bool.");
        if (postfix != null && postfix.ReturnType.MetadataType != MetadataType.Void)
            throw new WeaveException($"{id}: postfix {postfix.FullName} must return void.");
        foreach (var patch in new[] { prefix, postfix }.OfType<MethodDefinition>())
            if (!patch.IsStatic || patch.HasGenericParameters) throw new WeaveException($"{id}: {patch.FullName} must be a static, non-generic method.");
        body.SimplifyMacros();
        var il = body.GetILProcessor();
        bool hasResult = target.ReturnType.MetadataType != MetadataType.Void;
        VariableDefinition? result = null;
        if (hasResult)
        {
            result = new VariableDefinition(target.ReturnType);
            body.Variables.Add(result);
            body.InitLocals = true;
        }
        var first = body.Instructions[0];
        var epilogue = new List<Instruction>();
        if (postfix != null) epilogue.AddRange(CallPatch(id, il, target, postfix, result, isPrefix: false));
        if (result != null) epilogue.Add(il.Create(OpCodes.Ldloc, result));
        epilogue.Add(il.Create(OpCodes.Ret));
        var exit = epilogue[0];
        // Every return goes through the one epilogue, keeping each instruction object so branch and
        // exception-region references to it stay valid.
        foreach (var instruction in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            if (result != null)
            {
                instruction.OpCode = OpCodes.Stloc; instruction.Operand = result;
                il.InsertAfter(instruction, il.Create(OpCodes.Br, exit));
            }
            else
            {
                instruction.OpCode = OpCodes.Br; instruction.Operand = exit;
            }
        }
        foreach (var handler in body.ExceptionHandlers)
        {
            handler.TryEnd ??= exit;
            handler.HandlerEnd ??= exit;
        }
        foreach (var instruction in epilogue) il.Append(instruction);
        if (prefix != null)
        {
            var entry = CallPatch(id, il, target, prefix, result, isPrefix: true);
            // A prefix that returns false skips the original body; the postfix still runs, as with Harmony.
            if (prefix.ReturnType.MetadataType == MetadataType.Boolean) entry.Add(il.Create(OpCodes.Brfalse, exit));
            foreach (var instruction in entry) il.InsertBefore(first, instruction);
        }
        body.OptimizeMacros();
    }

    private List<Instruction> CallPatch(string id, ILProcessor il, MethodDefinition target, MethodDefinition patch, VariableDefinition? result, bool isPrefix)
    {
        var sequence = new List<Instruction>();
        foreach (var parameter in patch.Parameters)
        {
            var type = parameter.ParameterType;
            bool byReference = type.IsByReference;
            string elementName = byReference ? ((ByReferenceType)type).ElementType.FullName : type.FullName;
            if (parameter.Name == "__instance")
            {
                if (target.IsStatic || target.DeclaringType.IsValueType || byReference || !IsSameOrBase(target.DeclaringType, elementName))
                    throw new WeaveException($"{id}: {patch.FullName} takes __instance as {type.FullName}, which does not fit {target.FullName}.");
                sequence.Add(il.Create(OpCodes.Ldarg, target.Body.ThisParameter));
            }
            else if (parameter.Name == "__result")
            {
                if (result == null || elementName != target.ReturnType.FullName || (isPrefix && !byReference))
                    throw new WeaveException($"{id}: {patch.FullName} takes __result as {type.FullName}, which does not fit {target.FullName}.");
                sequence.Add(il.Create(byReference ? OpCodes.Ldloca : OpCodes.Ldloc, result));
            }
            else if (parameter.Name.StartsWith("___", StringComparison.Ordinal))
            {
                var field = ResolveField(id, target, patch, parameter.Name[3..]);
                if (field.FieldType.FullName != elementName || field.IsLiteral ||
                    (byReference && field.IsInitOnly) || (!field.IsStatic && target.IsStatic))
                    throw new WeaveException($"{id}: {patch.FullName} takes {parameter.Name} as {type.FullName}, which does not fit field {field.FullName}.");
                // The emitted access lives in the game's target, not in the mod. A private base
                // field needs assembly visibility when the target belongs to a derived type.
                if (field.IsPrivate && field.DeclaringType != target.DeclaringType) field.IsAssembly = true;
                if (!field.IsStatic) sequence.Add(il.Create(OpCodes.Ldarg, target.Body.ThisParameter));
                var opcode = field.IsStatic
                    ? (byReference ? OpCodes.Ldsflda : OpCodes.Ldsfld)
                    : (byReference ? OpCodes.Ldflda : OpCodes.Ldfld);
                sequence.Add(il.Create(opcode, field));
            }
            else if (parameter.Name.StartsWith("__", StringComparison.Ordinal))
            {
                throw new WeaveException($"{id}: {patch.FullName} takes {parameter.Name}, which is not supported on iOS.");
            }
            else
            {
                var original = target.Parameters.SingleOrDefault(p => p.Name == parameter.Name)
                    ?? throw new WeaveException($"{id}: {patch.FullName} takes {parameter.Name}, which {target.FullName} does not have.");
                var originalType = original.ParameterType;
                bool originalByReference = originalType.IsByReference;
                string originalElement = originalByReference ? ((ByReferenceType)originalType).ElementType.FullName : originalType.FullName;
                if (originalElement != elementName || (originalByReference && !byReference))
                    throw new WeaveException($"{id}: {patch.FullName} takes {parameter.Name} as {type.FullName}; the game passes {originalType.FullName}.");
                sequence.Add(il.Create(byReference && !originalByReference ? OpCodes.Ldarga : OpCodes.Ldarg, original));
            }
        }
        sequence.Add(il.Create(OpCodes.Call, _game.MainModule.ImportReference(patch)));
        return sequence;
    }

    private FieldDefinition ResolveField(string id, MethodDefinition target, MethodDefinition patch, string name)
    {
        for (var current = target.DeclaringType; current != null;
            current = current.BaseType is { } b && _gameTypes.TryGetValue(b.FullName, out var next) ? next : null)
        {
            var field = current.Fields.SingleOrDefault(f => f.Name == name);
            if (field == null) continue;
            if (current.HasGenericParameters)
                throw new WeaveException($"{id}: {patch.FullName} refers to generic field {field.FullName}, which is not supported on iOS.");
            return field;
        }
        throw new WeaveException($"{id}: {patch.FullName} takes ___{name}, but {target.DeclaringType.FullName} has no such field.");
    }

    private bool IsSameOrBase(TypeDefinition type, string name)
    {
        if (name == "System.Object") return true;
        for (TypeDefinition? current = type; current != null; current = current.BaseType is { } b && _gameTypes.TryGetValue(b.FullName, out var next) ? next : null)
            if (current.FullName == name) return true;
        return false;
    }

    /// <summary>The game assembly calls the patch methods, so they and their classes must be public.</summary>
    private static void MakeReachable(TypeDefinition type)
    {
        for (var current = type; current != null; current = current.DeclaringType)
        {
            if (current.IsNested) { current.IsNestedPublic = true; }
            else { current.IsPublic = true; }
        }
    }

    /// <summary>The mod copy keeps no trace of 0Harmony: its attributes go, its patching calls become confirmations.</summary>
    private void RemoveHarmony(string id, AssemblyDefinition mod)
    {
        var module = mod.MainModule;
        var builtIn = _platform.GetType("STS2Mobile.Platform.BuiltInMods") ?? throw new WeaveException("STS2Mobile.Platform lacks BuiltInMods.");
        var createPatcher = module.ImportReference(builtIn.Methods.Single(m => m.Name == "CreatePatcher"));
        var patchAll = module.ImportReference(builtIn.Methods.Single(m => m.Name == "PatchAll"));
        void Strip(Mono.Collections.Generic.Collection<CustomAttribute> attributes)
        {
            foreach (var attribute in attributes.Where(IsHarmony).ToArray()) attributes.Remove(attribute);
        }
        Strip(mod.CustomAttributes);
        Strip(module.CustomAttributes);
        foreach (var type in CecilWalk.Flatten(module.Types))
        {
            Strip(type.CustomAttributes);
            if (IsHarmonyType(type.BaseType) || type.Interfaces.Any(i => IsHarmonyType(i.InterfaceType)))
                throw new WeaveException($"{id}: {type.FullName} derives from a Harmony type.");
            foreach (var field in type.Fields)
            {
                Strip(field.CustomAttributes);
                if (IsHarmonyType(field.FieldType)) throw new WeaveException($"{id}: {field.FullName} holds a Harmony object.");
            }
            foreach (var property in type.Properties) Strip(property.CustomAttributes);
            foreach (var method in type.Methods)
            {
                Strip(method.CustomAttributes);
                foreach (var parameter in method.Parameters) Strip(parameter.CustomAttributes);
                if (IsHarmonyType(method.ReturnType) || method.Parameters.Any(p => IsHarmonyType(p.ParameterType)))
                    throw new WeaveException($"{id}: {method.FullName} passes Harmony objects.");
                if (!method.HasBody) continue;
                method.Body.SimplifyMacros();
                foreach (var variable in method.Body.Variables.Where(v => IsHarmonyType(v.VariableType)))
                    variable.VariableType = module.TypeSystem.Object;
                foreach (var instruction in method.Body.Instructions.ToArray())
                {
                    if (instruction.Operand is MethodReference called && IsHarmonyType(called.DeclaringType))
                    {
                        string signature = called.DeclaringType.FullName + "::" + called.Name + "(" + string.Join(",", called.Parameters.Select(p => p.ParameterType.FullName)) + ")";
                        if (signature == "HarmonyLib.Harmony::PatchAll()" && called.HasThis &&
                            (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt))
                        {
                            // Keep the instruction object so branches and exception boundaries still
                            // enter here. GetExecutingAssembly runs in this mod, supplying exactly the
                            // assembly the explicit PatchAll(Assembly) adapter validates.
                            var assembly = patchAll.Parameters[1].ParameterType;
                            instruction.OpCode = OpCodes.Call;
                            instruction.Operand = new MethodReference("GetExecutingAssembly", assembly, assembly);
                            method.Body.GetILProcessor().InsertAfter(instruction, Instruction.Create(OpCodes.Call, patchAll));
                            continue;
                        }
                        (instruction.OpCode, instruction.Operand) = signature switch
                        {
                            "HarmonyLib.Harmony::.ctor(System.String)" when instruction.OpCode == OpCodes.Newobj => (OpCodes.Call, (object)createPatcher),
                            "HarmonyLib.Harmony::PatchAll(System.Reflection.Assembly)" => (OpCodes.Call, patchAll),
                            _ => throw new WeaveException($"{id}: {method.FullName} calls {signature}, which is not supported on iOS.")
                        };
                    }
                    else if (instruction.Operand is MemberReference member && (member is TypeReference t ? IsHarmonyType(t) : IsHarmonyType(member.DeclaringType)))
                    {
                        throw new WeaveException($"{id}: {method.FullName} uses {member.FullName}, which is not supported on iOS.");
                    }
                }
                method.Body.OptimizeMacros();
            }
        }
        foreach (var reference in module.AssemblyReferences.Where(r => r.Name == HarmonyScope).ToArray()) module.AssemblyReferences.Remove(reference);
    }

    /// <summary>Every game member the mod uses must exist in the staged game assembly.</summary>
    private static void CheckGameReferences(string id, ModuleDefinition module)
    {
        foreach (var type in CecilWalk.Flatten(module.Types))
            foreach (var method in type.Methods.Where(m => m.HasBody))
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MemberReference member) continue;
                    var declaring = member is TypeReference t ? t : member.DeclaringType;
                    if (declaring?.GetElementType().Scope.Name != "sts2") continue;
                    object? resolved = member switch
                    {
                        MethodReference m => m.Resolve(),
                        FieldReference f => f.Resolve(),
                        TypeReference r => r.Resolve(),
                        _ => null
                    };
                    if (resolved == null) throw new WeaveException($"{id}: {method.FullName} uses {member.FullName}, which the staged game does not have.");
                }
    }

    private static bool IsHarmony(CustomAttribute attribute) => attribute.AttributeType.Scope.Name == HarmonyScope;

    private static bool IsHarmonyType(TypeReference? type)
    {
        if (type == null) return false;
        if (type is GenericInstanceType generic && generic.GenericArguments.Any(IsHarmonyType)) return true;
        if (type is TypeSpecification specification) return IsHarmonyType(specification.ElementType);
        return type.Scope.Name == HarmonyScope;
    }
}
