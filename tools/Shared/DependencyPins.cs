using System.Text.Json.Nodes;

namespace STS2Mobile.Tools;

public static class DependencyPins
{
    private static readonly JsonNode Root = Load();

    public static string GameAssemblySha256 => Root["game"]!["assembly_sha256"]!.GetValue<string>();

    public static IReadOnlySet<string> SteamKit2Sha256 =>
        Root["steamkit2"]!["sha256"]!.AsObject().Select(pin => pin.Value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

    private static JsonNode Load()
    {
        using var stream = typeof(DependencyPins).Assembly.GetManifestResourceStream("dependencies.json")
            ?? throw new InvalidOperationException("dependencies.json is not embedded.");
        return JsonNode.Parse(stream)!;
    }
}
