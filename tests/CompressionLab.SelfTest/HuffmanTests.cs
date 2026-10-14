using System.Buffers.Binary;
using System.Security.Cryptography;
using CompressionLab.Core;

internal static class HuffmanTests
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> reject)
    {
        var registry = new CodecRegistry();
        registry.LoadPlugins(Path.Combine(AppContext.BaseDirectory, "examples"));
        check(registry.Diagnostics.Count == 0 && registry.Descriptors.Single().Id == "huffman-example", "Huffman example DLL discovery");
        var codec = registry.Get("huffman-example");
        var random = new byte[100_003];
        RandomNumberGenerator.Fill(random);
        byte[][] samples = [[], [1], Enumerable.Repeat((byte)65, 150_000).ToArray(),
            Enumerable.Range(0, 256).Select(value => (byte)value).ToArray(), random];
        foreach (var sample in samples)
        {
            var encoded = codec.Encode(sample, new Dictionary<string, int>(), default);
            check(sample.AsSpan().SequenceEqual(codec.Decode(encoded, new Dictionary<string, int>(), sample.Length, default)),
                "Huffman roundtrip");
            if (sample.Length > 0)
            {
                reject(() => codec.Decode(encoded, new Dictionary<string, int>(), sample.Length - 1, default), "Huffman output limit ignored");
            }

            reject(() => codec.Decode(encoded[..^1], new Dictionary<string, int>(), sample.Length, default), "Truncated Huffman accepted");
            reject(() => codec.Decode([.. encoded, 0], new Dictionary<string, int>(), sample.Length, default), "Trailing Huffman data accepted");
        }

        var malformed = codec.Encode([1, 2, 2], new Dictionary<string, int>(), default);
        malformed[^1] |= 1;
        reject(() => codec.Decode(malformed, new Dictionary<string, int>(), 3, default), "Nonzero Huffman padding accepted");
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(4), -1);
        reject(() => codec.Decode(malformed, new Dictionary<string, int>(), 3, default), "Negative Huffman frequency accepted");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        reject(() => codec.Encode(random, new Dictionary<string, int>(), cancelled.Token), "Huffman encode cancellation ignored");
        reject(() => codec.Decode([], new Dictionary<string, int>(), 0, cancelled.Token), "Huffman decode cancellation ignored");

        var source = Path.Combine(folder, "huffman-example.txt");
        File.WriteAllBytes(source, random);
        var engine = new ArchiveEngine(registry);
        engine.Compress(source, source + ".clcs", "sample.txt", [new("huffman-example", [])], 65536, 1, 0, default);
        engine.Restore(source + ".clcs", source + ".restored", default);
        check(random.AsSpan().SequenceEqual(File.ReadAllBytes(source + ".restored")), "Huffman multi-block archive roundtrip");
    }
}
