using Mono.Cecil;
using Mono.Cecil.Cil;

namespace STS2Mobile.GamePatcher;

internal static class SentryRemoval
{
    public static IEnumerable<PatchStep> Steps => [new("Crash reporting SDK removed", true, Apply)];

    private static void Apply(GamePatch patch)
    {
        var module = patch.Module;
        foreach (var method in patch.Type("<Module>").Methods) patch.Noop(method, "Remove Sentry module initialization.");
        // Remove the complete reporting service implementation before mapping residual callback signatures.
        foreach (var type in patch.Types.Where(t => t.FullName.StartsWith("MegaCrit.Sts2.Core.Debug.SentryService") ||
            t.FullName.StartsWith("MegaCrit.Sts2.Core.Debug.SentryExtension") || t.FullName == "SentryAutoInit"))
            foreach (var method in type.Methods.Where(m => m.HasBody)) patch.Noop(method, "Remove Sentry reporting implementation.");
        var disabled = new TypeDefinition("STS2Mobile.Privacy", "RemovedReportingCalls",
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        module.Types.Add(disabled);
        int sentryCalls = 0;
        foreach (var method in patch.MethodsWithBodies)
            foreach (var instruction in method.Body.Instructions.ToArray())
            {
                if (instruction.Operand is not MethodReference target || !target.DeclaringType.Scope.Name.StartsWith("Sentry")) continue;
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt && instruction.OpCode != OpCodes.Newobj)
                    throw new InvalidOperationException("Unexpected Sentry operand: " + target.FullName);
                if (target is GenericInstanceMethod || target.ContainsGenericParameter)
                    throw new InvalidOperationException("Unexpected generic reporting call.");
                var stub = new MethodDefinition("Removed" + sentryCalls++, MethodAttributes.Assembly | MethodAttributes.Static,
                    instruction.OpCode == OpCodes.Newobj ? target.DeclaringType : target.ReturnType);
                if (target.HasThis && instruction.OpCode != OpCodes.Newobj)
                    stub.Parameters.Add(new ParameterDefinition(target.DeclaringType.IsValueType ? new ByReferenceType(target.DeclaringType) : target.DeclaringType));
                foreach (var parameter in target.Parameters) stub.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
                disabled.Methods.Add(stub);
                patch.Noop(stub, "Remove direct Sentry SDK call: " + target.FullName);
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = stub;
            }
        foreach (var attribute in patch.Assembly.CustomAttributes.Where(a => a.AttributeType.Scope.Name.StartsWith("Sentry")).ToArray())
            patch.Assembly.CustomAttributes.Remove(attribute);
        // No SDK ships for dormant signatures. Erase reporting-only classes, enum and ID metadata.
        foreach (var reference in module.GetTypeReferences().Where(r => r.Scope.Name.StartsWith("Sentry")).ToArray())
        {
            var original = reference.Resolve() ?? throw new InvalidOperationException("Cannot resolve reporting type: " + reference.FullName);
            string name = original.IsEnum ? "Int32" : original.IsValueType ? "Guid" : "Object";
            if (original.IsValueType && !original.IsEnum && original.Name != "SentryId")
                throw new InvalidOperationException("Unexpected reporting struct: " + original.FullName);
            patch.Changes.Add(new PatchChange(null, reference.FullName, "Erase reporting metadata as System." + name));
            reference.Namespace = "System";
            reference.Name = name;
            reference.Scope = module.TypeSystem.CoreLibrary;
            reference.IsValueType = name != "Object";
        }
        // Harmony goes too: ModLoaderPatches redirected the mod loader's only calls into it.
        foreach (var reference in module.AssemblyReferences.Where(r => r.Name.StartsWith("Sentry") || r.Name == "0Harmony").ToArray())
            module.AssemblyReferences.Remove(reference);
    }
}
