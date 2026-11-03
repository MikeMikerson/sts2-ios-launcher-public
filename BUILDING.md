# Build pipeline

The build uses Godot 4.5.1 .NET, .NET SDK 10.0.401, a .NET 9 runtime for tools, and Xcode 27.1. Setup provisions local tools and verifies dependency archives before extracting them. The dependency manifest specifies the exact sources and checksums.

The app's build reads no game file, and needs no Steam install. The game's assemblies are replaced by `STS2Mobile.GameStub`, which holds names only, and the game's files reach the app's data folder on the phone.

`./setup.sh` runs all of it, unsigned, and ends with `analysis/full-game-mono/FullGame.ipa`:

1. Provision the toolchain, FMOD and Spine.
2. Generate static Steam serializers and stage the patched SteamKit2.
3. Prepare the host project and a placeholder icon.
4. Export the Godot host: the Xcode project and the main pack, with an empty stub in place of the app's library.
5. Prepare native lifecycle and presentation fixes in the generated Xcode project.
6. Build the Mono app: `scripts/build-mono.sh --unsigned`.

`./setup.sh --prepare-only` stops before step 6, for a signed build next. `./setup.sh --install` replaces step 6 with a signed build and installs it on the connected iPhone (see "Signing and installing"). The Xcode scheme uses the Release configuration and physical-device libraries. It cannot build for a simulator. `scripts/simulator.sh` makes a simulator app from its output, for testing (`tools/mono/simulator_app.py`).

## The app build

`scripts/build-mono.sh` builds the app on Mono from a tree where `./setup.sh --prepare-only` has run, in about 3.5 minutes, most of it (about 2.5 minutes) the publish's AOT compile. It ends by running its check and scanning the IPA. Step logs are in `.work/logs/mono-*.log`, and the output is `analysis/full-game-mono/FullGame.app` and `FullGame.ipa`.

- It signs with `team` and `dev_bundle_id` from `.local/device.json`. `--unsigned` builds without signing. `--reuse-publish` repeats only the steps after the publish.
- It has its own copy of the sources and its own package folder in `analysis/full-game-mono`, and it reuses the export's Xcode project through APFS clones.
- `tools/scan_no_game_files.py` stops the build if the IPA holds a game file. When `analysis/game-files` exists, it also compares the IPA with those files.
- The app holds no game. On the first run it signs in to Steam, checks that the account owns the game, and downloads it into its data folder. From then on it runs the game from there: a mounted pack, a settings override and a Mono preload hook (`tools/mono/game_assembly_hook.c`).
- The game's code runs in Mono's interpreter, which needs eleven workarounds (listed under "Mono workarounds" below). The steps live in `tools/mono/`:
  - `MonoBuildTool`: the GodotSharp rewrites (workarounds 3, 7, 10), the P/Invoke listing and the check of game references.
  - `mono_library.py`: the source edits and binary patches (startup mode, workarounds 4, 6, 8, 11).
  - `pinvoke_table.py`: the P/Invoke override (workaround 9).
  - `check_mono_build.py`: confirms all eleven in the built files, that the library is stripped, and that everything the app and the game refer to is in the publish. To run it alone: `tools/python/bin/python3 tools/mono/check_mono_build.py`.
  - The host project sets workarounds 2 and 5, and `build_mono.py` sets workaround 1 and links the C files of workarounds 6 and 9.
- The app's library is stripped, and `analysis/full-game-mono/STS2Mobile.FullGame` keeps the symbols: read native crash reports against that copy. `REMOVED_ASSEMBLIES` in `check_mono_build.py` lists the .NET assemblies left out of the publish. When adding a patch profile for a new game version, rebuild with that game in `analysis/game-files`, so the check covers what it refers to. Names built in strings at run time are out of the check's sight, so also search the game for removed names.
- Every step is pinned in the `mono` section of `tools/dependencies.json`: the LibraryBuilder pack, `AppleBuild.targets` before and after its edit, `GodotSharp.dll` before and after its rewrite, and `libmonosgen-2.0.a`, the source of the code the binary patches change. A mismatch stops the build. The linked library has no pinned hash of its own, because it embeds absolute build paths.

## Mono workarounds

The game's code, and the app's code that uses it, runs in Mono's interpreter (Mono 10.0.12, full AOT plus interpreter), while the rest is AOT compiled. Mono needs these eleven workarounds for that, numbered as in the code's comments and in `CHECKS` in `tools/mono/check_mono_build.py`, which confirms each in the built app. Besides them, setup edits the LibraryBuilder pack's `AppleBuild.targets` to the `FullInterp` AOT mode (`tools/setup_ios.py`), and `edit_autoinit` in `mono_library.py` makes startup call `mono_jit_set_aot_mode (MONO_AOT_MODE_INTERP)` in place of `mono_jit_set_aot_only (true)`.

