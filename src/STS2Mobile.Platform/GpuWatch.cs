namespace STS2Mobile.Platform;

/// <summary>
/// What the app's <c>native/gpu_watch.mm</c> has seen of the GPU: command buffers that ended in an error, and whether it found the
/// picture stopped and showed its screen. Off the phone those functions do not exist and this reports nothing.
/// </summary>
public static unsafe class GpuWatch
{
    private static readonly delegate* unmanaged<int> ErrorsFunction = (delegate* unmanaged<int>)NativeExports.Find("sts2_gpu_errors");
    private static readonly delegate* unmanaged<int> StoppedFunction = (delegate* unmanaged<int>)NativeExports.Find("sts2_gpu_picture_stopped");

    /// <summary>Command buffers that ended in an error since launch, or -1 off the phone.</summary>
    public static int Errors() => ErrorsFunction == null ? -1 : ErrorsFunction();

    public static bool PictureStopped() => StoppedFunction != null && StoppedFunction() != 0;
}
