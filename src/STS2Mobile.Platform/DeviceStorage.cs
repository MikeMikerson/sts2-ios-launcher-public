using System.Runtime.InteropServices;

namespace STS2Mobile.Platform;

/// <summary>
/// Disk space and backups, from the C functions in the app's <c>native/game_files.mm</c>. Off the phone those functions do
/// not exist: the space is the volume's, and nothing is excluded from anything.
/// </summary>
public static unsafe class DeviceStorage
{
    private const string FreeBytesOverride = "STS2_FREE_BYTES";
    private static readonly delegate* unmanaged<long> FreeFunction = (delegate* unmanaged<long>)NativeExports.Find("sts2_files_free_bytes");
    private static readonly delegate* unmanaged<byte*, int> ExcludeFunction = (delegate* unmanaged<byte*, int>)NativeExports.Find("sts2_files_exclude_from_backup");

    /// <summary>The space a download may count on. <c>STS2_FREE_BYTES</c> stands in for it, to test a full iPhone.</summary>
    public static long FreeBytes(string folder)
    {
        if (long.TryParse(Environment.GetEnvironmentVariable(FreeBytesOverride), out long forced)) return forced;
        if (FreeFunction != null && FreeFunction() is var free and >= 0) return free;
        return new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace;
    }

    /// <summary>A folder iCloud and computer backups skip, with what is in it. False if iOS refused, or off the phone.</summary>
    public static bool ExcludeFromBackup(string folder)
    {
        if (ExcludeFunction == null) return false;
        nint path = Marshal.StringToCoTaskMemUTF8(folder);
        try { return ExcludeFunction((byte*)path) != 0; }
        finally { Marshal.FreeCoTaskMem(path); }
    }
}
