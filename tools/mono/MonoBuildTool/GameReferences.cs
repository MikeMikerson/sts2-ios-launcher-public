using Mono.Cecil;

namespace STS2Mobile.Tools.Mono;

public static class GameReferences
{
    private const string GameAssembly = "sts2";

    /// <summary>
    /// The game-facing assemblies compile against a stub of the game's API, so a wrong stub only shows at run time. This
    /// resolves every type and member they use from the game against the real assembly, and returns what it cannot find.
    /// </summary>
    public static List<string> Missing(string game, string[] searchFolders, IEnumerable<string> assemblies)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(game))!);
        foreach (var folder in searchFolders) resolver.AddSearchDirectory(folder);
        var missing = new List<string>();
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        foreach (var path in assemblies)
        {
            using var module = ModuleDefinition.ReadModule(path, parameters);
            var name = Path.GetFileName(path);
            foreach (var type in module.GetTypeReferences().Where(type => type.Scope.Name == GameAssembly))
                if (type.Resolve() == null) missing.Add($"{name}: type {type.FullName}");
            foreach (var member in module.GetMemberReferences().Where(member => member.DeclaringType?.Scope.Name == GameAssembly))
            {
                var found = member switch
                {
                    MethodReference method => (object?)method.Resolve(),
                    FieldReference field => field.Resolve(),
                    _ => member,
                };
                if (found == null) missing.Add($"{name}: member {member.FullName}");
            }
        }
        return missing;
    }
}
