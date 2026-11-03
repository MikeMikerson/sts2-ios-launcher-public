using System.Text;

namespace STS2Mobile.Platform.Tests;

/// <summary>Writes Godot 4 (format 3) packs the way the engine's PCKPacker lays them out.</summary>
internal static class TestPack
{
    public static byte[] Build(params (string Path, byte[] Data)[] files) => Make(files);

    public static byte[] Make((string Path, byte[] Data)[] files, uint format = 3, uint packFlags = 2, uint fileFlags = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x43504447u);
        writer.Write(format);
        writer.Write(4u);
        writer.Write(5u);
        writer.Write(1u);
        writer.Write(packFlags);
        long baseAt = stream.Position;
        writer.Write(0ul);
        writer.Write(0ul);
        for (int i = 0; i < 16; i++) writer.Write(0u);
        long fileBase = stream.Position;
        var offsets = new List<long>();
        foreach (var (_, data) in files)
        {
            offsets.Add(stream.Position - fileBase);
            writer.Write(data);
        }
        long directory = stream.Position;
        writer.Write((uint)files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            byte[] name = Encoding.UTF8.GetBytes(files[i].Path);
            int padded = (name.Length + 3) / 4 * 4;
            writer.Write((uint)padded);
            writer.Write(name);
            writer.Write(new byte[padded - name.Length]);
            writer.Write((ulong)offsets[i]);
            writer.Write((ulong)files[i].Data.Length);
            writer.Write(new byte[16]);
            writer.Write(fileFlags);
        }
        stream.Position = baseAt;
        writer.Write((ulong)fileBase);
        writer.Write((ulong)directory);
        return stream.ToArray();
    }

    public static List<string> Paths(byte[] pack)
    {
        using var reader = new BinaryReader(new MemoryStream(pack));
        reader.BaseStream.Position = 32;
        reader.BaseStream.Position = (long)reader.ReadUInt64();
        var paths = new List<string>();
        for (uint i = 0, count = reader.ReadUInt32(); i < count; i++)
        {
            paths.Add(Encoding.UTF8.GetString(reader.ReadBytes((int)reader.ReadUInt32())).TrimEnd('\0'));
            reader.ReadBytes(36);
        }
        return paths;
    }
}
