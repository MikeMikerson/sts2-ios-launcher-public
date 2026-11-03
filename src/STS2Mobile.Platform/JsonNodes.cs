using System.Text.Json.Nodes;

namespace STS2Mobile.Platform;

public static class JsonNodes
{
    public static void AddString(JsonArray array, string value) => array.Add((JsonNode?)JsonValue.Create(value));
    public static void AddObject(JsonArray array, JsonObject value) => array.Add((JsonNode)value);
}
