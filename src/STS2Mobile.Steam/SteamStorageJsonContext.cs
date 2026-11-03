using System.Text.Json.Serialization;
using STS2Mobile.Steam.Auth;

namespace STS2Mobile.Steam;

[JsonSerializable(typeof(SteamCredentialStore.SteamCredentials))]
internal partial class SteamStorageJsonContext : JsonSerializerContext;
