using System.Security.Cryptography;
using System.Text;

namespace STS2Mobile.Platform;

/// <summary>
/// Makes a resource-only pack from an inspected export. Cleanup is allowed only for an exact known
/// input hash; unknown packs must already pass the normal resource checks. Source packs are kept.
/// </summary>
public static class ModPackPreparation
{
    public const string PolicyVersion = "mod-pack-1";
    // QuickRestart's released pack (exactly this hash) also carries its C# sources and other files it never loads, which the resource
    // checks refuse; for that one pack the four resources it uses are kept and the rest left out, so it imports as released.
    private const string QuickRestartPack = "3b7721262cacb78ae9fb9bd52c1772c2af2c9929811075fcc473d8d836dfcec3";
    private static readonly HashSet<string> QuickRestartResources = new(StringComparer.Ordinal)
    {
        ".godot/imported/mod_image.png-262ca25173881db5a1d83649999dd78f.ctex",
        "QuickRestart/localization/eng/gameplay_ui.json",
        "QuickRestart/localization/zhs/gameplay_ui.json",
        "QuickRestart/mod_image.png.import",
    };

    /// <summary>
    /// Returns omitted paths. An empty result means no cleanup was applied. A failed check leaves
    /// an existing destination unchanged; input and output must be different files.
    /// </summary>
    public static IReadOnlyList<string> Prepare(string inputPath, string outputPath)
    {
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pack preparation needs a separate output file.", nameof(outputPath));
        string temporary = outputPath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using var input = File.OpenRead(inputPath);
            string hash = Convert.ToHexStringLower(SHA256.HashData(input));
            input.Position = 0;
            IReadOnlyList<string> omitted;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                if (hash == QuickRestartPack)
                    omitted = CleanQuickRestart(input, output);
                else
                {
                    ModImport.CheckPack(input);
                    input.Position = 0;
                    input.CopyTo(output);
                    omitted = [];
                }
                output.Flush();
                ModImport.CheckPack(output);
            }
            // The same open input is checked again so a concurrent source change cannot be accepted.
            input.Position = 0;
            if (Convert.ToHexStringLower(SHA256.HashData(input)) != hash)
                throw new InvalidDataException("The mod pack changed while it was being prepared. Try importing it again.");
            File.Move(temporary, outputPath, overwrite: true);
            return omitted;
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("The .pck is damaged.", error);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record Entry(string Path, long Offset, long Size, byte[] Md5);

    // This reader is only reached after checking the whole inspected pack's SHA-256. It is not a
    // more permissive parser for arbitrary exports. The resulting pack still passes CheckPack.
    private static IReadOnlyList<string> CleanQuickRestart(Stream input, Stream output)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x43504447 || reader.ReadUInt32() != 3)
            throw new InvalidDataException("The inspected pack has an unexpected header.");
        uint major = reader.ReadUInt32(), minor = reader.ReadUInt32(), patch = reader.ReadUInt32();
        if (reader.ReadUInt32() != 2) throw new InvalidDataException("The inspected pack has unexpected flags.");
        long fileBase = reader.ReadInt64(), directory = reader.ReadInt64();
        input.Position = directory;
        uint count = reader.ReadUInt32();
        var kept = new List<Entry>();
        var omitted = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (uint i = 0; i < count; i++)
        {
            int length = reader.ReadInt32();
            if (length is <= 0 or > 4096) throw new InvalidDataException("The inspected pack has an invalid path.");
            string path = Encoding.UTF8.GetString(reader.ReadBytes(length)).TrimEnd('\0');
            long offset = reader.ReadInt64(), size = reader.ReadInt64();
            byte[] md5 = reader.ReadBytes(16);
            uint flags = reader.ReadUInt32();
            if (flags != 0 || !names.Add(path) || offset < 0 || size < 0 || fileBase < 0
                || fileBase > input.Length || offset > input.Length - fileBase || size > input.Length - fileBase - offset)
                throw new InvalidDataException("The inspected pack has an invalid entry.");
            if (QuickRestartResources.Contains(path)) kept.Add(new Entry(path, fileBase + offset, size, md5));
            else omitted.Add(path);
        }
        if (kept.Count != QuickRestartResources.Count)
            throw new InvalidDataException("The inspected pack is missing a resource required by its cleanup recipe.");

        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(new byte[128]);
        var written = new List<Entry>();
        var buffer = new byte[64 * 1024];
        foreach (var entry in kept)
        {
            Pad(output);
            long offset = output.Position - 128;
            input.Position = entry.Offset;
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            long remaining = entry.Size;
            while (remaining > 0)
            {
                int part = (int)Math.Min(buffer.Length, remaining);
                input.ReadExactly(buffer, 0, part);
                output.Write(buffer, 0, part);
                md5.AppendData(buffer, 0, part);
                remaining -= part;
            }
            byte[] checksum = md5.GetHashAndReset();
            if (!checksum.AsSpan().SequenceEqual(entry.Md5))
                throw new InvalidDataException("The inspected pack has a resource checksum mismatch: " + entry.Path);
            written.Add(new Entry(entry.Path, offset, entry.Size, checksum));
        }
        Pad(output);
        directory = output.Position;
        writer.Write(written.Count);
        foreach (var entry in written)
        {
            byte[] name = Encoding.UTF8.GetBytes(entry.Path);
            int padded = (name.Length + 3) & ~3;
            writer.Write(padded);
            writer.Write(name);
            writer.Write(new byte[padded - name.Length]);
            writer.Write(entry.Offset);
            writer.Write(entry.Size);
            writer.Write(entry.Md5);
            writer.Write(0u);
        }
        output.Position = 0;
        foreach (uint value in new[] { 0x43504447u, 3u, major, minor, patch, 2u }) writer.Write(value);
        writer.Write(128L);
        writer.Write(directory);
        writer.Flush();
        return omitted;
    }

    private static void Pad(Stream output)
    {
        int count = (int)(-output.Position & 31);
        if (count != 0) output.Write(new byte[count]);
    }
}
