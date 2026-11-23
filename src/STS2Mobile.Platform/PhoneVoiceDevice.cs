using STS2Mobile.ModApi;

namespace STS2Mobile.Platform;

internal sealed class PhoneVoiceDevice : IVoiceDevice
{
    public MicrophoneState CaptureState => VoiceAudio.CaptureState() switch
    {
        VoiceAudio.NeedsPermission => MicrophoneState.NeedsPermission,
        VoiceAudio.Asking => MicrophoneState.Asking,
        VoiceAudio.Denied => MicrophoneState.Denied,
        VoiceAudio.Ready => MicrophoneState.Ready,
        _ => MicrophoneState.Unsupported,
    };
    public void RequestPermission() => VoiceAudio.RequestPermission();
    public bool StartCapture() => VoiceAudio.StartCapture();
    public int ReadCapture(Span<short> destination) => VoiceAudio.Read(destination);
    public void StopCapture() => VoiceAudio.StopCapture();
    public int QueuedSamples => VoiceAudio.Queued();
    public void Play(ReadOnlySpan<short> samples) => VoiceAudio.Play(samples);
    public void StopOutput() => VoiceAudio.StopOutput();
}
