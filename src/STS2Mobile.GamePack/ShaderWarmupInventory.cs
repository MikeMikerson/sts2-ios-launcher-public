using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace STS2Mobile.GamePack;

/// <summary>
/// The shader warmup inventory the host reads from <c>res://warmup/</c>: small scenes holding one hidden particle node per
/// distinct particle material, and the list of canvas shaders. A port of tools/prepare_shader_warmup.py, which explains why
/// the materials are told apart as they are. Its output has to stay byte for byte the same, which is why every sort here
/// follows Python's order and not the culture's.
/// </summary>
public static partial class ShaderWarmupInventory
{
    public const string Folder = "warmup/";
    private const string MaterialType = "ParticleProcessMaterial";
    private const int MaterialsPerScene = 4;
    private static readonly string[] IgnoredProperties = ["resource_name", "resource_local_to_scene", "script"];
    private static readonly char[] PythonLineBreaks = ['\n', '\r', '\v', '\f', '\x1c', '\x1d', '\x1e', '\x85', '\u2028', '\u2029'];
    /// <summary>Texture types whose content cannot change the shader, and the stand-in every material shares.</summary>
    private static readonly (string Kind, string Body)[] SharedTextures =
    [
        ("CurveTexture", "curve = SubResource(\"shared_curve\")"),
        ("CurveXYZTexture", "curve_x = SubResource(\"shared_curve\")\ncurve_y = SubResource(\"shared_curve\")\ncurve_z = SubResource(\"shared_curve\")"),
        ("GradientTexture1D", "gradient = SubResource(\"shared_gradient\")"),
    ];

    private static readonly string[] SharedBlocks =
    [
        "[sub_resource type=\"Curve\" id=\"shared_curve\"]",
        "[sub_resource type=\"Gradient\" id=\"shared_gradient\"]",
        .. SharedTextures.Select(texture => $"[sub_resource type=\"{texture.Kind}\" id=\"shared_{texture.Kind}\"]\n{texture.Body}"),
    ];

