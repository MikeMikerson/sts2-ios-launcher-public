using System.Security.Cryptography;
using Mono.Cecil;

namespace STS2Mobile.Tools;

public static class CecilHelpers
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

    public static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