1. The AOT compiler asserts in its dedup step. The publish sets `iOSLikeDedup=false` (`build_mono.py`).
2. The app's assemblies that use the game, `STS2Mobile.GameHost` and `STS2Mobile.GameAdapters`, cannot be AOT compiled: the game's assemblies are absent at build time and change with every game build, and an AOT module whose dependency is missing or changed is unusable. The host project's `InterpretGameAssemblies` target has them interpreted, and the check also confirms the library exports Godot's entry point `godotsharp_game_main_init` exactly once.
3. The AOT compiler asserts on the custom modifier in the function pointer type of `Godot.Callable._trampoline`. The modifiers are stripped from GodotSharp (`MonoBuildTool`, `GodotSharpRewrites.cs`).
4. Mono's `key_from_id` calls `strcmp(NULL, ".dll")` for an assembly name without a dot, which crashes at startup. A one-instruction patch of the linked library (`patch_key_from_id` in `mono_library.py`).
5. In library mode CoreLib's globalization calls throw `DllNotFoundException` at the first culture lookup. The host project adds `libSystem.Globalization.Native` to `DirectPInvokes`.
6. Mono runs a class's static constructor whenever reflection invokes one of its methods, even for a BeforeFieldInit class, where CoreCLR waits for the first static field access, and the game depends on that timing. The eager class-init calls in `mono_jit_runtime_invoke` and `mono_interp_transform_method` are pointed at a shim (`invoke_class_init.c`, patched in by `patch_class_init` in `mono_library.py`).
7. GodotSharp's generic `Callable.From` hands an interpreter pointer to AOT code as a trampoline, which crashes. The 19 generic `Callable.From` methods are rewritten to pass one AOT trampoline (`GodotSharpRewrites.cs`).
8. An assembly embedded at an unaligned address silently loses every try/catch/finally in its interpreted methods. The embedded arrays are aligned to 16 bytes before the link (`align_bundled_resource` in `mono_library.py`). The game's own assembly is mapped from its file by `game_assembly_hook.c`, so it is page aligned.
9. Interpreted code that makes a P/Invoke itself, for example after tier-up inlines `Stopwatch.GetTimestamp`, cannot find the .NET native libraries, which are linked in statically. A P/Invoke override table is installed when the library loads (`pinvoke_table.py`).
10. Once the interpreter tiers a hot method up it inlines small AOT callees, and an inlined `Callable.From` handled a function pointer inside the interpreter, so Godot later jumped to a non-code address. Every GodotSharp method that creates, takes or calls a function pointer is marked NoInlining (`GodotSharpRewrites.cs`).
11. In library mode nothing starts Mono's finalizer thread, so no finalizer runs, every Godot object a managed wrapper holds stays, and iOS ends the app at its memory limit after about nine minutes of play. Startup starts it (`edit_autoinit` in `mono_library.py`).

## Signing and installing

`./setup.sh --install` is the path in `README.md`. Before any download it fills in `.local/device.json` (gitignored) and checks the iPhone, then runs the setup, the signed build and the install. `tools/install_app.py` does the signing values and the install:

- `team`: one of the teams signed in to Xcode, read from Xcode's preferences. With more than one, `--team` picks it.
- `dev_bundle_id`: the same random identifier as `.local/signing.json`, so it is free on any team. Changing it installs a second app with an empty container.
- `device_udid`: the connected iPhone, from `devicectl`. With more than one, `--device` picks it. The build names it as its destination with `-allowProvisioningDeviceRegistration`, so Xcode registers it with the team; a free Apple ID's team has no profile without a registered device.

Values already in the file are kept, and other keys in it are left alone. `tools/python/bin/python3 tools/install_app.py` installs the last signed build again, and `--configure-only` only writes the file. `devicectl device install app` over an installed app with the same bundle id and team keeps its container (`Documents`, saves, the downloaded game) and its Keychain items (the Steam sign-in). That is how a free Apple ID's app is renewed after 7 days.

## Game files on the Mac

The app downloads the game itself. `scripts/prepare-game-files.sh` makes the same files on a Mac from the Steam install of the supported version, for the tests on the real game and for copying onto a test install. It reads the installed game and writes only into generated folders:

