using System.Security.Cryptography;
using System.Text;

namespace STS2Mobile.GamePack;

public sealed class PackException(string message) : Exception(message);

public readonly record struct PackEntry(string Name, long Offset, long Size, byte[] Md5);

/// <summary>
/// The Godot 4.5 resource pack format (core/io/file_access_pack.h), as tools/godot_pack.py reads it. It accepts only the
/// shape the pinned engine and game produce, so a different one fails here instead of being half understood.
/// </summary>
public static class PackFormat
{
    public const int HeaderSize = 104;
    public const int Alignment = 32;
    public const uint Magic = 0x43504447;
    public const uint Version = 3;
    public const uint EngineMajor = 4, EngineMinor = 5, EnginePatch = 1;
    /// <summary>The one pack flag the supported packs set: entry offsets count from the file base.</summary>
    public const uint RelativeFileBase = 2;
    /// <summary>Far above any real resource path, so a corrupt length cannot make the reader swallow the file.</summary>
    public const int MaxNameLength = 16384;
    /// <summary>Copying a pack through this much memory at a time is what keeps 2 GB out of the app's footprint.</summary>
    public const int CopyBufferBytes = 1024 * 1024;
}

/// <summary>A pack open for reading. Entries are read one at a time, so the file's size costs no memory.</summary>
public sealed class PackReader : IDisposable
{
    private readonly FileStream _stream;

    public IReadOnlyList<PackEntry> Entries { get; }

    public PackReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, PackFormat.CopyBufferBytes);
        try { Entries = ReadDirectory(_stream); }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public void Dispose() => _stream.Dispose();

    public byte[] Read(PackEntry entry)
    {
        var data = new byte[entry.Size];
        _stream.Seek(entry.Offset, SeekOrigin.Begin);
        _stream.ReadExactly(data);
        if (!MD5.HashData(data).AsSpan().SequenceEqual(entry.Md5)) throw new PackException("Pack entry MD5 mismatch: " + entry.Name);
        return data;
    }

    /// <summary>An entry into memory the caller owns, without its checksum: for a pack already verified as a whole.</summary>
    public void ReadInto(PackEntry entry, Span<byte> target)
    {
        _stream.Seek(entry.Offset, SeekOrigin.Begin);
        _stream.ReadExactly(target);
    }

    public void CopyTo(PackEntry entry, Stream target, byte[] buffer)
    {
        _stream.Seek(entry.Offset, SeekOrigin.Begin);
        long left = entry.Size;
        while (left > 0)
        {
            int part = (int)Math.Min(left, buffer.Length);
            _stream.ReadExactly(buffer, 0, part);
            target.Write(buffer, 0, part);
            left -= part;
        }
    }

    private static List<PackEntry> ReadDirectory(FileStream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.Length < PackFormat.HeaderSize) throw new PackException("Truncated pack header");
        uint magic = reader.ReadUInt32(), version = reader.ReadUInt32(), major = reader.ReadUInt32(), minor = reader.ReadUInt32();
        uint patch = reader.ReadUInt32(), flags = reader.ReadUInt32();
        long fileBase = reader.ReadInt64(), directory = reader.ReadInt64();
        if (magic != PackFormat.Magic || version != PackFormat.Version || major != PackFormat.EngineMajor || minor != PackFormat.EngineMinor
            || patch != PackFormat.EnginePatch || flags != PackFormat.RelativeFileBase)
            throw new PackException("Unsupported pack header");
        stream.Seek(directory, SeekOrigin.Begin);
        int count = reader.ReadInt32();
        var entries = new List<PackEntry>(count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            int length = reader.ReadInt32();
            if (length is <= 0 or >= PackFormat.MaxNameLength) throw new PackException("Invalid resource name length");
            string name = Encoding.UTF8.GetString(reader.ReadBytes(length)).TrimEnd('\0');
            long offset = reader.ReadInt64(), size = reader.ReadInt64();
            byte[] md5 = reader.ReadBytes(16);
            uint entryFlags = reader.ReadUInt32();
            if (!names.Add(name) || name.StartsWith('/') || name.Split('/').Contains(".."))
                throw new PackException("Duplicate or unsafe pack entry path: " + name);
            if (entryFlags != 0 || fileBase + offset + size > stream.Length)
                throw new PackException("Encrypted, removed or truncated pack entry: " + name);
            entries.Add(new PackEntry(name, fileBase + offset, size, md5));
        }
        return entries;
    }
}

