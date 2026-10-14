using System.Security.Cryptography;
using CompressionLab.Codecs.BuiltIn;
using CompressionLab.Core;

var folder = Path.Combine(Path.GetTempPath(), "CompressionLab-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
int assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
void Reject(Action action, string message)
{
    bool rejected = false;
    try { action(); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException or IOException or OperationCanceledException) { rejected = true; }
    Check(rejected, message);
}
try
{
    var registry = new CodecRegistry(); registry.Register(new GzipCodec()); registry.Register(new BrotliCodec()); registry.Register(new DeflateCodec());
    registry.LoadPlugins(Path.Combine(AppContext.BaseDirectory, "plugins"));
    Check(registry.Diagnostics.Count == 0, string.Join(";", registry.Diagnostics));
    Check(registry.Descriptors.Count() == 5, "Внешние RLE и LZMA не загрузились");
    Reject(() => registry.Register(new GzipCodec()), "Дубликат ID принят");
    var engine = new ArchiveEngine(registry);
    var random = new byte[180003]; RandomNumberGenerator.Fill(random);
    byte[][] fixtures = [[], [0], Enumerable.Repeat((byte)65, 140000).ToArray(), Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), random];
    foreach (var descriptor in registry.Descriptors)
    {
        var codec = registry.Get(descriptor.Id);
        var options = descriptor.Parameters.ToDictionary(p => p.Key, p => p.Default);
        foreach (var bytes in fixtures)
        {
            var encoded = codec.Encode(bytes, options, default);
            var decoded = codec.Decode(encoded, options, bytes.Length, default);
            Check(bytes.AsSpan().SequenceEqual(decoded), descriptor.Id + " roundtrip");
            if (bytes.Length > 0) Reject(() => codec.Decode(encoded, options, bytes.Length - 1, default), descriptor.Id + " output limit ignored");
            var source = Path.Combine(folder, Guid.NewGuid() + ".bin"); File.WriteAllBytes(source, bytes);
            var archive = source + ".clcs"; var restored = source + ".out";
            var result = engine.Compress(source, archive, "sample.bin", [new(descriptor.Id, options)], 65536, 2, 1, default);
            Check(result.Verified && result.ArchiveBytes == new FileInfo(archive).Length && result.EncodeSamplesMs.Length == 2, "Метрики контейнера");
            Check(result.SavedPercent is null == (bytes.Length == 0), "Пустой файл: процент");
            engine.Restore(archive, restored, default);
            Check(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(restored)), "Archive roundtrip: " + descriptor.Id);
            Reject(() => engine.Restore(archive, restored, default), "Существующий файл перезаписан");
            Check(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(restored)), "Существующий файл удалён");
            Reject(() => engine.Compress(source, archive, "sample", [new(descriptor.Id, options)], 65536, 1, 0, default), "Существующий архив перезаписан");
            Check(File.Exists(archive), "Существующий архив удалён");
        }
    }
    var chainSource = Path.Combine(folder, "chain"); File.WriteAllBytes(chainSource, random);
    var chainArchive = chainSource + ".clcs";
    var steps = new CodecStep[] { new("rle", []), new("lzma", []), new("brotli", []) };
    engine.Compress(chainSource, chainArchive, "chain", steps, 65536, 1, 0, default);
    engine.Restore(chainArchive, chainSource + ".out", default);
    Check(random.AsSpan().SequenceEqual(File.ReadAllBytes(chainSource + ".out")), "RLE → LZMA → Brotli");
    var archiveBytes = File.ReadAllBytes(chainArchive);
    foreach (var altered in new[] { archiveBytes[..^1], archiveBytes.Concat(new byte[] { 0 }).ToArray(), archiveBytes.Select((b, i) => i == 0 ? (byte)0 : b).ToArray() })
    {
        var corrupt = Path.Combine(folder, Guid.NewGuid() + ".clcs"); File.WriteAllBytes(corrupt, altered);
        Reject(() => engine.Restore(corrupt, corrupt + ".out", default), "Повреждённый архив принят");
        Check(!File.Exists(corrupt + ".out"), "Частичный результат остался");
    }
    // Change the expected hash without modifying JSON structure.
    var hashCorrupt = archiveBytes.ToArray();
    var hash = System.Text.Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(random)));
    var hashOffset = hashCorrupt.AsSpan().IndexOf(hash); Check(hashOffset >= 0, "Hash not found");
    hashCorrupt[hashOffset] = hashCorrupt[hashOffset] == 'A' ? (byte)'B' : (byte)'A';
    var hashPath = Path.Combine(folder, "hash.clcs"); File.WriteAllBytes(hashPath, hashCorrupt);
    Reject(() => engine.Restore(hashPath, hashPath + ".out", default), "SHA-256 mismatch accepted");
    Check(!File.Exists(hashPath + ".out"), "Failed checksum output retained");
    Reject(() => registry.Validate(new("gzip", new() { { "level", 9 } })), "Invalid options accepted");
    Reject(() => registry.Validate(new("gzip", [], "other-version")), "Wrong version accepted");
    Reject(() => registry.Validate(new("not-installed", [])), "Missing codec accepted");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    Reject(() => engine.Compress(chainSource, chainSource + ".cancelled", "cancel", steps, 65536, 1, 0, cancelled.Token), "Cancellation ignored");
    Check(!File.Exists(chainSource + ".cancelled"), "Cancelled output retained");
    var lzma = registry.Get("lzma");
    var bomb = new byte[13]; bomb[4] = 127;
    Reject(() => lzma.Decode(bomb, new Dictionary<string, int>(), 1, default), "Unbounded LZMA dictionary accepted");
    ApplicationTests.Run(registry, folder, Check, Reject);
    UpdateTests.Run(folder, Check, Reject);
    HuffmanTests.Run(folder, Check, Reject);
    Console.WriteLine($"PASS: {assertions} проверок; кодеки, архивы, совместимость, очередь, хранение и настройки запуска.");
}
finally
{
    // Only remove the unique test directory created above, under the system temp directory.
    var target = Path.GetFullPath(folder);
    if (target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).StartsWith("CompressionLab-test-", StringComparison.Ordinal)) Directory.Delete(target, true);
}
