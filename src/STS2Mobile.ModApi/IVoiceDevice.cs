namespace STS2Mobile.ModApi;

public enum MicrophoneState { Unsupported, NeedsPermission, Asking, Denied, Ready }

/// <summary>16 kHz mono signed PCM samples. Call from the game thread.</summary>
public interface IVoiceDevice
{
    MicrophoneState CaptureState { get; }
    void RequestPermission();
    bool StartCapture();
    int ReadCapture(Span<short> destination);
    void StopCapture();
    int QueuedSamples { get; }
    void Play(ReadOnlySpan<short> samples);
    void StopOutput();
}