/// <summary>Writes a pack as tools/godot_pack.py does, in the order the entries are added, one entry at a time.</summary>
public sealed class PackWriter
{
    /// <summary>Where entry offsets count from: the header and its padding.</summary>
    public const long DataStart = (PackFormat.HeaderSize + PackFormat.Alignment - 1) & -PackFormat.Alignment;
    private readonly FileStream _stream;
    private readonly long _base;
    private readonly List<PackEntry> _index = [];

    private PackWriter(FileStream stream, IEnumerable<PackEntry> written)
    {
        _stream = stream;
        _base = DataStart;
        _index.AddRange(written);
    }

    /// <summary>Carries on with a pack whose entries so far are <paramref name="written"/>, in order, with the stream at their end.</summary>
    public static PackWriter Resume(FileStream stream, IEnumerable<PackEntry> written) => new(stream, written);

    public PackWriter(FileStream stream)
    {
        _stream = stream;
        // The header holds the directory's offset, so it is written last.
        _stream.Write(new byte[PackFormat.HeaderSize]);
        Pad();
        _base = _stream.Position;
    }

    public int Count => _index.Count;

    /// <summary>The entries from this index on, with offsets from <see cref="DataStart"/>.</summary>
    public IEnumerable<PackEntry> EntriesFrom(int first) => _index.Skip(first);

    public void Add(string name, ReadOnlySpan<byte> data)
    {
        Begin(name, data.Length, MD5.HashData(data));
        _stream.Write(data);
    }

    public void Add(string name, ReadOnlySpan<byte> data, byte[] md5)
    {
        Begin(name, data.Length, md5);
        _stream.Write(data);
    }

    /// <summary>An entry copied as it is keeps the checksum its pack recorded for it.</summary>
    public void Copy(PackReader source, PackEntry entry, byte[] buffer)
    {
        Begin(entry.Name, entry.Size, entry.Md5);
        source.CopyTo(entry, _stream, buffer);
    }

    public void Finish()
    {
        Pad();
        long directory = _stream.Position;
        using (var writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(_index.Count);
            foreach (var entry in _index)
            {
                byte[] name = Encoding.UTF8.GetBytes(entry.Name);
                int padded = name.Length + (-name.Length & 3);
                writer.Write(padded);
                writer.Write(name);
                writer.Write(new byte[padded - name.Length]);
                writer.Write(entry.Offset);
                writer.Write(entry.Size);
                writer.Write(entry.Md5);
                writer.Write(0u);
            }
            writer.Flush();
            _stream.Seek(0, SeekOrigin.Begin);
            foreach (uint word in new[] { PackFormat.Magic, PackFormat.Version, PackFormat.EngineMajor, PackFormat.EngineMinor, PackFormat.EnginePatch, PackFormat.RelativeFileBase })
                writer.Write(word);
            writer.Write(_base);
            writer.Write(directory);
            writer.Flush();
        }
        _stream.Seek(0, SeekOrigin.End);
    }

    private void Begin(string name, long size, byte[] md5)
    {
        Pad();
        _index.Add(new PackEntry(name, _stream.Position - _base, size, md5));
    }

    private void Pad()
    {
        int padding = (int)(-_stream.Position & (PackFormat.Alignment - 1));
        if (padding > 0) _stream.Write(new byte[padding]);
    }
}
