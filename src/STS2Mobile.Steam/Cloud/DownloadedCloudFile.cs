namespace STS2Mobile.Steam.Cloud;

/// <summary>A cloud file as downloaded, already checked against what Steam Cloud lists for it.</summary>
public sealed record DownloadedCloudFile(string Name, byte[] Content, string Sha1Hex, DateTimeOffset Timestamp);
