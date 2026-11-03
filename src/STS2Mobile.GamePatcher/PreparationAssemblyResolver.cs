using Mono.Cecil;

namespace STS2Mobile.GamePatcher;

/// <summary>
/// Resolves the game's inputs and preparation metadata without loading executable assemblies.
/// Framework and Harmony reference assemblies are embedded so the same metadata is available on iOS.
/// </summary>
public sealed class PreparationAssemblyResolver(string folder) : IAssemblyResolver
{
    private readonly Dictionary<string, AssemblyDefinition> _read = new(StringComparer.Ordinal);
    private readonly List<AssemblyDefinition> _opened = [];

    /// <summary>The current in-memory game answers its own references while patches are applied.</summary>
    public void Register(AssemblyDefinition assembly) => _read[assembly.Name.Name] = assembly;

    public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());

    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (_read.TryGetValue(name.Name, out var known)) return known;
        // Never use the machine's framework or Harmony installation. Those can differ from iOS.
        var embedded = typeof(PreparationAssemblyResolver).Assembly.GetManifestResourceStream("metadata/" + name.Name + ".dll");
        var reader = new ReaderParameters { AssemblyResolver = this, ReadingMode = ReadingMode.Deferred, InMemory = true };
        AssemblyDefinition assembly;
        if (embedded != null)
        {
            using (embedded)
            {
                var data = new MemoryStream();
                embedded.CopyTo(data);
                data.Position = 0;
                assembly = AssemblyDefinition.ReadAssembly(data, reader);
            }
        }
        else
        {
            if (name.Name.IndexOfAny(['/', '\\']) >= 0) throw new AssemblyResolutionException(name);
            string path = Path.Combine(folder, name.Name + ".dll");
            if (!File.Exists(path)) throw new AssemblyResolutionException(name);
            assembly = AssemblyDefinition.ReadAssembly(path, reader);
        }
        _read[name.Name] = assembly;
        _opened.Add(assembly);
        return assembly;
    }

    public void Dispose()
    {
        foreach (var assembly in _opened) assembly.Dispose();
    }
}
