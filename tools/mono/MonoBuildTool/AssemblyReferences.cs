using Mono.Cecil;

namespace STS2Mobile.Tools.Mono;

public static class AssemblyReferences
{
    private const int MostForwards = 8;

    /// <summary>
    /// Walks what the roots refer to, type by type, through the facades to the assembly that defines each type, and returns
    /// what is not in the publish folder. Following types and not assembly references matters: a facade such as netstandard
    /// refers to nearly everything, and only the types used through it need their assembly. Names built in strings at run
    /// time cannot be seen here.
    /// </summary>
    public static List<string> Missing(string publish, IEnumerable<string> absentByDesign, IEnumerable<string> roots)
    {
        var modules = new Dictionary<string, ModuleDefinition?>(StringComparer.OrdinalIgnoreCase);
        var absent = new HashSet<string>(absentByDesign, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ModuleDefinition>();
        var missing = new SortedSet<string>();

        ModuleDefinition? Load(string name)
        {
            if (modules.TryGetValue(name, out var known)) return known;
            var path = Path.Combine(publish, name + ".dll");
            return modules[name] = File.Exists(path) ? ModuleDefinition.ReadModule(path) : null;
        }

        string Defining(string assembly, string type, int depth = 0)
        {
            var forward = depth < MostForwards
                ? Load(assembly)?.ExportedTypes.FirstOrDefault(exported => exported.IsForwarder && exported.FullName == type)
                : null;
            return forward == null ? assembly : Defining(forward.Scope.Name, type, depth + 1);
        }

        foreach (var root in roots)
        {
            var module = ModuleDefinition.ReadModule(root);
            modules[module.Assembly.Name.Name] = module;
            if (seen.Add(module.Assembly.Name.Name)) queue.Enqueue(module);
        }
        while (queue.TryDequeue(out var module))
        {
            foreach (var type in module.GetTypeReferences())
            {
                if (type.Scope is not AssemblyNameReference reference) continue;
                var outer = type;
                while (outer.DeclaringType != null) outer = outer.DeclaringType;
                foreach (var name in new[] { reference.Name, Defining(reference.Name, outer.FullName) })
                {
                    if (absent.Contains(name) || !seen.Add(name)) continue;
                    var found = Load(name);
                    if (found == null) missing.Add($"{module.Assembly.Name.Name} needs {name} ({outer.FullName})");
                    else queue.Enqueue(found);
                }
            }
        }
        return [.. missing];
    }
}
