namespace STS2Mobile.GamePatcher;

/// <summary>
/// One patch, as the report of an untested build names it. <paramref name="Required"/> marks the short list the game cannot run
/// without on the phone, or that keeps it inside the network policy (NETWORK.md): Steamworks calls that would crash, saves in the wrong
/// place, and anything sent off the phone or to the LAN. A build one of those does not fit is refused with a plain message. The others are
/// comfort on a phone, and a build they do not fit plays without them.
/// </summary>
/// <param name="Name">What it does, in the player's words. The report and the record use it.</param>
/// <remarks>Each step finds everything it changes before it changes anything, so a step that does not fit leaves the game as it was.</remarks>
internal sealed record PatchStep(string Name, bool Required, Action<GamePatch> Apply);

/// <summary>A required patch does not fit this build, so it cannot be prepared.</summary>
public sealed class GamePatchRefusedException(string patch, string reason)
    : Exception($"\"{patch}\" does not fit this game version: {reason}")
{
    public string Patch { get; } = patch;
    public string Reason { get; } = reason;
}
