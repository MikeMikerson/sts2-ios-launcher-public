using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace STS2Mobile.Tools.Mono;

// The names added to GodotSharp (ProofCallableTrampolines, ProofInvoke) keep those of the original Mono prototype
// on purpose: the output's hash, pinned in tools/dependencies.json, is that of the GodotSharp.dll played on the phone.
public static class GodotSharpRewrites
{
    private const string Holder = "ProofCallableTrampolines";
    private const int GenericFromCount = 19;

    // Workaround 3. Mono's AOT compiler (10.0.12, full+interp) asserts in mono_metadata_type_dup_with_cmods
    // when a value type has a function pointer field whose signature carries custom modifiers
    // (Godot.Callable._trampoline: `out godot_variant`). They have no runtime meaning, so remove them.
    public static string StripFunctionPointerModifiers(string input, string output)
    {
        using var module = ModuleDefinition.ReadModule(input);
        var changed = 0;
        foreach (var type in module.GetTypes().Where(type => type.IsValueType))
        {
            foreach (var field in type.Fields)
            {
                var stripped = Strip(field.FieldType, out var did);
                if (did)
                {
                    field.FieldType = stripped;
                    changed++;
                }
            }
        }
        module.Write(output);
        return $"changed {changed} field(s)";
    }

    // Workaround 7. In Mono's mixed mode the interpreter's ldftn yields an InterpMethod*, not code, and the
    // interpreter never runs AOT code for generic instances. So Callable.From<T>(action), called from the
    // interpreted game, handed Godot an interpreter pointer as the trampoline, and the AOT compiled
    // DelegateUtils.InvokeWithVariantArgs crashed calling it. After this rewrite every generic From registers
    // a delegate to its generic trampoline (Mono converts delegates between the two worlds) and passes Godot
    // one non-generic trampoline, Dispatch, whose address comes from AOT code. The Callable still holds the
    // caller's own delegate, so Callable equality is unchanged.
    //
    // Workaround 10. Once the interpreter tiers a hot game method up, it inlines small callees, AOT ones
    // included. An inlined non-generic Callable.From or CreateWithUnsafeTrampoline then handled the function
    // pointer inside the interpreter, and Godot later jumped to a non-code address. So every GodotSharp
    // method that creates, takes or calls a function pointer is marked NoInlining.
    public static string RewriteCallables(string input, string output)
    {
        using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { InMemory = true });
        var ts = module.TypeSystem;
        var runtime = module.AssemblyReferences.Single(a => a.Name == "System.Runtime");
        var callable = module.GetType("Godot.Callable");
        var ptrArgs = module.GetType("Godot.NativeInterop.NativeVariantPtrArgs");
        var variant = module.GetType("Godot.NativeInterop.godot_variant");

        TypeReference Generic(string ns, string name, int arity)
        {
            var t = new TypeReference(ns, name, module, runtime);
            for (var i = 0; i < arity; i++)
                t.GenericParameters.Add(new GenericParameter(t));
            return t;
        }

        // Action<object, IntPtr, IntPtr>: (delegateObj, &NativeVariantPtrArgs, &ret)
        var actionOpen = Generic("System", "Action`3", 3);
        var invoker = new GenericInstanceType(actionOpen) { GenericArguments = { ts.Object, ts.IntPtr, ts.IntPtr } };
        var invokerCtor = new MethodReference(".ctor", ts.Void, invoker) { HasThis = true, Parameters = { new(ts.Object), new(ts.IntPtr) } };
        var invokerInvoke = new MethodReference("Invoke", ts.Void, invoker) { HasThis = true };
        foreach (var gp in actionOpen.GenericParameters)
            invokerInvoke.Parameters.Add(new ParameterDefinition(gp));

        var tableOpen = Generic("System.Runtime.CompilerServices", "ConditionalWeakTable`2", 2);
        var table = new GenericInstanceType(tableOpen) { GenericArguments = { ts.Object, invoker } };
        var tableCtor = new MethodReference(".ctor", ts.Void, table) { HasThis = true };
        var tableAdd = new MethodReference("AddOrUpdate", ts.Void, table) { HasThis = true, Parameters = { new(tableOpen.GenericParameters[0]), new(tableOpen.GenericParameters[1]) } };
        var tableGet = new MethodReference("TryGetValue", ts.Boolean, table) { HasThis = true, Parameters = { new(tableOpen.GenericParameters[0]), new(new ByReferenceType(tableOpen.GenericParameters[1])) } };

