using Mono.Cecil;
using Mono.Cecil.Cil;

namespace STS2Mobile.GamePatcher;

internal static class StartupPatches
{
    private const string PlatformUtil = "MegaCrit.Sts2.Core.Platform.PlatformUtil";

    public static IEnumerable<PatchStep> Steps =>
    [
        new("No second engine entry point", true, RemoveEntryPoint),
        new("Steam ownership checked by the launcher", true, patch =>
            patch.Replace(patch.Method("MegaCrit.Sts2.Core.Nodes.NGame", "InitializePlatform"), "Require launcher Steam ownership verification, never editor skipping.",
                il => CallAdapter(il, patch.Adapter("RuntimeServices", "RequireOwnershipAsync")))),
        new("No JIT warmup", false, patch =>
            patch.Noop(patch.Method("MegaCrit.Sts2.Core.Helpers.OneTimeInitialization", "PrewarmJit"), "iOS has no JIT (the game's code runs under Mono's interpreter), so there is nothing to prewarm.")),
        new("Steam Cloud through the launcher", true, patch =>
            patch.Replace(patch.Method("MegaCrit.Sts2.Core.Nodes.NGame", "DoCloudSync"), "Coordinate cloud startup through guarded launcher service.",
                il => CallAdapter(il, patch.Adapter("RuntimeServices", "SynchronizeCloudAsync")))),
        new("Player id from the signed-in account", true, patch =>
            patch.Replace(patch.Method(PlatformUtil, "GetLocalPlayerId"), "Use authenticated Steam account identity.",
                il => CallAdapter(il, patch.Adapter("RuntimeServices", "GetLocalPlayerId")))),
        new("Player name from the signed-in account", true, patch =>
            patch.Replace(patch.Method(PlatformUtil, "GetPlayerNameRaw"), "Steam name of the signed-in account; no Steamworks on the phone.", il =>
            {
                il.Emit(OpCodes.Ldarg_1);
                CallAdapter(il, patch.Adapter("RuntimeServices", "GetPlayerName"));
            })),
        // The game reports itself as a Steam game, so it would offer Steam's invite dialog, which needs the Steam overlay (Steamworks).
        new("No Steam invite dialog", false, patch =>
        {
            var methods = new[] { "SupportsInviteDialog", "TryOpenInviteDialog" }.Select(name => patch.Method(PlatformUtil, name)).ToArray();
            foreach (var method in methods)
                patch.Replace(method, "No Steam overlay on the phone, so no invite dialog.", il =>
                {
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ret);
                });
        }),
        new("Saves in the app's Steam folder", true, patch =>
            patch.Replace(patch.Method("MegaCrit.Sts2.Core.Saves.UserDataPathProvider", "GetPlatformDirectoryName"), "Keep Steam save namespace inside host's separate sandbox.", il =>
            {
                il.Emit(OpCodes.Ldstr, "steam");
                il.Emit(OpCodes.Ret);
            })),
        new("Links stay inside the app", true, patch =>
        {
            var methods = new[] { "MegaCrit.Sts2.Core.Platform.Steam.SteamPlatformUtilStrategy", "MegaCrit.Sts2.Core.Platform.Null.NullPlatformUtilStrategy" }
                .Select(type => patch.Method(type, "OpenUrl")).ToArray();
            foreach (var method in methods)
                patch.Replace(method, "Disable external browser escape until guarded presentation is available.", il =>
                {
                    il.Emit(OpCodes.Ldarg_1);
                    CallAdapter(il, patch.Adapter("RuntimeServices", "RejectExternalLink"));
                });
        }),
        new("No desktop system report", true, patch =>
            patch.Complete(patch.Method("MegaCrit.Sts2.Core.Debug.OsDebugInfo", "LogSystemInfo"), "Desktop system report queries the engine from a worker thread; not used on iOS.")),
        new("No desktop Steamworks start", true, patch =>
            patch.Reject(patch.Method("MegaCrit.Sts2.Core.Platform.Steam.SteamInitializer", "Initialize"), "Desktop Steamworks initialization is unavailable on iOS; use the guarded launcher.")),
    ];

    // Remove the managed engine entry point Godot generated into the game's assembly; the host supplies the app's own. A build without one has nothing to remove.
    private static void RemoveEntryPoint(GamePatch patch)
    {
        const string main = "GodotPlugins.Game.Main";
        if (!patch.Tolerant || patch.Types.Any(t => t.FullName == main)) patch.Module.Types.Remove(patch.Type(main));
        else patch.Note = main + " is not in this game version, so there was nothing to remove";
    }

    private static void CallAdapter(ILProcessor il, MethodReference adapter)
    {
        il.Emit(OpCodes.Call, adapter);
        il.Emit(OpCodes.Ret);
    }
}
