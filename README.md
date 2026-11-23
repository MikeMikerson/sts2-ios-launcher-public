# STS2 Mobile

Play Slay the Spire 2 on your iPhone. Build this app on your own Mac, or, with no Mac, install the
released IPA from Windows with your own Apple ID. When you open it and sign in to Steam with an account that
owns the game, it downloads the game from Steam and prepares it for the phone.

Neither this repository nor the released app holds any of the game: you still need to own it on Steam. It is
an unofficial fan project, not made or endorsed by Mega Crit or Valve.

## Is it safe?

- **Your password goes only to Steam.** You sign in on Steam's own servers, as the Steam app does. The app
  never stores your password, only the sign-in Steam gives back, encrypted on your iPhone.
- **There is no server of ours.** The app talks only to Steam and Mega Crit, plus a multiplayer relay if you
  approve it for an imported mod. There are no analytics, ads or tracking, and the game's own crash reporting and stats uploads
  are taken out.
- **Nothing happens behind your back.** The game downloads, updates and syncs saves with Steam Cloud only when
  you press the button for it.
- **You can read every line.** The app is built from this code, on your Mac or by this repository's
  GitHub Actions workflow, whose log anyone can read.

The details are in [Security and privacy](#security-and-privacy) below.

## What you need

To build it yourself (with no Mac, see [Install without a Mac](#install-without-a-mac-windows)):

- A Mac with Apple silicon (M1 or later) and about 20 GB free.
- Xcode 27.2 (other versions are refused), opened once, with your Apple ID added in **Settings > Apple
  Accounts**. A free Apple ID works.
- An iPhone and its cable, with about 5.5 GB free during setup and 2.5 GB after. Tested on an iPhone 11 Pro;
  the build allows iOS 15 or later, but other models are untested. An iPad works too, so far checked only on
  the simulator (see [iPad and LiveContainer](#ipad-and-livecontainer)).
- A Steam account that owns Slay the Spire 2. The game does not need to be on the Mac.

## Install

1. Fork this repository on GitHub and clone your fork:

   ```sh
   git clone https://github.com/<you>/sts2-ios-launcher.git
   cd sts2-ios-launcher
   ```

2. Connect the iPhone by cable, unlock it and tap **Trust** if it asks. Turn on **Settings > Privacy &
   Security > Developer Mode** and restart it. (If Developer Mode is missing, open Xcode's **Window >
   Devices and Simulators** with the iPhone connected, then look again.)
3. Run:

   ```sh
   ./setup.sh --install
   ```

   The first run downloads its own tools (about 6 GB, into this folder) and takes about 15 minutes; later
   runs take about 5. If it finds more than one team or iPhone, it lists them: run it again with
   `--team <id>` or `--device <udid>`.
4. With a free Apple ID, trust the app once: **Settings > General > VPN & Device Management**, tap your Apple
   ID, then **Trust**.

## Install without a Mac (Windows)

The app cannot be built on Windows (Apple's tools for iPhone apps run only on a Mac), so this repository's
GitHub Actions workflow builds it on GitHub's Macs and publishes it on the **Releases** page as an IPA file.

**What the IPA is.** An unsigned iPhone app. It holds none of the game and no one's personal information.
An iPhone only installs apps signed by an Apple ID, so a sideloading tool signs it with **your own Apple ID**
on your PC and installs it. A free Apple ID works. Sideloadly, AltStore and SideStore need no account of their
own: they only ask for your Apple ID, which they use with Apple to make the signature, and Apple may ask for
your two-factor code.

**You need:** an iPhone (iOS 15 or later) and its cable, a Windows PC, an Apple ID, and a Steam account that
owns Slay the Spire 2. The phone needs about 5.5 GB free during setup and 2.5 GB after.

### With Sideloadly

1. Download the newest `STS2Mobile-<version>.ipa` from this repository's **Releases** page.
2. On the PC, install iTunes from Apple's website (not the Microsoft Store version), so Windows can talk to
   the iPhone, then install [Sideloadly](https://sideloadly.io).
3. Connect the iPhone by cable, unlock it and tap **Trust**.
4. Open Sideloadly, drop the IPA on it, pick your iPhone, type your Apple ID and press **Start**. Type the
   Apple ID password and, if asked, the two-factor code.
5. On the iPhone, turn on **Settings > Privacy & Security > Developer Mode** and restart. The switch appears
   only once a sideloaded app is installed; if the iPhone already has it on, skip this.
6. Trust your Apple ID once: **Settings > General > VPN & Device Management**, tap your Apple ID, then
   **Trust**.
7. Open **STS2 Mobile** and follow [First launch](#first-launch): sign in to Steam, and the app downloads the
   game from Steam.

**Every 7 days with a free Apple ID** the app stops opening. Do step 4 again with the same IPA and the same
Apple ID: it installs over the old app, so the game, your saves and your sign-in stay. **Do not delete the
app from the iPhone**: that deletes the game and the saves on it. A newer release installs over it the same
way. A free Apple ID can have three sideloaded apps at once.

### With AltStore or SideStore

Install [AltStore](https://altstore.io) (with AltServer on the PC) or [SideStore](https://sidestore.io) by their
own instructions, then add this source in their **Sources** tab:

```text
https://github.com/MikeMikerson/sts2-ios-launcher-public/releases/latest/download/altstore-source.json
```

Install **STS2 Mobile** from the source, then do steps 5 to 7 above. AltStore and SideStore show new releases
and renew the 7 days for you (AltStore while AltServer runs on the same Wi-Fi). They count toward the three
apps.

### Your own build, still without a Mac

Fork this repository, open **Actions > Build IPA > Run workflow** in your fork, and after about 15 to 20
minutes download the IPA from the run's **Artifacts** (a zip with the IPA inside). Pushing a tag such as
`v1.0.0` to your fork also publishes a release with its own AltStore source, at the address above with your
fork's owner and name.

## First launch

Open **STS2 Mobile**. It walks you through signing in to Steam (password and Steam Guard code, or a QR code
scanned with the Steam app), checks that you own the game, downloads it (about 1.2 GB), prepares it (about 2
minutes) and warms up its effects (about 6 minutes, once). Keep the app open meanwhile; if it is interrupted
it carries on where it stopped. When it asks, close the app and open it again.

After that it opens on **Play**, which starts the game without connecting to Steam, so you can play offline:
your ownership was already checked when you signed in and downloaded. Below it, **Game version** updates,
switches or deletes the game, **Cloud saves** gets your saves from Steam Cloud or sends them there, **Steam
account** changes the account or signs out, and **Mods** manages mods. None of them touch your saves without
asking.

## Playing in portrait

The game starts in landscape and plays as it does on a desktop, with a touch as the mouse. To play with the
iPhone upright, open the game's **Settings > Graphics** and turn on **Portrait Mode**; the game turns at once,
also in the middle of a run, and turning it off goes back to landscape. **Set Portrait as Default**, shown
while Portrait Mode is on, starts the game upright the next time.

In portrait every screen is laid out for the narrow screen, with buttons and text sized for a finger:

- Drag a card to play it. Tap or long press a card to read it and its tips.
- Cards, relics and shop items are taken only after a confirm: tap one, then **Choose** (in the shop, tap it
  again to buy), so a stray tap takes nothing.
- A scroll that starts on a card scrolls.

Portrait Mode changes nothing in landscape.

In landscape the game stays inside the screen's safe area by default. **Settings > Graphics > Fill Screen**
uses the full screen after restarting the app; the notch or rounded corners may cover edge controls.
This choice is saved independently of Portrait Mode and Battery Saver and does not change the portrait layout.

## Every 7 days, with a free Apple ID

A free Apple ID's apps stop opening after 7 days. Connect the iPhone and run `./setup.sh --install` again. It
reinstalls over the old app, so the game, your saves and your sign-in stay. **Do not delete the app from the
iPhone**: that deletes the game and the saves on it. Saves on the iPhone reach Steam Cloud only when you press
**Send** under Cloud saves.

## iPad and LiveContainer

The same build installs on an iPad. It has been checked on iPad simulators (iPad Pro 13-inch), not on a real
iPad, so treat iPad support as untested on hardware:

- The launcher is upright and the game turns to landscape, as on an iPhone, and Portrait Mode works. The game
  fills the iPad's squarer screen; some menus leave empty space below them.
- The app takes the whole screen: no Split View, Slide Over or Stage Manager windows.
- On an iPad, Render Resolution 83% and 67% both draw at half the screen's resolution, since the engine needs
  a whole-number scale; 100% draws at full resolution.

The app also runs inside [LiveContainer](https://github.com/LiveContainer/LiveContainer), checked with
LiveContainer built from source on an iPad simulator. Builds before 2026-11-16 hung on a black screen, or took
no touches on an iPad, after the first **Close the app**; install a newer build. Inside LiveContainer, iPadOS
can resize the app, so the app cannot turn the screen itself: hold the iPad sideways before starting the game,
or it shows letterboxed in an upright window. The game, your saves and your sign-in there are kept in
LiveContainer's own folder for the app, apart from an installed copy.

## Mods and multiplayer

- **Mods > Import from Files** takes a mod's zip: a resource pack, a code mod, or both. Resource packs that
  contain scripts are refused. A code mod runs with the same access as the app, including your Steam sign-in,
  just as code mods do on a PC, so the app warns you and asks first. Only add mods you trust.
- **Online co-op** requires an imported multiplayer mod. For [Quick Match](https://github.com/MikeMikerson/sts-mods-quickmatch-workshop/releases), download the iPhone ZIP, import it through **Mods > Import from Files**, and start the game with mods enabled.

## Game versions

Choose a Stable or Beta build from Steam. For a game version without a tested profile, the app downloads its code first and checks that the launcher can run it. If the check fails, your installed game stays in place. Updates start only when you choose to download them.

## Troubleshooting

`./setup.sh --install` stops with a message that says what to do, and keeps its logs in `.work/logs/`.

- **"No Apple ID is signed in to Xcode"**: add it in Xcode's **Settings > Apple Accounts**.
- **"Developer Mode is off"**, **"is not paired"**, **"No iPhone is connected"**: see step 2, and keep the
  iPhone unlocked and connected for the whole build.
- **A signing error** (in `.work/logs/mono-xcode.log`): open Xcode's **Settings > Apple Accounts**, wait for
  your team to load, and run the command again. A free Apple ID allows 10 new app ids a week, so keep
  `.local/device.json` rather than making new ones.
- **The app limit**: a free Apple ID can have 3 of its apps on the iPhone. Delete one of the others.
- **"Untrusted Developer"**: see step 4. **"Unable to verify app"**: connect the iPhone to the internet and
  trust it again.
- **"This checkout is validated with Xcode 27.2"**: install that version, or point to it with
  `DEVELOPER_DIR=/path/to/Xcode.app/Contents/Developer ./setup.sh --install`.
- **A tool download fails**: run the command again.
- **A game version fails its compatibility check**: choose the Beta if Steam has one, or wait for an update of this
  app. Nothing is downloaded or deleted.
- **Steam Guard**: use the code from the Steam app or Steam's email. The QR sign-in needs the Steam app on
  another device.

## Security and privacy

This section is for anyone who wants to check the claims above. Each point names where it lives in the code.

### Your Steam sign-in

- The app signs in with SteamKit2, an open-source Steam client library, directly to Steam's servers. Your
  password goes only there and is never written to disk. The QR sign-in sends no password at all.
- The sign-in is kept on this iPhone: only your account name and the refresh token Steam issues (no
  password, no Steam Guard data), and, once Steam has confirmed that the account owns the game, its Steam ID
  and Steam name, in one file encrypted with **AES-256-GCM**
  (`src/STS2Mobile.FullGame/KeychainSecureStorage.cs`). The key is random, 256 bits, and kept only in the
  **iOS Keychain** (`src/STS2Mobile.Platform/AppleKeychain.cs`), readable only while the iPhone is unlocked
  and only on this device (`kSecAttrAccessibleWhenUnlockedThisDeviceOnly`). It is never in iCloud or device
  backups and does not move to another phone; without it the file is useless, and the app asks you to sign
  in again.
- Steam checks that the account owns the game when you sign in and before every download, update or switch
  of version (`src/STS2Mobile.Steam/Auth/OwnershipVerifier.cs`). Play does not ask again: the game could only
  be downloaded after that check, so Play and the game's own start check use the stored sign-in, its Steam ID
  and Steam name, and need no connection (`src/STS2Mobile.Launcher/SteamLoginSession.cs`). A sign-in stored
  by an older version of the app has no Steam ID yet, so its next Play checks with Steam once. Signing out
  ends this, and so does a later check that finds the account no longer owns the game. Offline, Cloud saves
  and Game version say that Steam could not be reached, and mods cannot find or host Steam lobbies. The one
  wait is the first Play on an iPhone with no saves yet, which still offers your Steam Cloud saves: offline,
  that gives up after about 15 seconds and the game starts.
- **Steam account > Sign out** signs out of Steam on this iPhone: it deletes the stored sign-in and its key,
  and asks Steam to end the sign-in. The game and your saves stay, and you sign in again to play. If Steam
  cannot be reached it still signs out on the phone, and you can remove the sign-in under **Authorized
  Devices** in your Steam account settings.
- The app's files are not shared with the Files app or a computer (file sharing is off).

### What the app connects to

Every connection goes through a policy in the code that allows only HTTPS or WSS on port 443 to named hosts,
and refuses IP addresses, redirects, proxies, cookies and overridden Host headers
(`src/STS2Mobile.Steam/Network`, `src/STS2Mobile.Platform/GameNetworkPolicy.cs`). The allowed destinations:

- **Steam**: sign-in, the ownership check, the game download (from `steamcontent.com`), Steam Cloud and lobbies.
- **Steam Cloud's storage** (on Amazon, Google and Microsoft clouds, at the address Steam gives for each file):
  only when you press Get or Send. The client that uploads to it can only write, never read or delete. A
  Send may still remove a cloud file the iPhone no longer has (such as a finished run's save), through Steam's
  own cloud API over the signed-in connection, never through the storage hosts.
- **Mega Crit's and Steam's websites**, for the game's own web requests.
- **A mod relay**, after you approve the host named by the imported mod.

Nothing else. [NETWORK.md](NETWORK.md) lists each rule and the allowed hosts.

### What was removed from the game

When the app prepares the game on your iPhone, it patches the game's code
(`src/STS2Mobile.GamePatcher`):

- Sentry crash reporting is removed completely.
- Run, achievement and settings stats, and the feedback upload (logs and a screenshot), are removed: nothing
  is uploaded to anyone.
- LAN play's raw network sockets are refused.

The app's own log (`Documents/full-game-startup.log`) stays on the iPhone and is never sent anywhere.

### Multiplayer

- Mods can find, host and join lobbies and exchange lobby chat through your signed-in Steam account.
- Before a mod connects to its configured relay, the launcher asks you to approve the mod and host. A change of host needs fresh approval. The relay sees your IP address and the data the mod sends.
- A mod can request microphone access for voice chat. iOS manages the microphone permission for the app.

### Files and mods

- **Import from Files** sees only the one zip you choose, as a copy. Paths that leave the mod's folder, links
  and oversized files are refused (`src/STS2Mobile.Platform/ModPackageImport.cs`).
- Code mods are trusted code, as on a PC: they are checked for some dangerous calls, but that is not a
  sandbox. The app warns before adding one.

### The build

- Every toolchain and archive setup downloads is checked against a hash pinned in `tools/dependencies.json`
  before use: SHA-512 for Godot, its export templates, .NET and the LibraryBuilder pack, SHA-256 for the rest.
  `setup.sh` pins uv, which installs Python, by SHA-256.
- Python packages install only as the wheels whose SHA-256 `tools/requirements.txt` pins
  (`--require-hashes`, no source builds). Every NuGet package, direct or transitive, is locked to its content
  hash in the `packages*.lock.json` next to each project, and a restore that would differ stops the build.
- The .NET iOS workload comes from Microsoft's signed NuGet feed through `dotnet workload install`, at the
  workload set version pinned in `tools/dependencies.json`.
- The repository and the built app hold no file from the game. The build ends by scanning the app for game
  files (`tools/scan_no_game_files.py`).

## For developers

- [BUILDING.md](BUILDING.md): the build pipeline, the checks, the tests and the test switches.
- [NETWORK.md](NETWORK.md): every network rule, in full.
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md): the components the app is built from, and their licenses.
- [LICENSE](LICENSE): MIT, for this repository's own code. Third-party parts keep their own licenses.
