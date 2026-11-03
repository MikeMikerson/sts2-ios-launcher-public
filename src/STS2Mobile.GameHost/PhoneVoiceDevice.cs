#if QUICKMATCH
using System;
using System.Diagnostics;
using Godot;
using QuickMatch.Voice;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// QuickMatch's voice chat on the phone: the mod's voice device answered by <see cref="VoiceAudio"/>
/// (native/voice_audio.mm), which records the microphone only while the player has their mic on and
/// plays the teammates' voices through the same audio engine, so iOS's echo removal knows what the
/// speaker is playing. QuickMatch sends the voice only over its relay, encrypted (NETWORK.md). Off the
/// phone there is no device.
/// </summary>
internal sealed class PhoneVoiceDevice : IVoiceDevice, IVoiceOutput
{
    private VoiceCaptureState? _lastState;
    private string? _lastFailure;
    private int _lastStatus;
    private int _lastRoute;
    private long _startedAt;
    private string? _lastAudioState;
    private long _audioCheckedAt;

    private PhoneVoiceDevice()
    {
    }

    /// <summary>The phone's voice device, or null where the app's voice functions are missing.</summary>
    public static PhoneVoiceDevice? TryCreate() => VoiceAudio.Available ? new PhoneVoiceDevice() : null;

    public VoiceCaptureState CaptureState
    {
        get
        {
            var state = VoiceAudio.CaptureState() switch
            {
                VoiceAudio.NeedsPermission => VoiceCaptureState.NeedsPermission,
                VoiceAudio.Asking => VoiceCaptureState.Asking,
                VoiceAudio.Denied => VoiceCaptureState.Denied,
                VoiceAudio.Ready => VoiceCaptureState.Ready,
                _ => VoiceCaptureState.Unsupported,
            };
            if (_lastState == VoiceCaptureState.Asking && state is VoiceCaptureState.Ready or VoiceCaptureState.Denied)
            {
                GD.Print($"voice: microphone permission {(state == VoiceCaptureState.Ready ? "granted" : "refused")}");
            }
            _lastState = state;
            return state;
        }
    }

    public IVoiceOutput Output => this;

    public void RequestPermission() => VoiceAudio.RequestPermission();

    public bool StartCapture()
    {
        if (!VoiceAudio.StartCapture())
        {
            // Asked again every frame while the mic is on: log a failure when its reason changes.
            LogFailure();
            return false;
        }
        _lastFailure = null;
        _lastStatus = VoiceAudio.MicStarting;
        _startedAt = Stopwatch.GetTimestamp();
        return true;
    }

    public void StopCapture()
    {
        VoiceAudio.StopCapture();
        _lastStatus = VoiceAudio.MicOff;
        GD.Print("voice: microphone off");
    }

    public int ReadCapture(Span<short> destination)
    {
        NoteAudioState();
        NoteCaptureStatus();
        return VoiceAudio.Read(destination);
    }

    // The microphone starts off the game thread a moment after StartCapture and starts again after a
    // route change (headphones in or out): log where it records once it does, or why it cannot.
    private void NoteCaptureStatus()
    {
        int status = VoiceAudio.CaptureStatus();
        if (status == VoiceAudio.MicRecording)
        {
            int route = VoiceAudio.Route();
            if (_lastStatus != VoiceAudio.MicRecording)
            {
                GD.Print($"voice: microphone on, {VoiceAudio.RouteName(route)} " +
                    $"(started in {Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds:F0} ms)");
            }
            else if (route != _lastRoute)
            {
                GD.Print($"voice: microphone now on {VoiceAudio.RouteName(route)}");
            }
            _lastRoute = route;
        }
        else if (status == VoiceAudio.MicFailed)
        {
            LogFailure();
        }
        _lastStatus = status;
    }

    private void LogFailure()
    {
        string reason = VoiceAudio.LastError();
        if (reason != _lastFailure)
        {
            _lastFailure = reason;
            GD.Print($"voice: microphone did not start ({reason})");
        }
    }

    /// <summary>
    /// Logs the voice engine and the audio session the game's sound shares with it, at most once a second and
    /// only when they change, so a change in the game's sound can be matched with what voice did.
    /// </summary>
    public void NoteAudioState(string? when = null)
    {
        long now = Stopwatch.GetTimestamp();
        if (when == null && Stopwatch.GetElapsedTime(_audioCheckedAt, now).TotalSeconds < 1) return;
        _audioCheckedAt = now;
        string state = VoiceAudio.AudioState();
        if (state == _lastAudioState) return;
        _lastAudioState = state;
        GD.Print($"voice: audio {when ?? "now"}: {state}");
    }

    public int QueuedSamples
    {
        get
        {
            NoteAudioState();
            return VoiceAudio.Queued();
        }
    }

    public void Play(ReadOnlySpan<short> samples)
    {
        NoteAudioState();
        VoiceAudio.Play(samples);
    }

    public void Stop()
    {
        VoiceAudio.StopOutput();
        GD.Print("voice: output off");
    }
}
#endif
