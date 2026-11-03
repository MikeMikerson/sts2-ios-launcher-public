namespace STS2Mobile.Steam.Cloud;

public sealed record CloudFileInfo(string Name, long Size, DateTimeOffset Timestamp, string Sha1Hex);
