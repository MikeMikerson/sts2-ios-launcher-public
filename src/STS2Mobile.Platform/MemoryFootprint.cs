using System.Runtime.InteropServices;

namespace STS2Mobile.Platform;

/// <summary>
/// This process's memory as iOS counts it against the app's limit. For logging measurements. The peak is for the process's
/// whole life, so it says something about a step only while that step is the largest thing the process has done.
/// </summary>
public static partial class MemoryFootprint
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int TaskVmInfo = 22;
    // Offsets into task_vm_info_data_t, in 64-bit words (mach/task_info.h).
    private const int FootprintWord = 18, PeakWord = 21;

    public static unsafe (long Current, long Peak) Read()
    {
        // The struct's size grows with the OS; the kernel fills what fits and says how much, in 32-bit units.
        const int words = 64;
        long* info = stackalloc long[words];
        int count = words * 2;
        int task = Marshal.ReadInt32(NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "mach_task_self_"));
        if (task_info(task, TaskVmInfo, info, ref count) != 0 || count < (PeakWord + 1) * 2) return (0, 0);
        return (info[FootprintWord], info[PeakWord]);
    }

    /// <summary>What malloc holds across all its zones: the engine and Mono's own structures, not the managed heap or the GPU's memory.</summary>
    public static long MallocInUse()
    {
        malloc_zone_statistics(0, out MallocStatistics statistics);
        return (long)statistics.SizeInUse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MallocStatistics
    {
        public uint BlocksInUse;
        public nuint SizeInUse, MaxSizeInUse, SizeAllocated;
    }

    [LibraryImport(LibSystem)]
    private static unsafe partial int task_info(int task, int flavor, long* info, ref int count);

    [LibraryImport(LibSystem)]
    private static partial void malloc_zone_statistics(nint zone, out MallocStatistics statistics);
}
