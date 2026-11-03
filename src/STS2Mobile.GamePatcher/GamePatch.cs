using Mono.Cecil;
using Mono.Cecil.Cil;

namespace STS2Mobile.GamePatcher;

internal sealed class GamePatch(AssemblyDefinition assembly, ModuleDefinition platform)
{
    public AssemblyDefinition Assembly { get; } = assembly;

    public ModuleDefinition Module { get; } = assembly.MainModule;

    public TypeDefinition[] Types { get; } = CecilWalk.Flatten(assembly.MainModule.Types).ToArray();

    public List<PatchChange> Changes { get; } = [];

    public IEnumerable<MethodDefinition> MethodsWithBodies => Types.SelectMany(t => t.Methods).Where(m => m.HasBody);

    /// <summary>Set by a step that applied but found the game unlike the tested build, for the report of an untested one.</summary>
    public string? Note { get; set; }

    /// <summary>Tolerant mode: a count that differs from the tested build is a note, not a failure.</summary>
    public bool Tolerant { get; init; }

    public TypeDefinition Type(string fullName) => Only(Types.Where(t => t.FullName == fullName), fullName);

    public MethodDefinition Method(string type, string name) => Only(Type(type).Methods.Where(m => m.Name == name), type + "." + name);

    /// <summary>The one match, or a failure that names what this game version lacks, for the report.</summary>
    private static T Only<T>(IEnumerable<T> matches, string what) => matches.Take(2).ToArray() switch
    {
        [var single] => single,
        [] => throw new InvalidOperationException(what + " is not in this game version."),
        _ => throw new InvalidOperationException(what + " is not unique in this game version."),
    };

    /// <summary>
    /// A count the tested build is known to have. Strict mode stops at any other; tolerant mode carries on and notes it, since the step
    /// still applied to whatever it found.
    /// </summary>
    public void Expect(int found, int expected, string what)
    {
        if (found == expected) return;
        if (!Tolerant) throw new InvalidOperationException($"Expected exactly {expected} {what}, found {found}.");
        Note = $"{found} {what}, where the tested build has {expected}";
    }

    public MethodReference Adapter(string type, string name) =>
        Module.ImportReference(platform.GetType("STS2Mobile.Platform." + type).Methods.Single(m => m.Name == name));

    public void Record(string method, string reason) => Changes.Add(new PatchChange(method, null, reason));

    public void Replace(MethodDefinition method, string reason, Action<ILProcessor> emit)
    {
        method.Body = new MethodBody(method);
        // An ordinary replacement is no longer the original async state machine entry point.
        foreach (var attribute in method.CustomAttributes.Where(a => a.AttributeType.Name == "AsyncStateMachineAttribute").ToArray())
            method.CustomAttributes.Remove(attribute);
        emit(method.Body.GetILProcessor());
        Record(method.FullName, reason);
    }

    public void Noop(MethodDefinition method, string reason)
    {
        Replace(method, reason, il =>
        {
            if (method.ReturnType.MetadataType != MetadataType.Void)
            {
                if (method.ReturnType.IsValueType || method.ReturnType.IsGenericParameter)
                {
                    var value = new VariableDefinition(method.ReturnType);
                    method.Body.Variables.Add(value);
                    method.Body.InitLocals = true;
                    il.Emit(OpCodes.Ldloca, value);
                    il.Emit(OpCodes.Initobj, method.ReturnType);
                    il.Emit(OpCodes.Ldloc, value);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }
            }
            il.Emit(OpCodes.Ret);
        });
    }

    // A removed step that returned a Task must still hand its caller a completed one, never null.
    public void Complete(MethodDefinition method, string reason)
    {
        if (method.ReturnType.FullName != "System.Threading.Tasks.Task") throw new InvalidOperationException("Not a Task method: " + method.FullName);
        var completedTask = new MethodReference("get_CompletedTask", method.ReturnType, method.ReturnType) { HasThis = false };
        Replace(method, reason, il =>
        {
            il.Emit(OpCodes.Call, completedTask);
            il.Emit(OpCodes.Ret);
        });
    }

    public void Reject(MethodDefinition method, string reason)
    {
        var exception = new TypeReference("System", "NotSupportedException", Module, Module.TypeSystem.CoreLibrary);
        var constructor = new MethodReference(".ctor", Module.TypeSystem.Void, exception) { HasThis = true };
        constructor.Parameters.Add(new ParameterDefinition(Module.TypeSystem.String));
        Replace(method, reason, il =>
        {
            il.Emit(OpCodes.Ldstr, reason);
            il.Emit(OpCodes.Newobj, constructor);
            il.Emit(OpCodes.Throw);
        });
    }
}
