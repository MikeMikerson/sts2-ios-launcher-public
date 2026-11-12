namespace STS2Mobile.Platform.Tests;

// RuntimeServices binds once until unbound, so each fact starts unbound. Facts in one class do not run at the same time.
public class RuntimeServicesTests
{
    public RuntimeServicesTests() => RuntimeServices.Unbind();

    [Fact]
    public void AnAttemptThatFailedBindsAgainWithAnotherAccount()
    {
        RuntimeServices.BindOwnershipVerifier(() => Task.FromResult(true));
        RuntimeServices.BindAccount(76561198000000001, "first-account");
        Assert.Throws<InvalidOperationException>(() => RuntimeServices.BindOwnershipVerifier(() => Task.FromResult(true)));
        Assert.Throws<InvalidOperationException>(() => RuntimeServices.BindAccount(76561198000000002, "second-account"));

        RuntimeServices.Unbind();
        Assert.Throws<InvalidOperationException>(() => RuntimeServices.GetLocalPlayerId());
        RuntimeServices.BindOwnershipVerifier(() => Task.FromResult(true));
        RuntimeServices.BindAccount(76561198000000002, "second-account");

        Assert.Equal(76561198000000002ul, RuntimeServices.GetLocalPlayerId());
        Assert.Equal("second-account", RuntimeServices.GetPlayerName(76561198000000002));
    }

    [Fact]
    public async Task OwnershipMustBeVerified()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(RuntimeServices.RequireOwnershipAsync);
        RuntimeServices.BindOwnershipVerifier(() => Task.FromResult(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(RuntimeServices.RequireOwnershipAsync);
    }

    [Fact]
    public async Task AnAnswerFromTheStoredSignInPassesWithoutWaiting()
    {
        // What the launcher binds: the stored sign-in's confirmed ownership, with no connection, so the game also starts offline.
        RuntimeServices.BindOwnershipVerifier(() => Task.FromResult(true));
        RuntimeServices.BindAccount(76561198000000001, "Stored Name");
        Task<bool> check = RuntimeServices.RequireOwnershipAsync();
        Assert.True(check.IsCompletedSuccessfully);
        Assert.True(await check);
        Assert.Equal(76561198000000001ul, RuntimeServices.GetLocalPlayerId());
        Assert.Equal("Stored Name", RuntimeServices.GetPlayerName(76561198000000001));
    }

    [Fact]
    public void PlayerNamesComeFromTheBoundAccountOnly()
    {
        Assert.Throws<InvalidOperationException>(() => RuntimeServices.GetLocalPlayerId());
        Assert.Throws<InvalidOperationException>(() => RuntimeServices.GetPlayerName(76561198000000002));

        RuntimeServices.BindAccount(76561198000000001, "owner-account");

        Assert.Equal("owner-account", RuntimeServices.GetPlayerName(76561198000000001));
        Assert.Equal("[unknown]", RuntimeServices.GetPlayerName(76561198000000002));
    }

    [Fact]
    public void OtherPlayersInALobbyGetTheirSteamNames()
    {
        RuntimeServices.BindAccount(76561198000000001, "owner-account");
        RuntimeServices.BindLobbies(new NamedLobbies());
        Assert.Throws<InvalidOperationException>(() => RuntimeServices.BindLobbies(new NamedLobbies()));
        Assert.Equal("owner-account", RuntimeServices.GetPlayerName(76561198000000001));
        Assert.Equal("Friend Name", RuntimeServices.GetPlayerName(76561198000000002));
        Assert.Equal("[unknown]", RuntimeServices.GetPlayerName(76561198000000003));

        RuntimeServices.Unbind();
        Assert.Null(RuntimeServices.Lobbies);
    }

    private sealed class NamedLobbies : IPhoneLobbies
    {
        public ulong LocalId => 76561198000000001;
        public event Action<LobbyUpdate>? Changed { add { } remove { } }
        public Task<ulong?> CreateAsync(int type, int maxMembers, CancellationToken ct) => Task.FromResult<ulong?>(null);
        public Task<LobbyEntry> JoinAsync(ulong lobby, CancellationToken ct) => Task.FromResult(LobbyEntry.Error);
        public void Leave(ulong lobby) { }
        public Task<IReadOnlyList<FoundLobby>?> SearchAsync(IReadOnlyDictionary<string, string> equal, int minFreeSlots, int maxResults, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FoundLobby>?>(null);
        public void SetData(ulong lobby, string key, string value) { }
        public void DeleteData(ulong lobby, string key) { }
        public string GetData(ulong lobby, string key) => "";
        public bool SetType(ulong lobby, int type) => false;
        public void SetJoinable(ulong lobby, bool joinable) { }
        public ulong Owner(ulong lobby) => 0;
        public IReadOnlyList<ulong> Members(ulong lobby) => [];
        public bool SendChat(ulong lobby, byte[] data, int length, ulong target) => false;
        public string? MemberName(ulong member) => member == 76561198000000002 ? "Friend Name" : null;
    }
}
