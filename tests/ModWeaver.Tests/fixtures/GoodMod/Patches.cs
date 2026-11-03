using System.Reflection;
using Game;
using HarmonyLib;

namespace GoodMod;

public static class Main
{
    public static void Initialize() => new Harmony("good.mod").PatchAll(Assembly.GetExecutingAssembly());
    public static void InitializeWithoutAssembly() => new Harmony("good.mod").PatchAll();
}

// Postfix changing a bool result through ref __result, with __instance.
[HarmonyPatch(typeof(Screen), nameof(Screen.IsReady))]
internal static class ReadyPatch
{
    public static void Postfix(Screen __instance, ref bool __result)
    {
        if (__instance.Value == 7) __result = false;
    }
}

// Bool prefix on a private-to-the-mod class: false skips the original.
[HarmonyPatch(typeof(Screen), "Press")]
internal static class PressPatch
{
    private static bool Prefix(int size)
    {
        Log.Add("prefix " + size);
        return size != 3;
    }
}

// Postfix on a virtual base-class method, such as NSubmenu.OnSubmenuClosed in the game.
[HarmonyPatch]
public static class Outer
{
    [HarmonyPatch(typeof(Base), nameof(Base.Closed))]
    public static class ClosedPatch
    {
        public static void Postfix(Base __instance) => Log.Add("closed postfix " + __instance.GetType().Name);
    }
}

[HarmonyPatch(typeof(Screen), nameof(Screen.ReadSecret))]
internal static class FieldPatch
{
    private static void Prefix(ref int ___secret) => ___secret += 2;
    private static void Postfix(int ___secret, ref int ___Value, ref int __result)
    {
        Log.Add("secret " + ___secret);
        ___Value += 3;
        __result += ___secret;
    }
}

[HarmonyPatch(typeof(Screen), nameof(Screen.ReadShared))]
internal static class StaticFieldPatch
{
    private static void Prefix(ref int ___shared) => ___shared += 1;
}

[HarmonyPatch(typeof(Screen), nameof(Screen.ReadInherited))]
internal static class InheritedFieldPatch
{
    private static void Prefix(ref int ___inherited) => ___inherited += 4;
}

// Attribute-marked prefix with by-reference arguments and result; postfix reads the result by value.
[HarmonyPatch(typeof(Screen), nameof(Screen.Name))]
internal static class NamePatch
{
    [HarmonyPrefix]
    private static bool Before(string prefix, ref int counter, ref string __result)
    {
        if (prefix == "skip")
        {
            __result = "skipped";
            return false;
        }
        counter += 10;
        return true;
    }

    private static void Postfix(string __result) => Log.Add("name " + __result);
}

// Postfix on a method that returns from inside try/finally; split [HarmonyPatch] attributes.
[HarmonyPatch(typeof(Screen))]
[HarmonyPatch(nameof(Screen.WithTry))]
internal static class TryPatch
{
    private static void Postfix(ref int __result) => __result *= 2;
}

// Overload chosen by argument types.
[HarmonyPatch(typeof(Screen), nameof(Screen.Overloaded), new[] { typeof(string) })]
internal static class OverloadPatch
{
    private static void Prefix(string value) => Log.Add("overload prefix " + value);
}
