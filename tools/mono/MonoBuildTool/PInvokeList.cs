using Mono.Cecil;

namespace STS2Mobile.Tools.Mono;

public static class PInvokeList
{
    public static IEnumerable<string> Entries(IEnumerable<string> paths)
    {
        var all = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            using var module = ModuleDefinition.ReadModule(path);
            foreach (var method in module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasPInvokeInfo))
                all.Add($"{method.PInvokeInfo.Module.Name}\t{method.PInvokeInfo.EntryPoint ?? method.Name}");
        }
        return all;
    }
}
