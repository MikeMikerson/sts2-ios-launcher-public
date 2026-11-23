namespace STS2Mobile.Platform;

public sealed record ModImportResult(ModImportStatus Status, string? Id, string Message, string? RelayHost = null);

internal sealed record RelayDecision(string ModId, string Host, bool Allow);
