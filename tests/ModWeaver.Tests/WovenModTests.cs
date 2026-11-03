using System.Reflection;
using Mono.Cecil;
using STS2Mobile.Platform;

namespace STS2Mobile.ModWeaver.Tests;

public sealed class WovenModTests : IClassFixture<WovenGoodMod>
{
    private readonly WovenGoodMod _woven;
    private readonly Type _screen;
    private readonly List<string> _log;

    public WovenModTests(WovenGoodMod woven)
    {
        _woven = woven;
        _screen = woven.Game.GetType("Game.Screen")!;
        _log = (List<string>)woven.Game.GetType("Game.Log")!.GetProperty("Lines")!.GetValue(null)!;
        _log.Clear();
    }

    [Fact]
    public void EveryPrefixAndPostfixIsWoven() => Assert.Equal(11, _woven.Report.Patches.Count);

    [Fact]
    public void NoSteamworksUseIsReported() => Assert.Empty(_woven.Report.SteamworksCalls);

    [Fact]
    public void WovenModNoLongerDependsOnHarmony()
    {
        using var module = ModuleDefinition.ReadModule(_woven.WovenModPath);
        Assert.DoesNotContain(module.AssemblyReferences, reference => reference.Name == "0Harmony");
        Assert.DoesNotContain(module.GetTypeReferences(), reference => reference.Scope.Name == "0Harmony");
    }

    [Fact]
    public void WeaversMarkNamesTheModAndItsPatchCount()
    {
        var mark = _woven.Mod.GetCustomAttribute<WovenModAttribute>()!;
        Assert.Equal("GoodMod", mark.ModId);
        Assert.Equal(11, mark.Patches);
    }

    [Fact]
    public void PostfixKeepsAnOrdinaryResult()
    {
        Assert.True((bool)Call(NewScreen(1), "IsReady")!);
        Assert.Equal(["ready"], _log);
    }

    [Fact]
    public void PostfixRewritesTheResultThroughRefResult()
    {
        Assert.False((bool)Call(NewScreen(7), "IsReady")!);
        Assert.Equal(["ready"], _log);
    }

    [Fact]
    public void PrefixReturningTrueRunsTheOriginalAfterIt()
    {
        Call(NewScreen(1), "Press", 2);
        Assert.Equal(["prefix 2", "press 2"], _log);
    }

    [Fact]
    public void PrefixReturningFalseSkipsTheOriginal()
    {
        Call(NewScreen(1), "Press", 3);
        Assert.Equal(["prefix 3"], _log);
    }

    [Fact]
    public void PostfixOnAnInheritedVirtualMethodSeesTheInstance()
    {
        Call(NewScreen(1), "Closed");
        Assert.Equal(["base closed", "closed postfix Screen"], _log);
    }

    [Fact]
    public void PrefixChangesAByReferenceArgumentTheOriginalThenUses()
    {
        object?[] arguments = ["x", 0];
        Assert.Equal("x11", _screen.GetMethod("Name")!.Invoke(NewScreen(1), arguments));
        Assert.Equal(11, arguments[1]);
        Assert.Equal(["name x11"], _log);
    }

    [Fact]
    public void SkippingPrefixSuppliesTheResultAndThePostfixStillRuns()
    {
        object?[] arguments = ["skip", 0];
        Assert.Equal("skipped", _screen.GetMethod("Name")!.Invoke(NewScreen(1), arguments));
        Assert.Equal(0, arguments[1]);
        Assert.Equal(["name skipped"], _log);
    }

    [Fact]
    public void PostfixRunsAfterAFinallyBlock()
    {
        Assert.Equal(10, Call(NewScreen(1), "WithTry", 5));
        Assert.Equal(["try", "finally"], _log);
    }

    [Fact]
    public void PostfixSeesAValueReturnedInsideTry()
    {
        Assert.Equal(-2, Call(NewScreen(1), "WithTry", -4));
        Assert.Equal(["finally"], _log);
    }

    [Fact]
    public void OnlyTheOverloadNamedByArgumentTypesIsPatched()
    {
        Call(NewScreen(1), "Overloaded", "s");
        Call(NewScreen(1), "Overloaded", 4);
        Assert.Equal(["overload prefix s", "string", "int"], _log);
    }

    [Fact]
    public void FieldReferenceWritesPersistAndValueArgumentsReadTheUpdatedField()
    {
        var screen = NewScreen(1);
        Assert.Equal(24, Call(screen, "ReadSecret"));
        Assert.Equal(28, Call(screen, "ReadSecret"));
        Assert.Equal(7, _screen.GetField("Value")!.GetValue(screen));
        Assert.Equal(["secret 12", "secret 14"], _log);
    }

    [Fact]
    public void StaticFieldReferenceWritesAreVisibleToTheOriginal()
    {
        Assert.Equal(31, _screen.GetMethod("ReadShared")!.Invoke(null, null));
    }

    [Fact]
    public void PrivateInheritedFieldCanBeReadAndWrittenFromADerivedTarget()
    {
        Assert.Equal(24, Call(NewScreen(1), "ReadInherited"));
    }

    private object NewScreen(int value)
    {
        var screen = Activator.CreateInstance(_screen)!;
        _screen.GetField("Value")!.SetValue(screen, value);
        return screen;
    }

    private object? Call(object target, string name, params object?[] arguments) =>
        _screen.GetMethods()
            .Single(method => method.Name == name && (name != "Overloaded" || method.GetParameters()[0].ParameterType == arguments[0]!.GetType()))
            .Invoke(target, arguments);
}