1. Verify the supported game assembly and asset-pack hashes.
2. Extract and audit resources, remove Sentry, convert phone textures to ASTC, limit particle pre-simulation, and prepare the shader inventory.
3. Build the platform boundary and the adapters, patch the game assembly, check the code mods listed in `mods/builtin.json` (QuickMatch, when setup fetched it) against their pinned hashes, and weave their patches in.
4. Check every game reference of the game-facing assemblies against the patched game.
5. Write `analysis/game-files`, which goes into the app's `Documents/game`: the game's own assemblies and pack. The app makes the assembly, the converted textures and the pack it runs from those.
6. Write `analysis/full-game-reference/FullGame.pck`, the Python tools' game pack, which stays on the Mac for `tests/GamePack.Tests` to compare with.

Under a minute when the extracted stages are reused. `--check` only verifies the installed game's version.

## Changing the app

- After changing anything under `src/`, `tools/` (apart from `tools/mono/`) or `scripts/`, run `./setup.sh --prepare-only` (about 35 s once the tools are there) before `scripts/build-mono.sh`, or the build stops at its inputs check. Then `./setup.sh --install`, or `scripts/build-mono.sh` and `tools/python/bin/python3 tools/install_app.py`.
- `scripts/test.sh`: the Python and .NET tests and the Steam wire checks, about 30 s, after `./setup.sh --prepare-only`. Without a Steam install, the tests on the real game and on Steam's cached manifest report as skipped. The game pack tests also need `scripts/prepare-game-files.sh`.
- `scripts/simulator.sh` makes a simulator app from the device build and installs it on a simulator named "iPhone 18 Pro", which must exist; `scripts/simulator-run.sh` launches it on that simulator and presses the launcher's buttons for you: `scripts/simulator-run.sh <name> <labels|to|press> <regex> <timeout> [ENV=VALUE ...]`, with its log in `.work/simlogs`. It finds the simulator and the app's bundle id itself (`tools/mono/simulator_app.py --print-target`). The switches are listed below. The simulator shares the phone's code, not its speed, memory or heat.
- `tools/python/bin/python3 tools/mono/analyze_session.py <log>` reads a play session from the game's log (`Documents/full-game-startup.log` in the app's container), from its `[stats]` samples (older logs tag them `[proof]`): frame rate, memory, heat and battery.
- `tools/python/bin/python3 tools/scan_no_game_files.py analysis/full-game-mono/FullGame.ipa`: the scan for game files. The build already runs it.
- `git status --porcelain -uall` must never show a game file (`.dll`, `.pck`, extracted resources) as untracked or staged. They belong only in the gitignored folders.

### Test switches

Each is an environment variable, passed to `scripts/simulator-run.sh` as `ENV=VALUE` (or to `xcrun devicectl device process launch --environment-variables` on a phone). They only change what the app does when set.

- `STS2_AUTO_TAP=<label|label|...>`: the launcher presses those buttons, in order, as they appear. Each screen is logged as `Screen [step]: text | buttons`. A fresh install asks about Steam Cloud saves before Play: pass `Keep the saves on this iPhone` to leave them alone.
- `STS2_SCREEN=<name>`: opens one launcher screen with made-up content and nothing else, for a screenshot (the names are in `LauncherPreview.cs`; `account-signout` shows the sign-out confirmation).
- `STS2_FREE_BYTES=<n>` stands in for the free space; `STS2_START_FAILURE=start|unsupported` and `STS2_PREPARE_FAILURE=1` stand in for those failures.
- `STS2_CLOUD_SEND_DRY_RUN=1`: a Send does everything but the upload calls and logs what it would send. A Send whose confirmation `STS2_AUTO_TAP` presses is refused unless `STS2_CLOUD_SEND_REAL=1` is also set.
- `STS2_AUTOSLAY=<seed>`: the game's own automatic player plays a whole new run and returns to the menu, logging `[AutoSlay] [MemProfile]` lines per room. Pass `STS2_AUTO_TAP=Play` too. It replaces the saved run and changes unlocks, so copy `Documents/steam` first and restore it after.
- `STS2_OPEN_MULTIPLAYER=1` opens the game's multiplayer menu once the main menu is up and logs QuickMatch's buttons; `STS2_OPEN_MULTIPLAYER=quickmatch` also presses Quick Match and a party of two, so QuickMatch searches Steam's lobbies and hosts one.
- `STS2_RELAY_DROP_AFTER=<seconds>` drops each relay connection that long after it opens; `STS2_STEAM_DROP_AFTER=<seconds>` closes the Steam connection that long after each lobby is entered or created.
- `STS2_VOICE=off` leaves QuickMatch with no voice device. `STS2_CHECK_VOICE=1` runs the voice audio once at startup (grant the microphone first with `xcrun simctl privacy <udid> grant microphone <bundle id>`).
- `STS2_CHECK_RELAY_RUNTIME=ws://127.0.0.1:<port>/...` checks AES-GCM, HKDF, HMAC and one WebSocket echo under the app's Mono at startup (needs an echo server on the Mac).
- `STS2_TEXTURE_OPTIONS=<workers,large threads,batch MB,large MB>`: four positive whole numbers that tune the texture conversion during preparation, for measuring it without a rebuild. Anything else leaves the defaults.
- `STS2_MODS=1` or `0` turns the launcher's mods switch on or off for that launch only; the saved choice is not changed.
- `STS2_IMPORT_MOD=<name>.zip` imports that zip from the app's `Documents` during preparation, through the same importer as **Import from Files**. It skips the question a code mod asks, so it is for tests only.
- `--headless` as a launch argument runs preparation with no renderer.

