namespace STS2Mobile.Platform;

/// <summary>A save file to stage: its path below the account's save folder, content, SHA-1 as its source lists it, and time.</summary>
public sealed record StagedSave(string Name, byte[] Content, string Sha1Hex, long UnixTime);
