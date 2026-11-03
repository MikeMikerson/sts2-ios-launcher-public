namespace STS2Mobile.Platform;

/// <summary>Checks every request the game makes against <see cref="GameNetworkPolicy"/> (<see cref="PolicyHandler"/>).</summary>
public sealed class GameOnlyHandler : PolicyHandler
{
    public GameOnlyHandler() : this(CreateTransport()) { }

    internal GameOnlyHandler(HttpMessageHandler transport) : base("Game", transport) { }

    private protected override void RequireAllowed(Uri? uri) => GameNetworkPolicy.RequireAllowed(uri);
}
