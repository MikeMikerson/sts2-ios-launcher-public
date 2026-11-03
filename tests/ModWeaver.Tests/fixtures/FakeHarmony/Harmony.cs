using System.Reflection;

namespace HarmonyLib;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class HarmonyPatch : Attribute
{
    public HarmonyPatch() { }
    public HarmonyPatch(Type declaringType) { }
    public HarmonyPatch(string methodName) { }
    public HarmonyPatch(Type declaringType, string methodName) { }
    public HarmonyPatch(Type declaringType, string methodName, Type[] argumentTypes) { }
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyPrefix : Attribute;

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyPostfix : Attribute;

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyTranspiler : Attribute;

public class Harmony(string id)
{
    public string Id { get; } = id;
    public void PatchAll(Assembly assembly) => throw new NotSupportedException();
    public void PatchAll() => throw new NotSupportedException();
}
