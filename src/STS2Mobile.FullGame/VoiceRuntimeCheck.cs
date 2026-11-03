using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

/// <summary>
/// <c>STS2_CHECK_VOICE=1</c>: runs the app's voice audio (<c>native/voice_audio.mm</c>, through <see cref="VoiceAudio"/>) the way
/// QuickMatch's voice chat drives it, with nobody there: the microphone permission and capture state, two seconds of recording
/// with how many samples arrived and where from, and one second of a quiet tone played and drained. On the simulator the
/// microphone is the Mac's, so this shows the pieces load and run, not that anyone hears them.
/// A test switch: it logs and changes nothing. Game thread only, like <see cref="VoiceAudio"/>.
/// </summary>
internal static class VoiceRuntimeCheck
{
    private const int Rate = 16000;

    public static async Task RunAsync()
    {
        GD.Print($"Voice check: functions {(VoiceAudio.Available ? "found" : "missing")}; capture state {StateName(VoiceAudio.CaptureState())}.");
        if (!VoiceAudio.Available) return;
        if (VoiceAudio.CaptureState() == VoiceAudio.NeedsPermission)
        {
            VoiceAudio.RequestPermission();
            await WaitAsync(() => VoiceAudio.CaptureState() != VoiceAudio.NeedsPermission && VoiceAudio.CaptureState() != VoiceAudio.Asking, 30);
            GD.Print($"Voice check: after asking, capture state {StateName(VoiceAudio.CaptureState())}.");
        }
        if (VoiceAudio.CaptureState() == VoiceAudio.Ready) await RecordAsync();
        await PlayAsync();
    }

    private static async Task RecordAsync()
    {
        long started = Stopwatch.GetTimestamp();
        if (!VoiceAudio.StartCapture())
        {
            GD.Print($"Voice check: microphone refused to start ({VoiceAudio.LastError()}).");
            return;
        }
        var buffer = new short[Rate / 10];
        long samples = 0;
        int peak = 0;
        // Read the way the mod does, a slice each frame, so the ring never fills.
        // The audio session's switch can take seconds on the simulator; stop waiting at recording or a failure with its reason.
        bool recording = await WaitAsync(() => { samples += Drain(buffer, ref peak); return VoiceAudio.CaptureStatus() is VoiceAudio.MicRecording or VoiceAudio.MicFailed; }, 20)
            && VoiceAudio.CaptureStatus() == VoiceAudio.MicRecording;
        double startMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!recording)
        {
            GD.Print($"Voice check: microphone status {StatusName(VoiceAudio.CaptureStatus())} after {startMs:F0} ms ({VoiceAudio.LastError()}).");
            VoiceAudio.StopCapture();
            return;
        }
        long readFrom = Stopwatch.GetTimestamp();
        samples = 0;
        await WaitAsync(() => { samples += Drain(buffer, ref peak); return false; }, 2);
        double seconds = Stopwatch.GetElapsedTime(readFrom).TotalSeconds;
        GD.Print($"Voice check: microphone recording on {VoiceAudio.RouteName(VoiceAudio.Route())}, started in {startMs:F0} ms; " +
            $"{samples} samples in {seconds:F1} s ({samples / seconds:F0} a second), peak {peak}.");
        VoiceAudio.StopCapture();
        await WaitAsync(() => VoiceAudio.CaptureStatus() == VoiceAudio.MicOff, 2);
        GD.Print($"Voice check: microphone off (status {StatusName(VoiceAudio.CaptureStatus())}).");
    }

    private static async Task PlayAsync()
    {
        // One second of a quiet 440 Hz tone, at about a tenth of full scale.
        var tone = new short[Rate];
        for (int i = 0; i < tone.Length; i++) tone[i] = (short)(3000 * Math.Sin(2 * Math.PI * 440 * i / Rate));
        VoiceAudio.Play(tone);
        int queued = VoiceAudio.Queued();
        bool drained = await WaitAsync(() => VoiceAudio.Queued() == 0, 4);
        GD.Print($"Voice check: played a one-second tone; {queued} samples queued, {(drained ? "all heard" : VoiceAudio.Queued() + " still queued after 4 s")}.");
        VoiceAudio.StopOutput();
    }

    private static int Drain(short[] buffer, ref int peak)
    {
        int count = VoiceAudio.Read(buffer);
        for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs((int)buffer[i]));
        return count;
    }

    /// <summary>Checks once a frame until <paramref name="done"/> or the time runs out; true if it was done.</summary>
    private static async Task<bool> WaitAsync(Func<bool> done, double seconds)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        long until = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            if (done()) return true;
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        return done();
    }

    private static string StateName(int state) => state switch
    {
        VoiceAudio.NeedsPermission => "not asked yet",
        VoiceAudio.Asking => "asking",
        VoiceAudio.Denied => "refused",
        VoiceAudio.Ready => "ready",
        _ => "cannot record",
    };

    private static string StatusName(int status) => status switch
    {
        VoiceAudio.MicStarting => "starting",
        VoiceAudio.MicRecording => "recording",
        VoiceAudio.MicFailed => "failed",
        _ => "off",
    };
}
