using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace STS2Mobile.GamePatcher;

internal static class PhonePatches
{
    public static IEnumerable<PatchStep> Steps =>
    [
        new("Fewer background loads at once, to bound memory", false, LimitLoadsInFlight),
        // A touchscreen has no pointer to restyle. Each attempt only produced an engine warning with a
        // captured backtrace (290 of them in a five minute session on the phone).
        new("No mouse cursor", false, patch =>
            patch.Noop(patch.Method("MegaCrit.Sts2.Core.Nodes.CommonUi.NCursorManager", "UpdateCursor"), "No mouse cursor on a touchscreen.")),
        new("A card let go below the play zone goes back", false, CancelCardReleasedBelowPlayZone),
        new("The game fills the screen", false, FillScreen),
        new("The game's version read from its folder", false, ReadReleaseInfoFromGameFolder),
    ];

    // The game looks for release_info.json beside its executable. On the phone that is the app, which holds no game file and
    // cannot be written to, so the file sits with the rest of the game in the app's data folder.
    private static void ReadReleaseInfoFromGameFolder(GamePatch patch)
    {
        var text = patch.Module.TypeSystem.String;
        patch.Replace(patch.Method("MegaCrit.Sts2.Core.Debug.ReleaseInfoManager", "GetPossibleReleaseInfoPaths"),
            "Read release_info.json from the game folder in the app's data folder.", il =>
            {
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Newarr, text);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldstr, "user://game/release_info.json");
                il.Emit(OpCodes.Stelem_Ref);
                il.Emit(OpCodes.Ret);
            });
    }

    // The desktop game keeps up to 128 background loads in flight. On the phone the loader threads then
    // decode far faster than the main thread can hand textures to the GPU, and the decoded images wait
    // in memory: a spike of about 700 MB while the main menu's asset set loads, enough to end the app
    // when a finished run returns to the menu. A small number in flight
    // bounds that queue to about one frame of work for the phone's six cores; nothing else changes.
    private static void LimitLoadsInFlight(GamePatch patch)
    {
        const int desktopLoadsInFlight = 128, phoneLoadsInFlight = 6;
        var loadQueue = patch.Method("MegaCrit.Sts2.Core.Assets.AssetLoadingSession", "ProcessLoadingQueue");
        var limit = loadQueue.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == desktopLoadsInFlight);
        limit.Operand = phoneLoadsInFlight;
        patch.Record(loadQueue.FullName, $"Keep at most {phoneLoadsInFlight} background loads in flight instead of {desktopLoadsInFlight}, to bound memory while loading.");
    }

    // Touch has no right click, the desktop way to put back a card that was picked up. A card released
    // before it reaches the play zone therefore stayed attached to the last touch position with no way
    // to choose another. Releasing it there now cancels the play, exactly as a right click does.
    private static void CancelCardReleasedBelowPlayZone(GamePatch patch)
    {
        var cardPlayInput = patch.Method("MegaCrit.Sts2.Core.Nodes.Combat.NMouseCardPlay", "_Input");
        var cardPlay = cardPlayInput.DeclaringType;
        var released = cardPlayInput.Body.Instructions.Single(i => i.OpCode == OpCodes.Stfld && ((FieldReference)i.Operand).Name == "_isLeftMouseDown" &&
            i.Previous.OpCode == OpCodes.Ldc_I4_0);
        var inPlayZone = cardPlay.Methods.Single(m => m.Name == "IsCardInPlayZone");
        var cancelPlay = patch.Module.ImportReference(cardPlay.BaseType.Resolve().Methods.Single(m => m.Name == "CancelPlayCard"));
        var il = cardPlayInput.Body.GetILProcessor();
        // Long-form branches while instructions are inserted, so no short branch can fall out of range.
        cardPlayInput.Body.SimplifyMacros();
        var resume = released.Next;
        var cancel = new[] {
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, inPlayZone),
            il.Create(OpCodes.Brtrue, resume),
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, cancelPlay) };
        foreach (var instruction in cancel) il.InsertBefore(resume, instruction);
        cardPlayInput.Body.OptimizeMacros();
        patch.Record(cardPlayInput.FullName, "Put a picked-up card back when the touch is released below the play zone.");
    }

    // A phone has one window that always covers the screen. The game's saved aspect ratio (16:9 unless
    // the player changed it, and whatever the desktop used once saves are imported) left black bars on
    // both sides of the 19.5:9 screen. Reading it as Auto selects the game's own wide layout, the same
    // one a desktop player gets with that option; the stored value is not consulted on the phone.
    private static void FillScreen(GamePatch patch)
    {
        const int auto = 5;
        var aspectRatio = patch.Type("MegaCrit.Sts2.Core.Settings.AspectRatioSetting");
        if (aspectRatio.Fields.Single(f => f.Name == "Auto").Constant is not int value || value != auto)
            throw new InvalidOperationException("Unexpected AspectRatioSetting values.");
        patch.Replace(patch.Method("MegaCrit.Sts2.Core.Saves.SettingsSave", "get_AspectRatioSetting"),
            "Fill the phone's screen: always use the game's automatic aspect ratio.", il =>
            {
                il.Emit(OpCodes.Ldc_I4, auto);
                il.Emit(OpCodes.Ret);
            });
    }
}
