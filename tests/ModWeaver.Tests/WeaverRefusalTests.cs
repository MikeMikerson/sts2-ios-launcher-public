using Mono.Cecil;
using Mono.Cecil.Cil;

namespace STS2Mobile.ModWeaver.Tests;

// Mutated copies of the fixture mod: every unsupported or unsafe form is refused with its reason.
public class WeaverRefusalTests
{
    [Theory]
    [InlineData("transpiler", "Transpiler")]
    [InlineData("__state", "__state")]
    [InlineData("unknown argument", "does not have")]
    [InlineData("missing target", "not in the game")]
    [InlineData("overload ambiguity", "overloaded")]
    [InlineData("same target twice", "more than once")]
    [InlineData("wrong result type", "__result")]
    [InlineData("network use", "System.Net.Http.HttpClient")]
    [InlineData("other Harmony call", "UnpatchAll()")]
    [InlineData("assembly name", "named Other")]
    public void UnsupportedFormIsRefusedWithItsReason(string form, string reason)
    {
        var (game, mod) = GoodModWeaving.Read();
        Mutate(mod, form);
        var error = Assert.Throws<WeaveException>(() => GoodModWeaving.Weave(game, mod, out _));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", "no such field")]
    [InlineData("wrong type", "does not fit field")]
    [InlineData("instance from static", "does not fit field")]
    [InlineData("readonly reference", "does not fit field")]
    [InlineData("constant", "does not fit field")]
    [InlineData("bare prefix", "does not name a target")]
    [InlineData("bare target hook", "TargetMethods")]
    public void InvalidFieldsAndIncompletePatchGroupsAreRefused(string form, string reason)
    {
        var (game, mod) = GoodModWeaving.Read();
        var fieldPrefix = PatchMethod(mod, "FieldPatch", "Prefix");
        switch (form)
        {
            case "missing": fieldPrefix.Parameters[0].Name = "___missing"; break;
            case "wrong type": fieldPrefix.Parameters[0].ParameterType = new ByReferenceType(mod.MainModule.TypeSystem.String); break;
            case "instance from static": PatchMethod(mod, "StaticFieldPatch", "Prefix").Parameters[0].Name = "___secret"; break;
            case "readonly reference": fieldPrefix.Parameters[0].Name = "___fixedValue"; break;
            case "constant":
                var field = game.MainModule.GetType("Game.Screen").Fields.Single(f => f.Name == "secret");
                field.IsLiteral = true;
                field.Constant = 10;
                break;
            case "bare prefix":
            case "bare target hook":
                PatchType(mod, "Outer").Methods.Add(new MethodDefinition(
                    form == "bare prefix" ? "Prefix" : "TargetMethods",
                    Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.Public, mod.MainModule.TypeSystem.Void));
                break;
        }
        var error = Assert.Throws<WeaveException>(() => GoodModWeaving.Weave(game, mod, out _));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    private static void Mutate(AssemblyDefinition mod, string form)
    {
        var module = mod.MainModule;
        switch (form)
        {
            case "transpiler":
                PatchType(mod, "PressPatch").Methods.Add(
                    new MethodDefinition("Transpiler", Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Void));
                break;
            case "__state":
                PatchMethod(mod, "PressPatch", "Prefix").Parameters[0].Name = "__state";
                break;
            case "unknown argument":
                PatchMethod(mod, "PressPatch", "Prefix").Parameters[0].Name = "width";
                break;
            case "missing target":
                PatchType(mod, "PressPatch").CustomAttributes[0].ConstructorArguments[1] = new CustomAttributeArgument(module.TypeSystem.String, "Missing");
                break;
            case "overload ambiguity":
                PatchType(mod, "PressPatch").CustomAttributes[0].ConstructorArguments[1] = new CustomAttributeArgument(module.TypeSystem.String, "Overloaded");
                break;
            case "same target twice":
                PatchType(mod, "TryPatch").CustomAttributes[1].ConstructorArguments[0] = new CustomAttributeArgument(module.TypeSystem.String, "IsReady");
                break;
            case "wrong result type":
                PatchMethod(mod, "TryPatch", "Postfix").Parameters[0].ParameterType = new ByReferenceType(module.TypeSystem.String);
                break;
            case "network use":
                {
                    var initialize = PatchMethod(mod, "Main", "Initialize");
                    var client = new TypeReference("System.Net.Http", "HttpClient", module, module.TypeSystem.CoreLibrary);
                    var il = initialize.Body.GetILProcessor();
                    il.InsertBefore(initialize.Body.Instructions[0], il.Create(OpCodes.Pop));
                    il.InsertBefore(initialize.Body.Instructions[0], il.Create(OpCodes.Newobj, new MethodReference(".ctor", module.TypeSystem.Void, client) { HasThis = true }));
                    break;
                }
            case "other Harmony call":
                {
                    var initialize = PatchMethod(mod, "Main", "Initialize");
                    var patchAll = initialize.Body.Instructions.Single(instruction => instruction.Operand is MethodReference { Name: "PatchAll" });
                    var harmony = ((MethodReference)patchAll.Operand).DeclaringType;
                    initialize.Body.GetILProcessor().Remove(patchAll.Previous);
                    patchAll.Operand = new MethodReference("UnpatchAll", module.TypeSystem.Void, harmony) { HasThis = true };
                    break;
                }
            case "assembly name":
                mod.Name.Name = "Other";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(form));
        }
    }

    private static TypeDefinition PatchType(AssemblyDefinition mod, string name) =>
        CecilWalk.Flatten(mod.MainModule.Types).Single(type => type.Name == name);

    private static MethodDefinition PatchMethod(AssemblyDefinition mod, string type, string name) =>
        PatchType(mod, type).Methods.Single(method => method.Name == name);
}
