namespace STS2Mobile.Platform;

/// <summary>
/// Put on a code mod's assembly by the weaver (src/STS2Mobile.GamePatcher) when its Harmony patches were woven into the game
/// assembly at build time. The app checks it before the game's mod loader may use the assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class WovenModAttribute(string modId, string sourceSha256, int patches) : Attribute
{
    public string ModId { get; } = modId;
    public string SourceSha256 { get; } = sourceSha256;
    public int Patches { get; } = patches;
}
