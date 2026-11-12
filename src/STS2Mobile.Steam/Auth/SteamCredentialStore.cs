using System.Text.Json;
using STS2Mobile.Steam.Abstractions;

namespace STS2Mobile.Steam.Auth;

/// <summary>
/// The signed-in account's name and refresh token, encrypted with <see cref="ISecureStorage"/>. Once Steam has confirmed that this sign-in
/// owns the game, the account's Steam ID and Steam name are kept beside them, so the game can start without a connection.
/// </summary>
public sealed class SteamCredentialStore
{
    private const string Tag = "Credentials";
    private readonly string _path;
    private readonly ISecureStorage _secure;
    private readonly ILogger _logger;
    private SteamCredentials? _credentials;

    public SteamCredentialStore(string dataDirectory, ISecureStorage secure, ILogger logger)
    {
        _path = Path.Combine(dataDirectory, "steam_credentials.enc");
        _secure = secure;
        _logger = logger;
    }

    public string? AccountName => _credentials?.AccountName;

    public string? RefreshToken => _credentials?.RefreshToken;

    public bool HasCredentials => _credentials is { AccountName: not null, RefreshToken: not null };

    /// <summary>The account's Steam ID, kept only once Steam has confirmed this sign-in owns the game; null before then.</summary>
    public ulong? SteamId => _credentials?.SteamId is > 0 ? _credentials.SteamId : null;

    /// <summary>The account's Steam name as Steam last sent it, kept with <see cref="SteamId"/>; null if Steam had not sent one.</summary>
    public string? PersonaName => _credentials?.PersonaName;

    /// <summary>A sign-in is stored and Steam has confirmed, with it, that the account owns the game.</summary>
    public bool OwnershipConfirmed => HasCredentials && SteamId != null;

    public void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            string? json = _secure.Decrypt(File.ReadAllText(_path));
            if (json == null)
            {
                // The key is gone (for example after a restore to another device), so the file can never be read again.
                _logger.Warn(Tag, "Stored credentials could not be decrypted; removing them");
                File.Delete(_path);
                return;
            }
            _credentials = JsonSerializer.Deserialize(json, SteamStorageJsonContext.Default.SteamCredentials);
        }
        // The Keychain reports its own failures as InvalidOperationException.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _logger.Warn(Tag, "Stored credentials could not be read", error);
            _credentials = null;
        }
    }

    /// <summary>
    /// Replaces the stored credentials. A new sign-in has not been checked for the game yet, so no Steam ID is kept with it. False when
    /// they could not be encrypted or written.
    /// </summary>
    public bool Save(string accountName, string refreshToken)
    {
        var credentials = new SteamCredentials { AccountName = accountName, RefreshToken = refreshToken };
        if (!Write(credentials)) return false;
        _credentials = credentials;
        return true;
    }

    /// <summary>
    /// Steam has just confirmed that the stored sign-in owns the game: keeps the account's Steam ID and Steam name with it. A name Steam
    /// did not send this time keeps the one stored for the same account. Kept in memory even if the file cannot be written, so this
    /// launch can still play; the next launch then asks Steam again. False when the file was not written.
    /// </summary>
    public bool RecordOwnership(ulong steamId, string? personaName)
    {
        if (_credentials is not { AccountName: not null, RefreshToken: not null } current) throw new InvalidOperationException("No stored sign-in to record ownership for.");
        if (steamId == 0) throw new ArgumentException("Authenticated Steam ID required.", nameof(steamId));
        string? name = !string.IsNullOrWhiteSpace(personaName) ? personaName : current.SteamId == steamId ? current.PersonaName : null;
        if (current.SteamId == steamId && current.PersonaName == name) return true;
        var credentials = new SteamCredentials { AccountName = current.AccountName, RefreshToken = current.RefreshToken, SteamId = steamId, PersonaName = name };
        _credentials = credentials;
        return Write(credentials);
    }

    /// <summary>
    /// Steam said the account does not own the game (for example after a refund): the sign-in stays, but no longer starts the game
    /// without asking Steam. Forgotten in memory even if the file cannot be written.
    /// </summary>
    public bool ForgetOwnership()
    {
        if (_credentials is not { SteamId: not null } current) return true;
        var credentials = new SteamCredentials { AccountName = current.AccountName, RefreshToken = current.RefreshToken };
        _credentials = credentials;
        return Write(credentials);
    }

    private bool Write(SteamCredentials credentials)
    {
        string temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            string? encrypted = _secure.Encrypt(JsonSerializer.Serialize(credentials, SteamStorageJsonContext.Default.SteamCredentials));
            if (encrypted == null)
            {
                _logger.Warn(Tag, "Credentials could not be encrypted");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, encrypted);
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.Warn(Tag, "Credentials could not be saved", error);
            return false;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// Forgets the sign-in: deletes the stored file, then the key it was encrypted with. The sign-in is gone from memory either way. Either
    /// deletion is enough on its own, since a file whose key is gone is never read again (<see cref="Load"/> removes it), so this is false
    /// only when neither could be done and the sign-in would come back at the next launch.
    /// </summary>
    public bool Delete()
    {
        _credentials = null;
        bool fileGone = true, keyGone = true;
        try
        {
            File.Delete(_path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.Warn(Tag, "Stored credentials could not be deleted", error);
            fileGone = false;
        }
        try
        {
            _secure.DeleteKey();
        }
        // The Keychain reports its own failures as InvalidOperationException.
        catch (InvalidOperationException error)
        {
            _logger.Warn(Tag, "The credentials' key could not be deleted", error);
            keyGone = false;
        }
        return fileGone || keyGone;
    }

    /// <summary>
    /// What the file holds. Files written before Steam Guard data was dropped still carry a GuardData field, which reading skips. Files
    /// written before offline play have no Steam ID, so the next Play asks Steam once, as it always did, and keeps it.
    /// </summary>
    internal sealed class SteamCredentials
    {
        public string? AccountName { get; set; }

        public string? RefreshToken { get; set; }

        public ulong? SteamId { get; set; }

        public string? PersonaName { get; set; }
    }
}
