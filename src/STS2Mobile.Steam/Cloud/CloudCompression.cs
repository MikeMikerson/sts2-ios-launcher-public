using System.IO.Compression;

namespace STS2Mobile.Steam.Cloud;

/// <summary>
/// Steam Cloud keeps larger files as a one-entry zip and lists both sizes. The same format both ways, as other
/// Steam Cloud clients send it: one entry named <c>data</c>, and the raw bytes when zipping would not make them smaller.
/// </summary>
public static class CloudCompression
{
    public static (byte[] Data, bool Compressed) Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream entry = archive.CreateEntry("data", CompressionLevel.Optimal).Open();
            entry.Write(raw, 0, raw.Length);
        }
        byte[] zipped = output.ToArray();
        return zipped.Length >= raw.Length ? (raw, false) : (zipped, true);
    }

    public static bool IsZip(byte[] content) => content is [(byte)'P', (byte)'K', 3, 4, ..];

    /// <summary>The most a download may unzip to when Steam did not list the file's raw size.</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Unzips the one entry, stopping with <see cref="InvalidDataException"/> as soon as the output passes
    /// <paramref name="maxBytes"/> (the raw size Steam listed for the file), so a small download cannot expand without bound.
    /// </summary>
    public static byte[] Decompress(byte[] zipped, long maxBytes = DefaultMaxBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(zipped), ZipArchiveMode.Read);
        if (archive.Entries.Count == 0) throw new InvalidDataException("The cloud file's zip is empty.");
        using Stream entry = archive.Entries[0].Open();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = entry.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (read > maxBytes - output.Length)
                throw new InvalidDataException($"The cloud file unzips to more than its listed {maxBytes} bytes.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