    [GeneratedRegex("^\\[(\\w+)((?: [^\\]\\n]*)?)\\]$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    [GeneratedRegex("(\\w+)=\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    [GeneratedRegex("SubResource\\(\"([^\"]+)\"\\)", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    [GeneratedRegex("\\A(?:true|false|\\d+)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SwitchValue();

    private sealed record Block(string Kind, string Id, string Body);

    /// <summary>The inventory's files, by pack name, in the pack's order.</summary>
    /// <param name="names">Every file of the game's resources.</param>
    /// <param name="read">Reads a scene as text.</param>
    public static List<(string Name, byte[] Data)> Build(IReadOnlyCollection<string> names, Func<string, string> read)
    {
        var (materials, seen, skipped) = Collect(names, read);
        if (materials.Count == 0) throw new PackException("No particle materials found; refusing to write an empty warmup inventory.");
        var scenes = new List<(string Name, byte[] Data)>();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int start = 0; start < materials.Count; start += MaterialsPerScene)
        {
            string name = $"particles_{scenes.Count:000}.tscn";
            byte[] scene = Encoding.UTF8.GetBytes(RenderScene(materials.GetRange(start, Math.Min(MaterialsPerScene, materials.Count - start))));
            digest.AppendData(scene);
            scenes.Add((name, scene));
        }
        var canvasShaders = names.Where(name => name.EndsWith(".gdshader", StringComparison.Ordinal)).Select(name => "res://" + name).Order(StringComparer.Ordinal).ToList();
        string inventory = RenderInventory(seen, skipped, materials.Count, scenes.Select(scene => scene.Name).ToList(), canvasShaders, Convert.ToHexStringLower(digest.GetHashAndReset()));
        var files = new List<(string Name, byte[] Data)> { (Folder + "inventory.json", Encoding.UTF8.GetBytes(inventory)) };
        files.AddRange(scenes.Select(scene => (Folder + scene.Name, scene.Data)));
        return files;
    }

    /// <summary>Distinct particle materials, most used first, each as blocks with the material last.</summary>
    private static (List<List<Block>> Materials, int Seen, int Skipped) Collect(IReadOnlyCollection<string> names, Func<string, string> read)
    {
        var distinct = new Dictionary<string, List<Block>>(StringComparer.Ordinal);
        var keys = new List<string[]>();
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        int seen = 0, skipped = 0;
        foreach (string scene in names.Where(name => name.EndsWith(".tscn", StringComparison.Ordinal)).Order(PathOrder.Instance))
        {
            string text = read(scene);
            if (!text.Contains(MaterialType, StringComparison.Ordinal)) continue;
            var resources = SubResources(text);
            foreach (string identifier in resources.Order)
            {
                var (kind, body) = resources.Found[identifier];
                if (kind != MaterialType) continue;
                seen++;
                var needed = Closure(resources.Found, identifier);
                if (needed == null)
                {
                    skipped++;
                    continue;
                }
                string[] signature = Signature(body);
                string key = string.Join('\n', signature);
                uses[key] = uses.GetValueOrDefault(key) + 1;
                if (distinct.ContainsKey(key)) continue;
                keys.Add(signature);
                string shared = Reference().Replace(body, match => resources.Found.TryGetValue(match.Groups[1].Value, out var target) && IsShared(target.Kind)
                    ? $"SubResource(\"shared_{target.Kind}\")"
                    : match.Value);
                var blocks = needed.SkipLast(1).Select(name => new Block(resources.Found[name].Kind, name, resources.Found[name].Body)).ToList();
                blocks.Add(new Block(kind, identifier, shared));
                distinct[key] = blocks;
            }
        }
        var ordered = keys.Order(Comparer<string[]>.Create((left, right) =>
        {
            string leftKey = string.Join('\n', left), rightKey = string.Join('\n', right);
            int byUse = uses[rightKey].CompareTo(uses[leftKey]);
            return byUse != 0 ? byUse : CompareParts(left, right);
        })).Select(signature => distinct[string.Join('\n', signature)]).ToList();
        return (ordered, seen, skipped);
    }

    private static bool IsShared(string kind) => SharedTextures.Any(texture => texture.Kind == kind);

    private static (List<string> Order, Dictionary<string, (string Kind, string Body)> Found) SubResources(string text)
    {
        var headers = Header().Matches(text);
        var order = new List<string>();
        var found = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        for (int index = 0; index < headers.Count; index++)
        {
            var header = headers[index];
            if (header.Groups[1].Value != "sub_resource") continue;
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match attribute in Attribute().Matches(header.Groups[2].Value)) attributes[attribute.Groups[1].Value] = attribute.Groups[2].Value;
            int start = header.Index + header.Length;
            int end = index + 1 < headers.Count ? headers[index + 1].Index : text.Length;
            string id = attributes["id"];
            if (!found.ContainsKey(id)) order.Add(id);
            found[id] = (attributes["type"], text[start..end].Trim('\n'));
        }
        return (order, found);
    }

    /// <summary>What decides the generated shader: the properties set, and the value of each switch.</summary>
    private static string[] Signature(string body)
    {
        var parts = new List<string>();
        foreach (string line in body.Split(PythonLineBreaks))
        {
            int separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator < 0) continue;
            string name = line[..separator], value = line[(separator + 3)..];
            if (IgnoredProperties.Contains(name)) continue;
            parts.Add(SwitchValue().IsMatch(value) ? name + "=" + value : name);
        }
        parts.Sort(StringComparer.Ordinal);
        return parts.ToArray();
    }

    /// <summary>Sub-resources the material needs, dependencies first. Null if it reaches outside the scene.</summary>
    private static List<string>? Closure(Dictionary<string, (string Kind, string Body)> resources, string root)
    {
        var ordered = new List<string>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string identifier)
        {
            if (ordered.Contains(identifier)) return true;
            if (visiting.Contains(identifier) || !resources.TryGetValue(identifier, out var resource)) return false;
            if (IsShared(resource.Kind)) return true;
            if (resource.Body.Contains("ExtResource(", StringComparison.Ordinal)) return false;
            visiting.Add(identifier);
            foreach (Match reference in Reference().Matches(resource.Body))
                if (!Visit(reference.Groups[1].Value)) return false;
            ordered.Add(identifier);
            return true;
        }
        return Visit(root) ? ordered : null;
    }

    /// <summary>One scene holding these materials, each on a hidden, idle particle node.</summary>
    private static string RenderScene(List<List<Block>> materials)
    {
        var resources = new List<string>(SharedBlocks);
        var nodes = new List<string> { "[node name=\"ShaderWarmupParticles\" type=\"Node2D\"]" };
        for (int index = 0; index < materials.Count; index++)
        {
            string prefix = $"m{index}_";
            foreach (var block in materials[index])
            {
                string renamed = Reference().Replace(block.Body, match => match.Groups[1].Value.StartsWith("shared_", StringComparison.Ordinal)
                    ? match.Value
                    : $"SubResource(\"{prefix}{match.Groups[1].Value}\")");
                resources.Add($"[sub_resource type=\"{block.Kind}\" id=\"{prefix}{block.Id}\"]\n{renamed}".TrimEnd('\n'));
            }
            nodes.Add($"[node name=\"P{index}\" type=\"GPUParticles2D\" parent=\".\"]\nvisible = false\nemitting = false\namount = 1\n"
                + $"process_material = SubResource(\"{prefix}{materials[index][^1].Id}\")");
        }
        return string.Join("\n\n", [$"[gd_scene load_steps={resources.Count + 1} format=3]", .. resources, .. nodes]) + "\n";
    }

    /// <summary>As Python's <c>json.dumps(indent=1)</c> writes it. The names are the game's paths, and any that would need escaping stops here.</summary>
    private static string RenderInventory(int seen, int skipped, int materials, List<string> scenes, List<string> canvasShaders, string sha256)
    {
        static string List(List<string> items)
        {
            foreach (string item in items)
                if (item.Any(character => character is '"' or '\\' or < ' ' or > '~')) throw new PackException("A name needs escaping: " + item);
            return items.Count == 0 ? "[]" : "[\n  " + string.Join(",\n  ", items.Select(item => '"' + item + '"')) + "\n ]";
        }
        return "{\n \"format\": 1,\n"
            + $" \"particleMaterialsSeen\": {seen},\n \"particleMaterialsSkipped\": {skipped},\n \"particleMaterials\": {materials},\n"
            + $" \"particleScenes\": {List(scenes)},\n \"canvasShaders\": {List(canvasShaders)},\n"
            + $" \"particleScenesSha256\": \"{sha256}\"\n}}\n";
    }

    private static int CompareParts(string[] left, string[] right)
    {
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            int part = string.CompareOrdinal(left[index], right[index]);
            if (part != 0) return part;
        }
        return left.Length.CompareTo(right.Length);
    }

    /// <summary>Python sorts paths folder by folder, which is not the order of the whole strings: "a/b" sorts before "a-b".</summary>
    private sealed class PathOrder : IComparer<string>
    {
        public static readonly PathOrder Instance = new();

        public int Compare(string? left, string? right) => CompareParts(left!.Split('/'), right!.Split('/'));
    }
}
