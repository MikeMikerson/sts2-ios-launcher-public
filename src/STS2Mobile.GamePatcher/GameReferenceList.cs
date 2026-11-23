using Mono.Cecil;

namespace STS2Mobile.GamePatcher;

/// <summary>
/// The game's types and members the app's own code compiles against (STS2Mobile.GameHost and STS2Mobile.GameAdapters, against the game
/// stub), one per line. <c>scripts/list-game-references.sh</c> lists them when the app is built (MonoBuildTool list-game-references,
/// which shares this file), the patcher embeds the list, and preparation checks each build against it, so a build that lacks one is
/// refused before it loads instead of failing in the game with a MissingMethodException.
/// The lines name types the same way on both sides, with generic parameters by position, so a reference and the definition it
/// binds to give the same line:
/// <code>
/// type MegaCrit.Sts2.Core.Nodes.NGame
/// method MegaCrit.Sts2.Core.Nodes.NGame::get_Instance() MegaCrit.Sts2.Core.Nodes.NGame
/// field MegaCrit.Sts2.Core.Runs.RunState::Players System.Collections.Generic.List&lt;MegaCrit.Sts2.Core.Entities.Players.Player&gt;
/// </code>
/// Mono binds a member by its name and signature as above, so a renamed method or a changed parameter is missing here as it would be
/// in the game. Default parameter values and attributes are not compared.
/// </summary>
public static class GameReferenceList
{
    public const string GameAssemblyName = "sts2";
    public const string ResourceName = "game-references.txt";
    private const string Type = "type ";
    private const string Method = "method ";
    private const string Field = "field ";

    public static IReadOnlyList<string> Parse(string text) =>
        text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();

    /// <summary>The text of a list, as the build writes it.</summary>
    public static string Text(IEnumerable<string> lines) =>
        "# The game members STS2Mobile.GameHost and STS2Mobile.GameAdapters use; made by scripts/list-game-references.sh, checked at preparation.\n"
        + string.Concat(lines.Select(line => line + "\n"));

    /// <summary>Every game type and member the modules refer to, sorted and without repeats.</summary>
    public static IReadOnlyList<string> Of(IEnumerable<ModuleDefinition> modules)
    {
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            foreach (var type in module.GetTypeReferences().Where(type => type.Scope?.Name == GameAssemblyName))
                lines.Add(Type + type.FullName);
            foreach (var member in module.GetMemberReferences())
            {
                // Members of an array type are the runtime's, whatever the element.
                if (member.DeclaringType is not { } declaring || declaring.Scope?.Name != GameAssemblyName || declaring is ArrayType) continue;
                string owner = Owner(declaring);
                if (member is MethodReference method) lines.Add(MethodLine(owner, method));
                else if (member is FieldReference field) lines.Add(FieldLine(owner, field));
            }
        }
        return lines.ToArray();
    }

    /// <summary>The lines <paramref name="game"/> does not have, in the list's order.</summary>
    public static IReadOnlyList<string> Missing(ModuleDefinition game, IEnumerable<string> lines)
    {
        var types = game.GetTypes().ToDictionary(type => type.FullName, StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types.Values)
        {
            present.Add(Type + type.FullName);
            foreach (var method in type.Methods) present.Add(MethodLine(type.FullName, method));
            foreach (var field in type.Fields) present.Add(FieldLine(type.FullName, field));
        }
        return lines.Where(line => !present.Contains(line) && !Inherited(line, types, present)).ToArray();
    }

    /// <summary>A member found on a base type within the game, as the runtime would find it.</summary>
    private static bool Inherited(string line, Dictionary<string, TypeDefinition> types, HashSet<string> present)
    {
        string kind = line.StartsWith(Method, StringComparison.Ordinal) ? Method : line.StartsWith(Field, StringComparison.Ordinal) ? Field : "";
        int separator = line.IndexOf("::", StringComparison.Ordinal);
        if (kind.Length == 0 || separator < 0 || !types.TryGetValue(line[kind.Length..separator], out var type)) return false;
        string member = line[separator..];
        // The dictionary holds the game's types only, so the walk stops where the base types leave the game (Godot's, for a node).
        for (var parent = type.BaseType; parent != null && types.TryGetValue(Owner(parent), out var definition); parent = definition.BaseType)
            if (present.Contains(kind + definition.FullName + member)) return true;
        return false;
    }

    private static string Owner(TypeReference type) => type is GenericInstanceType generic ? generic.ElementType.FullName : type.FullName;

    private static string MethodLine(string owner, MethodReference method) =>
        $"{Method}{owner}::{method.Name}{(method.GenericParameters.Count > 0 ? "`" + method.GenericParameters.Count : "")}"
        + $"({string.Join(",", method.Parameters.Select(parameter => Name(parameter.ParameterType)))}) {Name(method.ReturnType)}";

    private static string FieldLine(string owner, FieldReference field) => $"{Field}{owner}::{field.Name} {Name(field.FieldType)}";

    /// <summary>A type as a signature names it, with generic parameters by position (<c>!0</c> of the type, <c>!!0</c> of the method).</summary>
    private static string Name(TypeReference type) => type switch
    {
        GenericParameter parameter => (parameter.Type == GenericParameterType.Method ? "!!" : "!") + parameter.Position,
        ByReferenceType reference => Name(reference.ElementType) + "&",
        PointerType pointer => Name(pointer.ElementType) + "*",
        ArrayType array => Name(array.ElementType) + (array.Rank == 1 ? "[]" : "[" + new string(',', array.Rank - 1) + "]"),
        RequiredModifierType required => $"{Name(required.ElementType)} modreq({Name(required.ModifierType)})",
        OptionalModifierType optional => $"{Name(optional.ElementType)} modopt({Name(optional.ModifierType)})",
        GenericInstanceType generic => $"{Name(generic.ElementType)}<{string.Join(",", generic.GenericArguments.Select(Name))}>",
        PinnedType pinned => Name(pinned.ElementType),
        SentinelType sentinel => Name(sentinel.ElementType),
        _ => type.FullName,
    };
}
