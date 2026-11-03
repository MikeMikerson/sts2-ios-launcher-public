using Mono.Cecil;
using STS2Mobile.GamePatcher;

namespace STS2Mobile.GamePatcher.Tests;

public sealed class PreparationAssemblyResolverTests
{
    [Theory]
    [InlineData("System.Runtime", "System.Runtime.CompilerServices.MethodImplOptions")]
    [InlineData("System.Runtime", "System.Reflection.BindingFlags")]
    [InlineData("0Harmony", "HarmonyLib.MethodType")]
    public void EmbeddedMetadataResolvesEnumsWithoutAnyInputFiles(string assemblyName, string typeName)
    {
        using var resolver = new PreparationAssemblyResolver(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var assembly = resolver.Resolve(new AssemblyNameReference(assemblyName, new Version(9, 0)));
        var type = assembly.MainModule.GetType(typeName);
        Assert.NotNull(type);
        Assert.True(type.IsEnum);
        Assert.Equal("System.Int32", type.Fields.Single(f => f.Name == "value__").FieldType.FullName);
        Assert.Same(assembly, resolver.Resolve(assembly.Name));
    }

    [Theory]
    [InlineData("System.IO")]
    [InlineData("System.Reflection.Metadata")]
    public void FrameworkNamesThatResembleCulturesStayInTheMainAssembly(string name)
    {
        using var resolver = new PreparationAssemblyResolver("missing");
        var assembly = resolver.Resolve(new AssemblyNameReference(name, new Version(9, 0)));
        Assert.Equal(name, assembly.Name.Name);
        Assert.Contains("metadata/" + name + ".dll", typeof(PreparationAssemblyResolver).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public void RegisteredGameWinsOverAnyFile()
    {
        using var resolver = new PreparationAssemblyResolver("missing");
        using var game = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("sts2", new Version(1, 0)), "sts2", ModuleKind.Dll);
        resolver.Register(game);
        Assert.Same(game, resolver.Resolve(game.Name));
    }

    [Fact]
    public void MissingDependenciesAreNotTakenFromTheWorkingDirectory()
    {
        using var resolver = new PreparationAssemblyResolver("missing");
        Assert.Throws<AssemblyResolutionException>(() => resolver.Resolve(new AssemblyNameReference("UnknownModDependency", new Version(1, 0))));
    }

    [Theory]
    [InlineData("0Harmony", "HarmonyLib.MethodType")]
    [InlineData("System.Runtime", "System.Runtime.CompilerServices.MethodImplOptions")]
    public void ImmediateReadDecodesAnAttributeWithAnExternalEnum(string assemblyName, string typeName)
    {
        using var resolver = new PreparationAssemblyResolver("missing");
        using var fixture = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Mod", new Version(1, 0)), "Mod", new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = resolver });
        var module = fixture.MainModule;
        var scope = new AssemblyNameReference(assemblyName, new Version(9, 0));
        module.AssemblyReferences.Add(scope);
        int dot = typeName.LastIndexOf('.');
        var enumType = new TypeReference(typeName[..dot], typeName[(dot + 1)..], module, scope, true);
        var attributeType = new TypeDefinition("Example", "ModAttribute", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(attributeType);
        var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
        attributeType.Methods.Add(ctor);
        ctor.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
        ctor.Parameters.Add(new ParameterDefinition(enumType));
        var attribute = new CustomAttribute(ctor);
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(enumType, 1));
        fixture.CustomAttributes.Add(attribute);
        using var data = new MemoryStream();
        fixture.Write(data);
        data.Position = 0;
        using var read = AssemblyDefinition.ReadAssembly(data, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate });
        Assert.Equal(1, read.CustomAttributes.Single().ConstructorArguments.Single().Value);
    }
}
