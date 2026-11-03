using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace STS2Mobile.GamePack;

/// <summary>One texture to convert, as tools/transcode_textures_astc.py's plan names it.</summary>
internal sealed record PlannedTexture(PackEntry Import, PackEntry Source, string OutputName, byte[] ImportData);

/// <summary>How the conversion uses the cores and memory. <see cref="Default"/> is the setting that measured fastest over every texture on a phone.</summary>
/// <param name="Workers">Textures converted side by side, one thread each.</param>
/// <param name="LargeThreads">Threads for one large texture, converted alone.</param>
/// <param name="BatchBytes">How much source data goes through memory at once, and between saving the progress.</param>
/// <param name="LargeBytes">A source this long counts as large.</param>
public sealed record TextureConversionOptions(int Workers, int LargeThreads, long BatchBytes, long LargeBytes)
{
    private const long Megabyte = 1024 * 1024;

    public static TextureConversionOptions Default { get; } = new(Environment.ProcessorCount, Environment.ProcessorCount, 16 * Megabyte, 4 * Megabyte);

    /// <summary>"workers,large threads,batch MB,large MB" for measuring on the phone with no rebuild, or null.</summary>
    public static TextureConversionOptions? Parse(string? text)
    {
        var parts = text?.Split(',');
        if (parts is not { Length: 4 } || !parts.All(part => int.TryParse(part, out int value) && value > 0)) return null;
        int[] values = parts.Select(int.Parse).ToArray();
        return new(values[0], values[1], values[2] * Megabyte, values[3] * Megabyte);
    }
}

/// <param name="Total">The textures in the pack.</param>
/// <param name="Resumed">Those an earlier run had finished.</param>
/// <param name="AlreadyDone">An earlier run finished them all, and nothing happened.</param>
public sealed record TextureConversionResult(int Total, int Resumed, bool AlreadyDone, TimeSpan Elapsed, TimeSpan Reading, TimeSpan Encoding, TimeSpan Writing, long OutputBytes);

