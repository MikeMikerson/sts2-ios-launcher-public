using System.Runtime.InteropServices;

namespace STS2Mobile.Platform;

/// <summary>
/// The UIKit screen over the engine for the end of setup, from the C functions in the app's <c>native/setup_overlay.mm</c>, which says
/// why it is not drawn by the engine. It closes the app when its button is pressed. Off the phone those functions do not exist and this does nothing.
/// </summary>
public static unsafe class SetupOverlay
{
    private static readonly delegate* unmanaged<byte*, byte*, byte*, float, byte*, void> ShowFunction =
        (delegate* unmanaged<byte*, byte*, byte*, float, byte*, void>)NativeExports.Find("sts2_overlay_show");
    private static readonly delegate* unmanaged<void> PressFunction = (delegate* unmanaged<void>)NativeExports.Find("sts2_overlay_press");

    /// <param name="steps">The row of steps: "label=state" joined by tabs, the state 0 done, 1 current, 2 waiting.</param>
    /// <param name="fraction">From 0 to 1, or negative for no bar.</param>
    /// <param name="button">The label of the button that closes the app, or null for none.</param>
    public static void Show(string steps, string title, string status, float fraction, string? button)
    {
        if (ShowFunction == null) return;
        nint stepsText = Marshal.StringToCoTaskMemUTF8(steps), titleText = Marshal.StringToCoTaskMemUTF8(title), statusText = Marshal.StringToCoTaskMemUTF8(status), buttonText = Marshal.StringToCoTaskMemUTF8(button);
        try { ShowFunction((byte*)stepsText, (byte*)titleText, (byte*)statusText, fraction, (byte*)buttonText); }
        finally
        {
            Marshal.FreeCoTaskMem(stepsText);
            Marshal.FreeCoTaskMem(titleText);
            Marshal.FreeCoTaskMem(statusText);
            Marshal.FreeCoTaskMem(buttonText);
        }
    }

    /// <summary>Presses the button, for a run with nobody there.</summary>
    public static void Press()
    {
        if (PressFunction != null) PressFunction();
    }
}
