using System.Runtime.InteropServices;

namespace STS2Mobile.Platform;

/// <summary>
/// The phone's microphone and voice output for voice chat, read from the small C functions in the
/// app's <c>native/voice_audio.mm</c>: recording only while the player has their mic on, and playback
/// through the same audio engine so iOS's echo removal knows what the speaker plays. Samples are
/// 16 kHz mono. Off the phone (tests, the Mac) those functions do not exist and <see cref="Available"/>
/// is false. Game thread only.
/// </summary>
public static unsafe class VoiceAudio
{
    /// <summary>The values <see cref="CaptureState"/> returns.</summary>
    public const int Unsupported = 0, NeedsPermission = 1, Asking = 2, Denied = 3, Ready = 4;

    /// <summary>The values <see cref="CaptureStatus"/> returns.</summary>
    public const int MicOff = 0, MicStarting = 1, MicRecording = 2, MicFailed = 3;

    private static readonly delegate* unmanaged<int> StateFunction = (delegate* unmanaged<int>)Export("sts2_voice_capture_state");
    private static readonly delegate* unmanaged<void> RequestFunction = (delegate* unmanaged<void>)Export("sts2_voice_request_permission");
    private static readonly delegate* unmanaged<int> StartFunction = (delegate* unmanaged<int>)Export("sts2_voice_start_capture");
    private static readonly delegate* unmanaged<void> StopFunction = (delegate* unmanaged<void>)Export("sts2_voice_stop_capture");
    private static readonly delegate* unmanaged<int> StatusFunction = (delegate* unmanaged<int>)Export("sts2_voice_capture_status");
    private static readonly delegate* unmanaged<short*, int, int> ReadFunction = (delegate* unmanaged<short*, int, int>)Export("sts2_voice_read");
    private static readonly delegate* unmanaged<int> QueuedFunction = (delegate* unmanaged<int>)Export("sts2_voice_queued");
    private static readonly delegate* unmanaged<short*, int, void> PlayFunction = (delegate* unmanaged<short*, int, void>)Export("sts2_voice_play");
    private static readonly delegate* unmanaged<void> StopOutputFunction = (delegate* unmanaged<void>)Export("sts2_voice_stop_output");
    private static readonly delegate* unmanaged<int> RouteFunction = (delegate* unmanaged<int>)Export("sts2_voice_route");
    private static readonly delegate* unmanaged<nint> ErrorFunction = (delegate* unmanaged<nint>)Export("sts2_voice_last_error");
    private static readonly delegate* unmanaged<nint> AudioStateFunction = (delegate* unmanaged<nint>)Export("sts2_voice_audio_state");

    /// <summary>True when the app carries every voice function.</summary>
    public static bool Available =>
        StateFunction != null && RequestFunction != null && StartFunction != null && StopFunction != null && StatusFunction != null &&
        ReadFunction != null && QueuedFunction != null && PlayFunction != null && StopOutputFunction != null && RouteFunction != null &&
        ErrorFunction != null && AudioStateFunction != null;

    /// <summary>One of <see cref="Unsupported"/>, <see cref="NeedsPermission"/>, <see cref="Asking"/>, <see cref="Denied"/>, <see cref="Ready"/>.</summary>
    public static int CaptureState() => StateFunction != null ? StateFunction() : Unsupported;

    /// <summary>Shows iOS's microphone prompt if the player has not answered it yet.</summary>
    public static void RequestPermission()
    {
        if (RequestFunction != null) RequestFunction();
    }

    /// <summary>
    /// Asks for recording to start. The audio engine starts off the game thread a moment later
    /// (<see cref="CaptureStatus"/>); false only without the microphone permission (<see cref="LastError"/>).
    /// </summary>
    public static bool StartCapture() => StartFunction != null && StartFunction() != 0;

    public static void StopCapture()
    {
        if (StopFunction != null) StopFunction();
    }

    /// <summary>
    /// One of <see cref="MicOff"/>, <see cref="MicStarting"/>, <see cref="MicRecording"/>, <see cref="MicFailed"/>.
    /// A microphone that fails is tried again at most once a second while it stays wanted.
    /// </summary>
    public static int CaptureStatus() => StatusFunction != null ? StatusFunction() : MicOff;

    /// <summary>Copies recorded samples, oldest first; returns how many.</summary>
    public static int Read(Span<short> destination)
    {
        if (ReadFunction == null) return 0;
        fixed (short* samples = destination)
        {
            return ReadFunction(samples, destination.Length);
        }
    }

    /// <summary>Samples handed to <see cref="Play"/> that have not been heard yet.</summary>
    public static int Queued() => QueuedFunction != null ? QueuedFunction() : 0;

    public static void Play(ReadOnlySpan<short> samples)
    {
        if (PlayFunction == null) return;
        fixed (short* data = samples)
        {
            PlayFunction(data, samples.Length);
        }
    }

    /// <summary>Drops queued voice and releases the output until the next <see cref="Play"/>.</summary>
    public static void StopOutput()
    {
        if (StopOutputFunction != null) StopOutputFunction();
    }

    /// <summary>Where recording goes: 2 the phone's speaker with echo removal, 1 without it, 0 headphones.</summary>
    public static int Route() => RouteFunction != null ? RouteFunction() : 0;

    /// <summary>A <see cref="Route"/> value for the log.</summary>
    public static string RouteName(int route) => route switch
    {
        2 => "phone speaker with echo removal",
        1 => "phone speaker without echo removal",
        _ => "headphones",
    };

    public static string LastError() =>
        ErrorFunction != null && Marshal.PtrToStringUTF8(ErrorFunction()) is { Length: > 0 } error ? error : "no reason given";

    /// <summary>
    /// For the log: what the voice engine runs, and the audio session it shares with the game's sound
    /// (category, sample rate, buffer and route).
    /// </summary>
    public static string AudioState() =>
        AudioStateFunction != null && Marshal.PtrToStringUTF8(AudioStateFunction()) is { Length: > 0 } state ? state : "unknown";

    private static nint Export(string name) => NativeExports.Find(name);
}
