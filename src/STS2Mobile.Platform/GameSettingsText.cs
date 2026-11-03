using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace STS2Mobile.Platform;

/// <summary>
/// Turns the game's binary project settings into the text file the engine reads at startup through
/// <c>application/config/project_settings_override</c>. The rules are the
/// ones tools/build_full_game_pack.py uses to merge settings into a single main pack: host settings win and the
/// reporting SDK's settings are dropped.
/// </summary>
public static class GameSettingsText
{
    private const int ConfigVersion = 5;
    private const string CustomFeatures = "_custom_features";
    private static readonly string[] DroppedPrefixes = ["sentry/", "autoload/Sentry"];

    /// <param name="game">The game's project.binary.</param>
    /// <param name="host">The app's own project.binary.</param>
    /// <param name="valueText">Writes one setting's encoded value in the engine's text format.</param>
    public static string Write(byte[] game, byte[] host, Func<byte[], string> valueText)
    {
        var hostKeys = Read(host).Select(setting => setting.Key).ToHashSet(StringComparer.Ordinal);
        var text = new StringBuilder($"config_version={ConfigVersion}\n\n");
        foreach (var (key, value) in Read(game))
        {
            if (key == CustomFeatures || hostKeys.Contains(key)) continue;
            if (DroppedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal))) continue;
            // The engine's parser takes these unquoted. Anything else is a shape this was not written for.
            if (!key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '/' or '.' or '-'))
                throw new InvalidDataException($"Unsupported project setting name: {key}");
            text.Append(key).Append('=').Append(valueText(value)).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>The settings of a project.binary, in file order: "ECFG", a count, then length-prefixed keys and values.</summary>
    public static IReadOnlyList<KeyValuePair<string, byte[]>> Read(byte[] data)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data));
            if (!reader.ReadBytes(4).SequenceEqual("ECFG"u8.ToArray()))
                throw new InvalidDataException("Not a binary project settings file.");
            var settings = new List<KeyValuePair<string, byte[]>>();
            for (var remaining = reader.ReadUInt32(); remaining > 0; remaining--)
            {
                var key = Encoding.UTF8.GetString(ReadExact(reader));
                settings.Add(new(key, ReadExact(reader)));
            }
            if (reader.BaseStream.Position != data.Length)
                throw new InvalidDataException("Trailing bytes after the project settings.");
            return settings;
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("Truncated project settings.");
        }
    }

    private static byte[] ReadExact(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var bytes = reader.ReadBytes(length);
        if (length < 0 || bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }
}
