using System.Text;
using STS2Mobile.Launcher;
using STS2Mobile.Steam.Abstractions;
using STS2Mobile.Steam.Auth;

namespace STS2Mobile.Steam.Tests;

// The stored sign-in: what the file keeps, and signing out, which asks Steam to end the sign-in and forgets it here whatever Steam says.
public sealed class SignOutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sign-out-").FullName;
    private readonly FakeKeychain _keychain = new();
    private string CredentialsFile => Path.Combine(_root, "steam_credentials.enc");

    public void Dispose() => Directory.Delete(_root, true);

    private SteamCredentialStore Store()
    {
        var store = new SteamCredentialStore(_root, _keychain, new QuietLogger());
        store.Load();
        return store;
    }

    [Fact]
    public void OnlyTheAccountAndTokenAreStored()
    {
        Assert.True(Store().Save("player_one", "refresh-token"));
        string json = _keychain.Decrypt(File.ReadAllText(CredentialsFile))!;
        Assert.DoesNotContain("Guard", json);
        var reread = Store();
        Assert.Equal("player_one", reread.AccountName);
        Assert.Equal("refresh-token", reread.RefreshToken);
    }

    [Fact]
    public void AFileThatStillHasSteamGuardDataIsRead()
    {
        File.WriteAllText(CredentialsFile, _keychain.Encrypt("{\"AccountName\":\"player_one\",\"RefreshToken\":\"refresh-token\",\"GuardData\":\"old\"}"));
        var store = Store();
        Assert.True(store.HasCredentials);
        Assert.Equal("refresh-token", store.RefreshToken);
    }

    [Fact]
    public void DeleteRemovesTheFileAndTheKey()
    {
        var store = Store();
        store.Save("player_one", "refresh-token");
        Assert.True(store.Delete());
        Assert.False(store.HasCredentials);
        Assert.False(File.Exists(CredentialsFile));
        Assert.False(_keychain.HasKey);
        Assert.False(Store().HasCredentials);
    }

    [Fact]
    public void AKeychainFailureStillLeavesNoSignIn()
    {
        var store = Store();
        store.Save("player_one", "refresh-token");
        _keychain.FailDelete = true;
        Assert.True(store.Delete());
        Assert.False(File.Exists(CredentialsFile));
        Assert.False(Store().HasCredentials);
    }

    [Fact]
    public void AFileWhoseKeyIsGoneIsRemovedOnLoad()
    {
        Store().Save("player_one", "refresh-token");
        _keychain.DeleteKey();
        Assert.False(Store().HasCredentials);
        Assert.False(File.Exists(CredentialsFile));
    }

    [Fact]
    public async Task SignOutRevokesTheStoredTokenThenForgetsIt()
    {
        Store().Save("player_one", "refresh-token");
        var revoked = new List<(string, string)>();
        using var session = new SteamLoginSession(_root, _keychain, new QuietLogger(), (account, token, _) => { revoked.Add((account, token)); return Task.CompletedTask; });
        Assert.True(session.HasStoredCredentials);
        var result = await session.SignOutAsync(CancellationToken.None);
        Assert.Equal(new SignOutResult(Revoked: true, Forgotten: true), result);
        Assert.Equal([("player_one", "refresh-token")], revoked);
        Assert.False(session.HasStoredCredentials);
        Assert.False(File.Exists(CredentialsFile));
        Assert.False(_keychain.HasKey);
    }

    [Fact]
    public async Task ASteamFailureDoesNotKeepTheSignIn()
    {
        Store().Save("player_one", "refresh-token");
        using var session = new SteamLoginSession(_root, _keychain, new QuietLogger(), (_, _, _) => throw new TimeoutException("Steam connection timed out"));
        var result = await session.SignOutAsync(CancellationToken.None);
        Assert.Equal(new SignOutResult(Revoked: false, Forgotten: true), result);
        Assert.False(session.HasStoredCredentials);
        Assert.False(Store().HasCredentials);
    }

    [Fact]
    public async Task GivingUpOnSteamStillSignsOutHere()
    {
        Store().Save("player_one", "refresh-token");
        using var session = new SteamLoginSession(_root, _keychain, new QuietLogger(), (_, _, wait) => Task.Delay(Timeout.Infinite, wait));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await session.SignOutAsync(stop.Token);
        Assert.Equal(new SignOutResult(Revoked: false, Forgotten: true), result);
        Assert.False(session.HasStoredCredentials);
    }

    [Fact]
    public void TheNoticeSaysWhetherSteamWasTold()
    {
        Assert.True(SignOut.Result(new(Revoked: true, Forgotten: true)).Succeeded);
        var local = SignOut.Result(new(Revoked: false, Forgotten: true));
        Assert.False(local.Succeeded);
        Assert.Contains("could not be told", local.Text);
        Assert.Contains("Authorized Devices", local.Text);
        Assert.Contains("may come back", SignOut.Result(new(Revoked: false, Forgotten: false)).Text);
        foreach (bool revoked in new[] { true, false })
            foreach (bool forgotten in new[] { true, false })
                Assert.Contains("saves are still on this iPhone", SignOut.Result(new(revoked, forgotten)).Text);
    }

    /// <summary>Stands in for the Keychain-backed storage: reversible "encryption" that stops working once the key is deleted.</summary>
    private sealed class FakeKeychain : ISecureStorage
    {
        public bool HasKey { get; private set; }
        public bool FailDelete { get; set; }

        public string? Encrypt(string plaintext)
        {
            HasKey = true;
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        }

        public string? Decrypt(string ciphertext) => HasKey ? Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext)) : null;

        public void DeleteKey()
        {
            if (FailDelete) throw new InvalidOperationException("Keychain delete failed with status -1");
            HasKey = false;
        }
    }

    private sealed class QuietLogger : ILogger
    {
        public void Info(string tag, string message, Exception? exception = null) { }
        public void Warn(string tag, string message, Exception? exception = null) { }
        public void Error(string tag, string message, Exception? exception = null) { }
    }
}
