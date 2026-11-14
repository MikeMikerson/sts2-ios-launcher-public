namespace STS2Mobile.Platform;

/// <summary>
/// Which way up the app is: the launcher in portrait, the game in landscape. iOS is asked through the C function in the app's
/// <c>native/orientation_guard.mm</c>, which also keeps the app that way. Off the phone that function does not exist and this does nothing.
/// </summary>
public static unsafe class ScreenOrientation
{
    private static readonly delegate* unmanaged<int, void> SetFunction = (delegate* unmanaged<int, void>)NativeExports.Find("sts2_orientation_set");
    private static readonly delegate* unmanaged<byte*> GesturesFunction = (delegate* unmanaged<byte*>)NativeExports.Find("sts2_gestures_state");

    public static void Set(bool landscape)
    {
        if (SetFunction != null) SetFunction(landscape ? 1 : 0);
    }

    /// <summary>What iOS is told about the screen-edge gestures (the game holds every edge back), or null off the phone.</summary>
    public static string? GestureState() =>
        GesturesFunction == null ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)GesturesFunction());
}
