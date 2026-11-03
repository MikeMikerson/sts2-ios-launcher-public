using System.Text;

namespace STS2Mobile.Platform;

/// <summary>
/// The limits and the pack check for mods added on the phone. <see cref="ModPackageImport"/> applies the zip limits
/// to a picked archive, and <see cref="CheckPack"/> refuses a Godot .pck that is or carries code of any kind, or
/// that would replace a file the engine or this app needs to start.
/// </summary>
public static class ModImport
{
    public const long MaxArchiveEntries = 64;
    public const long MaxUncompressedBytes = 1L << 30;
    private const uint PackMagic = 0x43504447; // "GDPC"
    private const int PackFormat = 3;          // Godot 4.4 and later
    private const uint PackEncrypted = 1, PackRelativeBase = 2;
    private const uint FileEncrypted = 1, FileRemoval = 2;

    // Files that hold or load code, or that would change how the engine or this app starts.
    private static readonly string[] RefusedExtensions = [".gd", ".gdc", ".gde", ".cs", ".gdextension", ".dll", ".so", ".dylib", ".framework", ".a", ".wasm", ".pck", ".zip"];
    private static readonly string[] RefusedPaths = ["project.godot", "project.binary", "override.cfg", "export_presets.cfg", "Launcher.tscn"];
    private static readonly string[] RefusedFolders = [".godot/mono/", "addons/", "warmup/", "native/", "staged/"];
    private static readonly string[] RefusedEngineFiles = ["global_script_class_cache.cfg", "uid_cache.bin", "extension_list.cfg"];
    // Type names a scene or resource uses to embed or attach code.
    private static readonly byte[][] CodeMarkers = [.. new[] { "GDScript", "CSharpScript", "GDExtension", "ScriptExtension", "NativeScript" }.Select(Encoding.ASCII.GetBytes)];

    /// <summary>Reads a Godot 4 pack's directory and every file in it, refusing anything that is or carries code.</summary>
    public static void CheckPack(Stream pack)
    {
        try
        {
            CheckPackContents(pack);
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException)
        {
            throw new InvalidDataException("The .pck is damaged.");
        }
    }

    private static void CheckPackContents(Stream pack)
    {
        using var reader = new BinaryReader(pack, Encoding.UTF8, leaveOpen: true);
        pack.Position = 0;
        if (pack.Length < 104 || reader.ReadUInt32() != PackMagic) throw new InvalidDataException("The .pck is not a Godot pack.");
        uint format = reader.ReadUInt32();
        reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt32(); // engine version
        uint flags = reader.ReadUInt32();
        if (format != PackFormat) throw new InvalidDataException($"The .pck was made by an engine version this game does not use (pack format {format}).");
        if ((flags & PackEncrypted) != 0) throw new InvalidDataException("The .pck is encrypted, so it cannot be checked.");
        if ((flags & ~(PackEncrypted | PackRelativeBase)) != 0) throw new InvalidDataException("The .pck has unsupported flags.");
        ulong fileBase = reader.ReadUInt64(), directory = reader.ReadUInt64();
        ulong packLength = (ulong)pack.Length;
        if (directory < 104 || directory > packLength - 4
            || ((flags & PackRelativeBase) != 0 && (fileBase < 104 || fileBase > packLength)))
            throw new InvalidDataException("The .pck is damaged.");
        pack.Position = (long)directory;
        uint count = reader.ReadUInt32();
        if (count > 100_000) throw new InvalidDataException("The .pck is damaged.");
        var entries = new List<(string Path, long Offset, long Size)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var strictUtf8 = new UTF8Encoding(false, true);
        for (uint i = 0; i < count; i++)
        {
            uint length = reader.ReadUInt32();
            if (length is 0 or > 4096 || (ulong)(pack.Length - pack.Position) < length + 36UL)
                throw new InvalidDataException("The .pck is damaged.");
            string path = strictUtf8.GetString(reader.ReadBytes((int)length)).TrimEnd('\0');
            ulong offset = reader.ReadUInt64(), size = reader.ReadUInt64();
            reader.ReadBytes(16); // md5
            uint fileFlags = reader.ReadUInt32();
            if ((fileFlags & FileEncrypted) != 0) throw new InvalidDataException($"{path} is encrypted, so it cannot be checked.");
            if ((fileFlags & FileRemoval) != 0) throw new InvalidDataException($"The .pck removes {path} from the game.");
            if ((fileFlags & ~(FileEncrypted | FileRemoval)) != 0) throw new InvalidDataException($"{path} has unsupported flags.");
            ulong start = (flags & PackRelativeBase) != 0 ? fileBase : 0;
            // Subtraction bounds both additions, including offset + fileBase, before they happen.
            if (offset > packLength - start || size > packLength - start - offset)
                throw new InvalidDataException("The .pck is damaged.");
            ulong absolute = start + offset;
            if (absolute < 104) throw new InvalidDataException("The .pck is damaged.");
            string normalized = CheckPackPath(path);
            if (!paths.Add(normalized)) throw new InvalidDataException($"The .pck contains a duplicate path ({path}).");
            entries.Add((normalized, (long)absolute, (long)size));
        }
        long directoryEnd = pack.Position;
        var buffer = new byte[1 << 16];
        foreach (var (path, offset, size) in entries)
        {
            if (size > 0 && offset < directoryEnd && offset + size > (long)directory)
                throw new InvalidDataException("The .pck has resource data inside its directory.");
            pack.Position = offset;
            var start = new byte[Math.Min(4, size)];
            if (pack.Read(start, 0, start.Length) != start.Length) throw new InvalidDataException("The .pck is damaged.");
            // A compressed resource cannot be read for its contents here, so it is refused rather than trusted.
            if (start.Length == 4 && Encoding.ASCII.GetString(start) == "RSCC") throw new InvalidDataException($"{path} is a compressed resource, so it cannot be checked.");
            pack.Position = offset;
            long remaining = size;
            int carry = 0;
            int overlap = CodeMarkers.Max(m => m.Length) - 1;
            while (remaining > 0)
            {
                int read = pack.Read(buffer, carry, (int)Math.Min(buffer.Length - carry, remaining));
                if (read <= 0) throw new InvalidDataException("The .pck is damaged.");
                remaining -= read;
                var window = buffer.AsSpan(0, carry + read);
                foreach (var marker in CodeMarkers)
                    if (window.IndexOf(marker) >= 0) throw new InvalidDataException($"{path} contains a script ({Encoding.ASCII.GetString(marker)}). A mod's pack cannot contain scripts.");
                carry = Math.Min(overlap, window.Length);
                window[^carry..].CopyTo(buffer);
            }
        }
    }

    private static string CheckPackPath(string stored)
    {
        string path = stored.StartsWith("res://", StringComparison.Ordinal) ? stored[6..] : stored;
        string lower = path.ToLowerInvariant();
        if (path.Length == 0 || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)
            || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"The .pck contains an invalid path ({stored}).");
        if (RefusedExtensions.Any(e => lower.EndsWith(e, StringComparison.Ordinal)) || RefusedExtensions.Any(e => lower.EndsWith(e + ".remap", StringComparison.Ordinal) || lower.EndsWith(e + ".import", StringComparison.Ordinal)))
            throw new InvalidDataException($"The .pck contains code or another package ({path}). A mod's pack cannot contain scripts, libraries or other packages.");
        if (RefusedPaths.Contains(path, StringComparer.OrdinalIgnoreCase) || RefusedFolders.Any(folder => lower.StartsWith(folder, StringComparison.Ordinal))
            || RefusedEngineFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"The .pck replaces a file the app needs to start ({path}).");
        return path;
    }
}
