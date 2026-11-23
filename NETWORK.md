# Network boundaries

The running app permits only HTTPS or WSS on port 443, to Steam and Mega Crit, plus an imported mod's relay as described below. Domain boundaries, TLS, port restrictions, redirects, proxy bypass, Host-header checks, and URL override protections are enforced. The allowed hosts:

| What | Allowed hosts | Scheme and port | Rule in the code |
| --- | --- | --- | --- |
| The Steam connection and the launcher's Steam web calls: sign-in, ownership, lobbies, cloud calls, the game download | `steampowered.com`, `steamcommunity.com`, `steamcontent.com`, `steamstatic.com`, `steamserver.net`, and names under them | HTTPS or WSS, 443 | `src/STS2Mobile.Steam/Network/SteamNetworkPolicy.cs` (`IsAllowed`) |
| Steam Cloud's file storage, for Get (GET only) and Send (PUT or POST only) | `steamcloud-<name>.s3.dualstack.<region>.amazonaws.com`, `steamcloud-<name>.storage.googleapis.com`, `steamcloud<name>.blob.core.windows.net` | HTTPS, 443 | `SteamNetworkPolicy.IsCloudStorage` and `src/STS2Mobile.Steam/Network/CloudStorageAccess.cs` |
| The game's own web requests | `megacrit.com`, `steampowered.com`, `steamcommunity.com`, `steamcontent.com`, `steamstatic.com`, and names under them | HTTPS, 443 | `src/STS2Mobile.Platform/GameNetworkPolicy.cs` |
| An imported mod's approved relay | the one host in its config, paths `/v1/relay/` plus `auto`, `apac`, `na` or `eu` | WSS, 443 | `RelayNetworkPolicy` in `src/STS2Mobile.Platform/RelayConnection.cs` |

Every rule refuses IP addresses, user names and passwords in the address, and any other port. The clients that use them follow no redirects and use no proxy, cookies or overridden Host header.

Steam Cloud saves move only when the player asks on the launcher's cloud saves screen and confirms what it shows; nothing syncs on its own. The Steam Cloud calls themselves (listing files, asking for a download, beginning and ending an upload batch, beginning and committing a file upload, deleting a file) go over the signed-in Steam connection. The file contents go to and from Steam Cloud's storage hosts on Amazon S3, Google Cloud Storage, and Azure, at the signed address Steam gives for each file. Only Steam Cloud's own bucket names in each provider's virtual-host form, over HTTPS on port 443, are accepted, and only by two clients:

- The cloud-download client may GET from those hosts, for Get.
- The cloud-upload client may only PUT or POST, to those hosts or to Steam's own hosts, for Send. It refuses every other method, so it cannot read or delete anything.

They are not general app destinations, and both clients keep every other rule above (no redirects, no Host override, no proxy). A Send writes the saves that differ from Steam Cloud's, first keeping Steam Cloud's copies of what it replaces in a backup on the phone. It removes from Steam Cloud only files that the phone no longer has inside a profile the phone has, such as a finished run's `current_run.save`, and never run history. Each upload logs the storage host it went to (host only; the signed path is never logged).

The game's download comes from Steam's content servers, under `steamcontent.com`, through the same policy-checked client. Servers Steam offers outside that policy (other hosts, plain HTTP, proxies) are skipped. It starts only when the player asks.

Imported mods use the launcher's lobby API for discovery, membership, metadata and chat over the signed-in Steam connection. The API does not expose credentials. An imported code mod runs in the app process and must be trusted.

An imported mod can ask the launcher to show a Steam profile. The player sees the exact Steam Community profile URL and chooses Open or Cancel. Open hands that URL to iOS without attaching the launcher's Steam session.

The relay API opens a secure WebSocket to the single host in the calling mod's config after player approval for that mod and host. A different host requires fresh approval. Steam credentials are never attached to the connection. The relay sees the player's IP address, traffic timing and the data the mod chooses to send.

The build-time setup process downloads toolchains and dependencies from the pinned sources in its manifest. These build-time downloads are separate from the app's runtime network policy.

Steam credentials remain in device-local protected storage. The Keychain service is derived from the installed app's bundle identifier. Build tools do not request Steam credentials.
