using System.Runtime.InteropServices;

namespace STS2Mobile.GamePack;

/// <summary>
/// astc-encoder and bcdec through native/texture_encoder.cpp. In the app the library is linked into the Mono library, and the
/// P/Invoke override table finds these (tools/mono/pinvoke_table.py). On the Mac it is a dylib beside the tests.
/// </summary>
internal sealed unsafe partial class TextureEncoder : IDisposable
{
    private const string Library = "libsts2_texture_encoder";
    private nint _native;

    /// <param name="threads">How many threads convert one texture. Several encoders of one thread each can run side by side.</param>
    public TextureEncoder(int threads)
    {
        _native = sts2_texture_encoder_create(threads);
        if (_native == 0) throw new PackException("The texture encoder could not start.");
    }

    public void Dispose()
    {
        sts2_texture_encoder_free(_native);
        _native = 0;
    }

    /// <summary>How long a texture is once converted.</summary>
    public static long OutputBytes(ReadOnlySpan<byte> source)
    {
        fixed (byte* start = source)
        {
            long bytes = sts2_texture_encoder_output_bytes(start, source.Length);
            return bytes >= 0 ? bytes : throw new PackException(Describe((int)bytes));
        }
    }

    public void Convert(ReadOnlySpan<byte> source, Span<byte> output)
    {
        fixed (byte* input = source, result = output)
        {
            int status = sts2_texture_encoder_convert(_native, input, source.Length, result, output.Length);
            if (status != 0) throw new PackException(Describe(status));
        }
    }

    // The numbers of Failure in native/texture_encoder.cpp.
    private static string Describe(int failure) => failure switch
    {
        -2 => "A texture is not in the expected container.",
        -3 => "A texture has an unsupported format.",
        -4 => "A texture's size is not whole blocks.",
        -5 => "A texture's data does not match its header.",
        -6 => "A converted texture does not fit its buffer.",
        -7 => "The texture encoder failed.",
        -8 => "The texture encoder ran out of memory.",
        _ => "The texture encoder stopped with code " + failure + ".",
    };

    [LibraryImport(Library)] private static partial nint sts2_texture_encoder_create(int threads);
    [LibraryImport(Library)] private static partial void sts2_texture_encoder_free(nint encoder);
    [LibraryImport(Library)] private static partial long sts2_texture_encoder_output_bytes(byte* source, long size);
    [LibraryImport(Library)] private static partial int sts2_texture_encoder_convert(nint encoder, byte* source, long size, byte* output, long capacity);
}
