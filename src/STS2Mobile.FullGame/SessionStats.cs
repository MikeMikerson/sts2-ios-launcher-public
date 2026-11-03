using System;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

/// <summary>
/// One line a second in the game's log for measuring a play session, read by tools/mono/analyze_session.py. It keeps
/// the fields of the first measurements' lines, under the tag [stats] (older logs say [proof]), and adds what those
/// could not say: the worst frame of the second, the memory iOS counts against the app, the heat and the battery. A long frame also
/// gets its own line at once, so it lands in the log beside what the game was doing.
/// </summary>
public partial class SessionStats : Node
{
    private const ulong HitchUsec = 100_000;
    private const double SampleSeconds = 1;
    private const int Megabyte = 1024 * 1024;
    private ulong _lastFrame, _worstFrame, _lastSample;

    /// <summary>A mark on the same clock as the samples.</summary>
    public static void Mark(string name) => GD.Print($"[stats] {name} ms={Time.GetTicksMsec()}");

    public override void _Ready() => ProcessMode = ProcessModeEnum.Always;

    public override void _Process(double delta)
    {
        ulong now = Time.GetTicksUsec();
        ulong frame = _lastFrame == 0 ? 0 : now - _lastFrame;
        _lastFrame = now;
        _worstFrame = Math.Max(_worstFrame, frame);
        if (frame >= HitchUsec) GD.Print($"[stats] hitch ms={now / 1000} frame_ms={frame / 1000}");
        if (now - _lastSample < SampleSeconds * 1_000_000) return;
        _lastSample = now;
        var (footprint, peak) = MemoryFootprint.Read();
        var (cpu, energy) = DevicePower.TaskUsage();
        GD.Print($"[stats] ms={now / 1000} fps={Engine.GetFramesPerSecond()} "
            + $"process_ms={Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:F1} "
            + $"static_mb={OS.GetStaticMemoryUsage() / Megabyte} managed_mb={GC.GetTotalMemory(false) / Megabyte} "
            + $"worst_frame_ms={_worstFrame / 1000} footprint_mb={footprint / Megabyte} peak_mb={peak / Megabyte} "
            + $"vram_mb={(long)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / Megabyte} "
            + $"malloc_mb={MemoryFootprint.MallocInUse() / Megabyte} objects={Count(Performance.Monitor.ObjectCount)} "
            + $"resources={Count(Performance.Monitor.ObjectResourceCount)} nodes={Count(Performance.Monitor.ObjectNodeCount)} "
            + $"orphans={Count(Performance.Monitor.ObjectOrphanNodeCount)} "
            + $"gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} thermal={DevicePower.ThermalState()} "
            + $"battery={DevicePower.BatteryPercent()} power={DevicePower.BatteryState()} "
            + $"cpu_s={cpu:F1} energy_j={energy:F1} at={DateTime.Now:HH:mm:ss}");
        _worstFrame = 0;
    }

    private static long Count(Performance.Monitor monitor) => (long)Performance.GetMonitor(monitor);
}
