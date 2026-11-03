namespace STS2Mobile.Platform;

/// <summary>One built-in code mod, as listed in the catalog preparation writes beside the game's assembly (user://game/builtin-mods.json, read by GameEntry).</summary>
public sealed record BuiltInMod(string Id, string Manifest, string SourceSha256, string WovenSha256, int Patches, string? PckSha256 = null);