        var holder = new TypeDefinition("Godot", Holder,
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, ts.Object);
        module.Types.Add(holder);
        var invokers = new FieldDefinition("s_invokers", FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly, table);
        holder.Fields.Add(invokers);

        var cctor = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.Static, ts.Void);
        var il = cctor.Body.GetILProcessor();
        il.Emit(OpCodes.Newobj, tableCtor);
        il.Emit(OpCodes.Stsfld, invokers);
        il.Emit(OpCodes.Ret);
        holder.Methods.Add(cctor);

        // static void Dispatch(object delegateObj, NativeVariantPtrArgs args, out godot_variant ret)
        var dispatch = new MethodDefinition("Dispatch", MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.Static, ts.Void)
        {
            ImplAttributes = MethodImplAttributes.NoInlining,
            Parameters = { new("delegateObj", ParameterAttributes.None, ts.Object), new("args", ParameterAttributes.None, ptrArgs), new("ret", ParameterAttributes.Out, new ByReferenceType(variant)) },
        };
        dispatch.Body.Variables.Add(new VariableDefinition(invoker));
        il = dispatch.Body.GetILProcessor();
        il.Emit(OpCodes.Ldsfld, invokers);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca_S, dispatch.Body.Variables[0]);
        il.Emit(OpCodes.Callvirt, tableGet);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarga_S, dispatch.Parameters[1]);
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Callvirt, invokerInvoke);
        il.Emit(OpCodes.Ret);
        holder.Methods.Add(dispatch);

        // static IntPtr Register(object delegateObj, Action<object, IntPtr, IntPtr> invoke): returns &Dispatch from AOT code.
        var register = new MethodDefinition("Register", MethodAttributes.Assembly | MethodAttributes.HideBySig | MethodAttributes.Static, ts.IntPtr)
        {
            ImplAttributes = MethodImplAttributes.NoInlining,
            Parameters = { new("delegateObj", ParameterAttributes.None, ts.Object), new("invoke", ParameterAttributes.None, invoker) },
        };
        il = register.Body.GetILProcessor();
        il.Emit(OpCodes.Ldsfld, invokers);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, tableAdd);
        il.Emit(OpCodes.Ldftn, dispatch);
        il.Emit(OpCodes.Ret);
        holder.Methods.Add(register);

        var rewritten = 0;
        foreach (var from in callable.Methods.Where(m => m.Name == "From" && m.HasGenericParameters).ToList())
        {
            var body = from.Body.Instructions;
            var ldftn = body.Single(i => i.OpCode == OpCodes.Ldftn);
            var trampoline = ((GenericInstanceMethod)ldftn.Operand).ElementMethod.Resolve();
            var create = (MethodReference)body.Single(i => i.OpCode == OpCodes.Call).Operand;

            // static void <From>g__ProofInvoke|N<T...>(object delegateObj, IntPtr args, IntPtr ret) => Trampoline<T...>(delegateObj, *args, out *ret)
            var proofInvoke = new MethodDefinition(trampoline.Name.Replace("Trampoline", "ProofInvoke"), MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.Static, ts.Void)
            {
                Parameters = { new("delegateObj", ParameterAttributes.None, ts.Object), new("args", ParameterAttributes.None, ts.IntPtr), new("ret", ParameterAttributes.None, ts.IntPtr) },
            };
            foreach (var gp in from.GenericParameters)
                proofInvoke.GenericParameters.Add(new GenericParameter(gp.Name, proofInvoke));
            var callTrampoline = new GenericInstanceMethod(trampoline);
            foreach (var gp in proofInvoke.GenericParameters)
                callTrampoline.GenericArguments.Add(gp);
            il = proofInvoke.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldobj, ptrArgs);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, callTrampoline);
            il.Emit(OpCodes.Ret);
            callable.Methods.Add(proofInvoke);

            // From<T...>(d) => CreateWithUnsafeTrampoline(d, Register(d, new Action<object, IntPtr, IntPtr>(ProofInvoke<T...>)))
            var proofInvokeInst = new GenericInstanceMethod(proofInvoke);
            foreach (var gp in from.GenericParameters)
                proofInvokeInst.GenericArguments.Add(gp);
            body.Clear();
            il = from.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldftn, proofInvokeInst);
            il.Emit(OpCodes.Newobj, invokerCtor);
            il.Emit(OpCodes.Call, register);
            il.Emit(OpCodes.Call, create);
            il.Emit(OpCodes.Ret);
            rewritten++;
        }
        if (rewritten != GenericFromCount)
            throw new InvalidOperationException($"expected {GenericFromCount} generic Callable.From methods, rewrote {rewritten}");

        var pinned = 0;
        foreach (var method in module.GetTypes().SelectMany(type => type.Methods).Where(HandlesFunctionPointers))
        {
            if ((method.ImplAttributes & MethodImplAttributes.NoInlining) == 0)
            {
                method.ImplAttributes |= MethodImplAttributes.NoInlining;
                pinned++;
            }
        }
        module.Write(output);
        return $"rewrote {rewritten} generic Callable.From methods, marked {pinned} function pointer methods NoInlining";
    }

    public static string Verify(string path)
    {
        using var module = ModuleDefinition.ReadModule(path);
        var callable = module.GetType("Godot.Callable");
        var generic = callable.Methods.Where(m => m.Name == "From" && m.HasGenericParameters).ToList();
        var handling = module.GetTypes().SelectMany(type => type.Methods).Where(HandlesFunctionPointers).ToList();
        var trampoline = callable.Fields.Single(field => field.Name == "_trampoline");
        return JsonSerializer.Serialize(new
        {
            // Workaround 3.
            trampoline_is_function_pointer = trampoline.FieldType is FunctionPointerType,
            trampoline_has_modifiers = HasModifiers(trampoline.FieldType),
            // Workaround 7.
            holder_type = module.GetType("Godot", Holder) is not null,
            generic_from = generic.Count,
            generic_from_rewritten = generic.Count(CallsRegister),
            generic_from_with_own_trampoline = generic.Count(m => m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldftn && IsTrampolineReference(i.Operand))),
            // Workaround 10.
            function_pointer_methods = handling.Count,
            function_pointer_methods_inlinable = handling.Count(m => (m.ImplAttributes & MethodImplAttributes.NoInlining) == 0),
        });
    }

    private static bool CallsRegister(MethodDefinition method) =>
        method.Body.Instructions.Any(i => i.OpCode == OpCodes.Call
            && i.Operand is MethodReference { Name: "Register" } target
            && target.DeclaringType.Name == Holder);

    private static bool IsTrampolineReference(object operand) =>
        operand is MethodReference method && method.Name.Contains("Trampoline", StringComparison.Ordinal);

    private static bool HandlesFunctionPointers(MethodDefinition method) =>
        method.HasBody && !method.HasGenericParameters
        && (method.Parameters.Any(p => IsFunctionPointer(p.ParameterType)) || IsFunctionPointer(method.ReturnType)
            || method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldftn || i.OpCode == OpCodes.Calli));

    private static bool IsFunctionPointer(TypeReference type) =>
        type is FunctionPointerType || (type is TypeSpecification specification && IsFunctionPointer(specification.ElementType));

    private static bool HasModifiers(TypeReference type) => type switch
    {
        IModifierType => true,
        FunctionPointerType pointer => HasModifiers(pointer.ReturnType) || pointer.Parameters.Any(p => HasModifiers(p.ParameterType)),
        TypeSpecification specification => HasModifiers(specification.ElementType),
        _ => false,
    };

    private static TypeReference Strip(TypeReference type, out bool did)
    {
        did = false;
        switch (type)
        {
            case RequiredModifierType required when ContainsFunctionPointer(required.ElementType):
                did = true;
                return Strip(required.ElementType, out _);
            case OptionalModifierType optional when ContainsFunctionPointer(optional.ElementType):
                did = true;
                return Strip(optional.ElementType, out _);
            case FunctionPointerType pointer:
            {
                var copy = new FunctionPointerType
                {
                    ReturnType = StripAny(pointer.ReturnType, ref did),
                    HasThis = pointer.HasThis,
                    ExplicitThis = pointer.ExplicitThis,
                    CallingConvention = pointer.CallingConvention,
                };
                foreach (var parameter in pointer.Parameters)
                    copy.Parameters.Add(new ParameterDefinition(StripAny(parameter.ParameterType, ref did)));
                return did ? copy : pointer;
            }
            default:
                return type;
        }
    }

    private static TypeReference StripAny(TypeReference type, ref bool did)
    {
        while (type is IModifierType modifier)
        {
            type = modifier.ElementType;
            did = true;
        }
        if (type is ByReferenceType reference)
        {
            var inner = reference.ElementType;
            var any = false;
            while (inner is IModifierType modifier)
            {
                inner = modifier.ElementType;
                any = true;
            }
            if (any)
            {
                did = true;
                return new ByReferenceType(inner);
            }
        }
        return type;
    }

    private static bool ContainsFunctionPointer(TypeReference type)
    {
        while (type is IModifierType modifier)
            type = modifier.ElementType;
        return type is FunctionPointerType;
    }
}
