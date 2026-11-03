using System.Runtime.InteropServices;

namespace STS2Mobile.Platform;

/// <summary>
/// iOS's Files picker for one .zip, from the small C functions in the app's <c>native/mod_import.mm</c>.
/// Off the phone those functions do not exist and the picker is unavailable.
/// </summary>
public static unsafe class FilesPicker
{
    private const int PathCapacity = 4096;
    private static readonly delegate* unmanaged<int> PickFunction = (delegate* unmanaged<int>)NativeExports.Find("sts2_mod_import_pick");
    private static readonly delegate* unmanaged<byte*, int, PollResult> PollFunction = (delegate* unmanaged<byte*, int, PollResult>)NativeExports.Find("sts2_mod_import_poll");

    // The sts2_mod_import_poll results; the enum in native/mod_import.mm holds the same values.
    private enum PollResult
    {
        Failed = -1,
        Idle = 0,
        Picked = 1,
        Cancelled = 2,
    }

    public static bool Available => PickFunction != null && PollFunction != null;

    /// <summary>Opens the picker. False when it cannot be shown, or one is already open.</summary>
    public static bool Open() => Available && PickFunction() == 1;

    /// <summary>Asked once a frame while the picker is open. <paramref name="path"/> is set only for <see cref="FilesPickerResult.Picked"/>.</summary>
    public static FilesPickerResult Poll(out string? path)
    {
        path = null;
        if (!Available) return FilesPickerResult.Failed;
        byte* buffer = stackalloc byte[PathCapacity];
        switch (PollFunction(buffer, PathCapacity))
        {
            case PollResult.Idle: return FilesPickerResult.Nothing;
            case PollResult.Picked:
                path = Marshal.PtrToStringUTF8((nint)buffer);
                return string.IsNullOrEmpty(path) ? FilesPickerResult.Failed : FilesPickerResult.Picked;
            case PollResult.Cancelled: return FilesPickerResult.Cancelled;
            default: return FilesPickerResult.Failed;
        }
    }
}
