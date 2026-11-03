using Mono.Cecil;

namespace STS2Mobile.GamePatcher;

public static class CecilWalk
{
    public static IEnumerable<TypeDefinition> Flatten(IEnumerable<TypeDefinition> roots)
    {
        foreach (var type in roots)
        {
            yield return type;
            foreach (var child in Flatten(type.NestedTypes))
                yield return child;
        }
    }
}
