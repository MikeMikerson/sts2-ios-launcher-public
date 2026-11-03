namespace STS2Mobile.Platform;

/// <summary>What the launcher asks of the game-facing assembly, which it loads by name when the game is present.</summary>
public interface IGameEntry
{
    /// <summary>True once the game node exists. After that, a failed start cannot be retried in this process.</summary>
    bool GameCreated { get; }

    /// <summary>Starts the game and finishes when it has reached its main menu.</summary>
    Task StartAsync();

    /// <summary>Writes what the game holds in memory. Called when iOS is about to suspend the app.</summary>
    void FlushSaves();
}
