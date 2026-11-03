using System.Runtime.InteropServices;

namespace STS2Mobile.Platform;

/// <summary>
/// The C functions the app's <c>native/*.mm</c> units export, found by name. Off the phone (tests,
/// the Mac) they do not exist and every lookup gives 0.
/// </summary>
internal static class NativeExports
{
    public static nint Find(string name)
    {
        if (!OperatingSystem.IsIOS()) return 0;
        return NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), name, out nint address) ? address : 0;
    }
}