/// <summary>
/// Makes the pack of converted textures from the game's pack, in batches, resuming after an interruption.
/// It holds what <c>build_texture_pack</c> in tools/build_full_game_pack.py writes, byte for byte.
/// </summary>
/// <param name="keptMustExist">A tested version's kept textures are all in its pack, so one that is not stops the conversion. An untested build keeps those it has.</param>
public sealed class TextureConversion(string gameFolder, string packSha256, IReadOnlyCollection<string> keptOriginal, TextureConversionOptions? options = null, Action<int, int>? progress = null, bool keptMustExist = true)
{
    public const string OutputName = "phone-textures.pck";
    public const string JournalName = "phone-textures.journal";
    /// <summary>Names what makes the bytes, so a new encoder starts again. Part of <see cref="GamePackPreparation.StepsId"/>, so changing it also makes a new pack.</summary>
    public const string Encoder = "astcenc-5.3.0-bcdec-v1";
    private const string Import = ".import";
    private const string ReportingAddon = "addons/sentry/";
    private static readonly Regex Remap = new("""^path\.(?:s3tc|bptc)="res://(\.godot/imported/[^"\n]+)\.(s3tc|bptc)\.ctex"$""", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private readonly TextureConversionOptions _options = options ?? TextureConversionOptions.Default;

    /// <summary>The textures to convert, in the order of their sorted remap files. Every remap is read, which takes moments.</summary>
    internal static List<PlannedTexture> Plan(PackReader game, IReadOnlyCollection<string> keptOriginal, bool keptMustExist = true)
    {
        var names = game.Entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var kept = keptOriginal.ToHashSet(StringComparer.Ordinal);
        var textures = new List<PlannedTexture>();
        foreach (var import in game.Entries.Where(entry => entry.Name.EndsWith(Import, StringComparison.Ordinal) && !entry.Name.StartsWith(ReportingAddon, StringComparison.Ordinal))
                     .OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            // As Python's read_text reads it: universal newlines.
            string text = Encoding.UTF8.GetString(game.Read(import)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var matches = Remap.Matches(text);
            if (matches.Count == 0) continue;
            if (matches.Count > 1) throw new PackException("A remap names more than one block-compressed file: " + import.Name);
            string stem = matches[0].Groups[1].Value, variant = matches[0].Groups[2].Value;
            string source = $"{stem}.{variant}.ctex", output = $"{stem}.astc.ctex";
            if (!names.TryGetValue(source, out var sourceEntry) || names.ContainsKey(output))
                throw new PackException("A remap names a texture outside the game's files: " + import.Name);
            if (kept.Remove(source)) continue;
            var match = matches[0];
            byte[] rewritten = Encoding.UTF8.GetBytes($"{text[..match.Index]}path.astc=\"res://{output}\"{text[(match.Index + match.Length)..]}");
            textures.Add(new PlannedTexture(import, sourceEntry, output, rewritten));
        }
        if (textures.Count == 0) throw new PackException("The game has no textures to convert.");
        if (kept.Count != 0 && keptMustExist) throw new PackException("The game's profile keeps a texture this game does not have: " + kept.First());
        return textures;
    }

    /// <summary>The overlay's list, as Python's <c>json.dumps</c> writes it, which the pack's bytes depend on.</summary>
    internal static byte[] Manifest(IReadOnlyList<PlannedTexture> textures, string packSha256)
    {
        var text = new StringBuilder();
        text.Append("{\"profile\": ").Append(Quote(GamePackBuilder.TextureProfile)).Append(", \"assetPackSha256\": ").Append(Quote(packSha256)).Append(", \"textures\": [");
        for (int index = 0; index < textures.Count; index++)
        {
            var texture = textures[index];
            if (index > 0) text.Append(", ");
            text.Append("{\"source\": ").Append(Quote(texture.Source.Name)).Append(", \"import\": ").Append(Quote(texture.Import.Name)).Append(", \"output\": ").Append(Quote(texture.OutputName)).Append('}');
        }
        return Encoding.UTF8.GetBytes(text.Append("]}").ToString());
    }

    private static string Quote(string name)
    {
        // Nothing on v0.111.0 needs escaping. One that did would otherwise change the pack without saying so.
        foreach (char letter in name)
            if (letter is '"' or '\\' or < ' ' or > '~') throw new PackException("A texture name needs escaping: " + name);
        return "\"" + name + "\"";
    }

    public TextureConversionResult Run(string gamePack)
    {
        var clock = Stopwatch.StartNew();
        string output = Path.Combine(gameFolder, OutputName);
        string partial = output + ".partial";
        string journalPath = Path.Combine(gameFolder, JournalName);
        string header = $"{Encoder} {packSha256}";

        using var game = new PackReader(gamePack);
        var textures = Plan(game, keptOriginal, keptMustExist);
        var written = ReadJournal(journalPath, header);
        // The finished file only ever appears by a rename, so its being there with this journal means it is whole.
        if (written != null && File.Exists(output))
            return new TextureConversionResult(textures.Count, textures.Count, true, clock.Elapsed, default, default, default, new FileInfo(output).Length);

        File.Delete(output);
        int resumed = written == null ? 0 : Resumable(written, textures, partial);
        if (resumed == 0)
        {
            written = [];
            File.Delete(partial);
            File.Delete(journalPath);
            File.WriteAllText(journalPath, header + "\n");
        }

        using var stream = new FileStream(partial, resumed == 0 ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.None, PackFormat.CopyBufferBytes);
        PackWriter writer;
        if (resumed == 0)
        {
            writer = new PackWriter(stream);
            writer.Add(GamePackBuilder.TextureManifest, Manifest(textures, packSha256));
        }
        else
        {
            var last = written![^1];
            stream.SetLength(PackWriter.DataStart + last.Offset + last.Size);
            stream.Seek(0, SeekOrigin.End);
            writer = PackWriter.Resume(stream, written);
            // Only whole textures stay: a torn last line or half a texture's lines would spoil what comes after them.
            File.WriteAllText(journalPath + ".partial", header + "\n" + string.Concat(written.Select(JournalLine)));
            File.Move(journalPath + ".partial", journalPath, overwrite: true);
        }

        var times = new Times();
        using (var journal = new FileStream(journalPath, FileMode.Append, FileAccess.Write, FileShare.None))
        using (var batches = new Batches(_options, textures, game, writer, resumed == 0 ? 0 : writer.Count, stream, journal, times))
        {
            progress?.Invoke(resumed, textures.Count);
            for (int done = resumed; done < textures.Count; progress?.Invoke(done, textures.Count))
                done = batches.Next(done);
        }
        writer.Finish();
        stream.Flush(flushToDisk: true);
        long bytes = stream.Length;
        stream.Dispose();
        File.Move(partial, output);
        return new TextureConversionResult(textures.Count, resumed, false, clock.Elapsed, times.Reading.Elapsed, times.Encoding.Elapsed, times.Writing.Elapsed, bytes);
    }

    private static string JournalLine(PackEntry entry) => string.Create(CultureInfo.InvariantCulture, $"{entry.Name}\t{entry.Offset}\t{entry.Size}\t{Convert.ToHexStringLower(entry.Md5)}\n");

    /// <summary>The entries the journal lists, or null with no journal of this encoder and pack. A line counts only when it is whole.</summary>
    private static List<PackEntry>? ReadJournal(string path, string header)
    {
        if (!File.Exists(path)) return null;
        string[] lines = File.ReadAllText(path).Split('\n');
        if (lines[0] != header || lines.Length < 2) return null;
        var entries = new List<PackEntry>(lines.Length);
        foreach (string line in lines.AsSpan(1, lines.Length - 2))
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 4
                || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long offset)
                || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long size))
                return null;
            byte[] md5;
            try { md5 = Convert.FromHexString(fields[3]); }
            catch (FormatException) { return null; }
            entries.Add(new PackEntry(fields[0], offset, size, md5));
        }
        return entries;
    }

    /// <summary>How many textures the journal and partial file hold whole, trimming the list to them. Zero means starting again.</summary>
    private static int Resumable(List<PackEntry> written, List<PlannedTexture> textures, string partial)
    {
        // The list's first entry, then a remap and a texture for each.
        int done = Math.Min((written.Count - 1) / 2, textures.Count);
        if (done <= 0 || !File.Exists(partial) || written[0].Name != GamePackBuilder.TextureManifest) return 0;
        written.RemoveRange(1 + 2 * done, written.Count - 1 - 2 * done);
        for (int index = 0; index < done; index++)
            if (written[1 + 2 * index].Name != textures[index].Import.Name || written[2 + 2 * index].Name != textures[index].OutputName) return 0;
        var last = written[^1];
        return new FileInfo(partial).Length >= PackWriter.DataStart + last.Offset + last.Size ? done : 0;
    }

    private sealed class Times
    {
        public readonly Stopwatch Reading = new(), Encoding = new(), Writing = new();
    }

    /// <summary>Runs one batch at a time: read in order, convert side by side, write in order, then save the progress.</summary>
    private sealed class Batches : IDisposable
    {
        // Room for the encoder's working arrays: a secondary thread's default stack on iOS is small.
        private const int WorkerStackBytes = 1024 * 1024;
        private readonly TextureConversionOptions _options;
        private readonly List<PlannedTexture> _textures;
        private readonly PackReader _game;
        private readonly PackWriter _writer;
        private readonly FileStream _pack, _journal;
        private readonly Times _times;
        private readonly TextureEncoder[] _workers;
        private readonly TextureEncoder _large;
        private readonly byte[] _source, _output;
        private int _journaled;

        public Batches(TextureConversionOptions options, List<PlannedTexture> textures, PackReader game, PackWriter writer, int journaled, FileStream pack, FileStream journal, Times times)
        {
            (_options, _textures, _game, _writer, _journaled, _pack, _journal, _times) = (options, textures, game, writer, journaled, pack, journal, times);
            _workers = Enumerable.Range(0, Math.Max(1, options.Workers)).Select(_ => new TextureEncoder(1)).ToArray();
            _large = new TextureEncoder(Math.Max(1, options.LargeThreads));
            long room = Math.Max(options.BatchBytes, textures.Max(texture => texture.Source.Size));
            _source = new byte[room];
            // A DXT1 texture doubles. Pages nothing writes to cost nothing.
            _output = new byte[room * 2];
        }

        public void Dispose()
        {
            foreach (var worker in _workers) worker.Dispose();
            _large.Dispose();
        }

        /// <returns>The index after the last texture of this batch.</returns>
        public int Next(int first)
        {
            int end = first + 1;
            bool large = _textures[first].Source.Size >= _options.LargeBytes;
            if (!large)
            {
                long bytes = _textures[first].Source.Size;
                while (end < _textures.Count && _textures[end].Source.Size < _options.LargeBytes && bytes + _textures[end].Source.Size <= _options.BatchBytes)
                    bytes += _textures[end++].Source.Size;
            }

            int count = end - first;
            var sources = new (int Start, int Length)[count];
            var outputs = new (int Start, int Length, byte[]? Md5)[count];
            _times.Reading.Start();
            int sourceEnd = 0, outputEnd = 0;
            for (int index = 0; index < count; index++)
            {
                var entry = _textures[first + index].Source;
                _game.ReadInto(entry, _source.AsSpan(sourceEnd, (int)entry.Size));
                sources[index] = (sourceEnd, (int)entry.Size);
                sourceEnd += (int)entry.Size;
                int length = (int)TextureEncoder.OutputBytes(_source.AsSpan(sources[index].Start, sources[index].Length));
                outputs[index] = (outputEnd, length, null);
                outputEnd += length;
            }
            _times.Reading.Stop();

            _times.Encoding.Start();
            if (large) ConvertOne(_large, sources, outputs, 0);
            else ConvertSideBySide(sources, outputs);
            _times.Encoding.Stop();

            _times.Writing.Start();
            for (int index = 0; index < count; index++)
            {
                var texture = _textures[first + index];
                var (start, length, md5) = outputs[index];
                _writer.Add(texture.Import.Name, texture.ImportData);
                _writer.Add(texture.OutputName, _output.AsSpan(start, length), md5!);
            }
            // The data first: a journal line promises data that is on disk.
            _pack.Flush(flushToDisk: true);
            _journal.Write(Encoding.UTF8.GetBytes(string.Concat(_writer.EntriesFrom(_journaled).Select(JournalLine))));
            _journal.Flush(flushToDisk: true);
            _journaled = _writer.Count;
            _times.Writing.Stop();
            return end;
        }

        private void ConvertOne(TextureEncoder encoder, (int Start, int Length)[] sources, (int Start, int Length, byte[]? Md5)[] outputs, int index)
        {
            var (start, length) = sources[index];
            var result = _output.AsSpan(outputs[index].Start, outputs[index].Length);
            encoder.Convert(_source.AsSpan(start, length), result);
            outputs[index].Md5 = MD5.HashData(result);
        }

        private void ConvertSideBySide((int Start, int Length)[] sources, (int Start, int Length, byte[]? Md5)[] outputs)
        {
            int next = -1;
            Exception? failure = null;
            var threads = _workers.Select(encoder => new Thread(() =>
            {
                try
                {
                    for (int index = Interlocked.Increment(ref next); index < sources.Length && Volatile.Read(ref failure) == null; index = Interlocked.Increment(ref next))
                        ConvertOne(encoder, sources, outputs, index);
                }
                catch (Exception error)
                {
                    Interlocked.CompareExchange(ref failure, error, null);
                }
            }, WorkerStackBytes) { IsBackground = true, Name = "Texture conversion" }).ToList();
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join();
            if (failure != null) throw new PackException(failure.Message);
        }
    }
}