One switch is for `scripts/test.sh`, not the app: `STS2_TEST_MOD_ZIP=<path to a mod zip>` makes an installed-game test in `tests/GamePatcher.Tests` import that zip (the Quick Restart release it was written against) and prepare the game with it. Without it, or without the game on the Mac, that test reports as skipped.

## Local state

- `tools/ios-dotnet`, `tools/python`, and `.tools`: tools and download cache.
- `vendor`: the FMOD and Spine libraries setup downloads and checks.
- `analysis`: extracted assets, staging manifests, compiler outputs, and the generated Xcode projects.
- `src/STS2Mobile.FullGame/staged`, `addons`, and `icon.png`: prepared host inputs, none from the game.
- `.local/signing.json`: bundle identifier and development team captured from the generated Xcode project before regeneration.
- `.local/device.json`: the signing team, bundle id and iPhone for the signed build and the install.
- `.work`: local logs.

These paths are not publication files. Do not share generated game packs or compiled assemblies as part of the source repository.

## Updating inputs

A different game version needs its own patch profile: the patches, their count guards and the input hashes are written for v0.111.0. Never replace expected hashes just to make setup accept a new game build.

Python packages are pinned by wheel hash in `tools/requirements.txt`; to change one, replace its version and its `--hash` with the values PyPI lists for the `cp313` macOS arm64 wheel. NuGet packages are locked in the `packages*.lock.json` next to each project: the plain file for ordinary builds and tests, and one per runtime (`packages.ios-arm64.lock.json`, and `.mono` for the Mono publish) for the iOS publishes, whose dependencies differ. After changing a package reference, run the restores once with `RestoreLockedMode=false` in the environment (for example `RestoreLockedMode=false ./setup.sh`, `scripts/build-mono.sh` and `scripts/test.sh`), check the changed lock files and commit them.

A code mod is pinned in `mods/builtin.json` by the SHA-256 of its DLL and its manifest, and an optional one (QuickMatch) also in `tools/dependencies.json`, from which `./setup.sh` fetches it into `.tools/builtin-mods`; with no release and no copy in `.local/quickmatch`, the app is built without it. `scripts/prepare-game-files.sh` refuses a mod whose files no longer match, so a changed mod needs a deliberate update of that file.

QuickMatch's relay is not in its release: whoever builds chooses it. `QUICKMATCH_RELAY=wss://<host>` (or `https://<host>`, nothing after the host) in the environment when running `./setup.sh`, or else a `.local/QuickMatchRelay.cfg`, is written into `.tools/builtin-mods/QuickMatchRelay.cfg` and built into the app. That file has `key = value` lines and `#` comments: `relay = https://<host>` (or `wss://<host>`), and optionally `enabled = true` or `false`. Setup checks either setting as the app does (a host name on port 443, no IP address, path or query; no unknown or repeated keys) and stops on a problem. With neither, or without QuickMatch, the app has no relay, and QuickMatch matches over Steam lobby chat with no voice chat. Never put a relay address in a tracked file.

## Troubleshooting

- **Unsupported game hash (game files only):** supply the supported v0.111.0 installation. A matching version label alone is insufficient.
- **Missing Xcode components:** finish Xcode's first-launch setup and install its iOS platform.
- **Signing error:** check `team` in `.local/device.json` is your own team, signed in to Xcode. Do not copy another person's certificate or provisioning profile.
- **Stale prepared inputs:** rerun `./setup.sh`, then reopen or rebuild the generated project.
- **Dependency download failure:** retry setup. Unverified downloads must never become staged inputs.

Only `./setup.sh --install` touches a phone, and only to install the app. Setup never reads device saves or publishes anything.
