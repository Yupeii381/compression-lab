using System.IO.Compression;
using CompressionLab.Contracts;

namespace CompressionLab.Codecs.BuiltIn;

public abstract class StreamCodec : ICompressionCodec
{
    public abstract CodecDescriptor Descriptor { get; }
    protected abstract Stream Compressor(Stream output, CompressionLevel level);
    protected abstract Stream Decompressor(Stream input);
    public byte[] Encode(byte[] input, IReadOnlyDictionary<string, int> options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var level = options.GetValueOrDefault("level", 2) switch
        { 0 => CompressionLevel.NoCompression, 1 => CompressionLevel.Fastest, 2 => CompressionLevel.Optimal, 3 => CompressionLevel.SmallestSize, _ => throw new ArgumentException("Уровень: 0–3") };
        using var output = new MemoryStream();
        using (var compressor = Compressor(output, level)) compressor.Write(input);
        ct.ThrowIfCancellationRequested();
        return output.ToArray();
    }
    public byte[] Decode(byte[] input, IReadOnlyDictionary<string, int> options, int maxOutputBytes, CancellationToken ct)
    {
        using var source = new MemoryStream(input, false);
        using var decoder = Decompressor(source);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = decoder.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            if (output.Length + count > maxOutputBytes) throw new InvalidDataException("Превышен размер распакованного блока");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    protected static CodecDescriptor Describe(string id, string name) => new(id, name, "1", ".NET System.IO.Compression", [new("level", "Уровень (0–3)", 2, 0, 3)]);
}
public sealed class GzipCodec : StreamCodec
{
    public override CodecDescriptor Descriptor => Describe("gzip", "Gzip");
    protected override Stream Compressor(Stream output, CompressionLevel level) => new GZipStream(output, level, true);
    protected override Stream Decompressor(Stream input) => new GZipStream(input, CompressionMode.Decompress);
}
public sealed class BrotliCodec : StreamCodec
{
    public override CodecDescriptor Descriptor => Describe("brotli", "Brotli");
    protected override Stream Compressor(Stream output, CompressionLevel level) => new BrotliStream(output, level, true);
    protected override Stream Decompressor(Stream input) => new BrotliStream(input, CompressionMode.Decompress);
}
public sealed class DeflateCodec : StreamCodec
{
    public override CodecDescriptor Descriptor => Describe("deflate", "DEFLATE");
    protected override Stream Compressor(Stream output, CompressionLevel level) => new DeflateStream(output, level, true);
    protected override Stream Decompressor(Stream input) => new DeflateStream(input, CompressionMode.Decompress);
}
