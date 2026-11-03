namespace STS2Mobile.Platform;

/// <summary>What a verified pending import holds, for the player's choice.</summary>
public sealed record PendingSaveImport(string Directory, string Account, int Files, int Profiles, int FinishedRuns, DateTime? LastChangedUtc);
