using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Assets;

namespace STS2Mobile.GameAdapters;

public static class AtlasJson
{
    public static TpSheetData? Deserialize(string json, JsonSerializerOptions unusedOriginalOptions)
        => JsonSerializer.Deserialize(json, AtlasJsonContext.Default.TpSheetData);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TpSheetData))]
internal partial class AtlasJsonContext : JsonSerializerContext;
