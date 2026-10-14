using System.Buffers.Binary;
using CompressionLab.Contracts;
using SevenZip;
namespace CompressionLab.Codecs.Lzma;

/// <summary>Adapter around unmodified official SDK sources in Vendor.
/// Block format: 5 SDK property bytes + little-endian Int64 original size + LZMA1 payload.</summary>
public sealed class LzmaCodec : ICompressionCodec
{
    public CodecDescriptor Descriptor => new("lzma", "LZMA1", "sdk26.04-adapter1", "Официальные C#-исходники LZMA SDK, отдельная DLL", [new("dictionaryMiB", "Словарь, МиБ", 4, 1, 16), new("fastBytes", "Fast bytes", 32, 5, 273)]);
    public byte[] Encode(byte[] input, IReadOnlyDictionary<string, int> options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int dictionary = options.GetValueOrDefault("dictionaryMiB", 4), fastBytes = options.GetValueOrDefault("fastBytes", 32);
        if (dictionary is < 1 or > 16 || fastBytes is < 5 or > 273) throw new ArgumentException("Некорректные параметры LZMA");
        var encoder = new SevenZip.Compression.LZMA.Encoder();
        encoder.SetCoderProperties([CoderPropID.DictionarySize, CoderPropID.NumFastBytes, CoderPropID.EndMarker], [dictionary * 1048576, fastBytes, false]);
        using var source = new CheckedInput(input, ct);
        using var output = new LimitedOutput(32 * 1048576, ct);
        encoder.WriteCoderProperties(output);
        Span<byte> length = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(length, input.Length); output.Write(length);
        encoder.Code(source, output, input.Length, -1, new Progress(ct));
        return output.ToArray();
    }
    public byte[] Decode(byte[] input, IReadOnlyDictionary<string, int> options, int maxOutputBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (input.Length < 13) throw new InvalidDataException("Заголовок LZMA обрезан");
        long size = BinaryPrimitives.ReadInt64LittleEndian(input.AsSpan(5, 8));
        uint dictionary = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(1, 4));
        if (size < 0 || size > maxOutputBytes || dictionary > 16 * 1048576 || input[0] >= 9 * 5 * 5) throw new InvalidDataException("Некорректный размер, словарь или свойства LZMA");
        using var source = new CheckedInput(input, ct); source.Position = 13;
        using var output = new LimitedOutput((int)size, ct);
        var decoder = new SevenZip.Compression.LZMA.Decoder();
        try
        {
            decoder.SetDecoderProperties(input.AsSpan(0, 5).ToArray());
            decoder.Code(source, output, input.Length - 13, size, new Progress(ct));
        }
        catch (DataErrorException e) { throw new InvalidDataException("Повреждённый поток LZMA", e); }
        catch (InvalidParamException e) { throw new InvalidDataException("Некорректные свойства LZMA", e); }
        if (output.Length != size) throw new InvalidDataException("Неверный размер LZMA");
        return output.ToArray();
    }
    private sealed class Progress(CancellationToken ct) : ICodeProgress
    { public void SetProgress(long inSize, long outSize) => ct.ThrowIfCancellationRequested(); }
    private sealed class CheckedInput(byte[] bytes, CancellationToken ct) : MemoryStream(bytes, false)
    {
        public override int Read(byte[] buffer, int offset, int count) { ct.ThrowIfCancellationRequested(); return base.Read(buffer, offset, count); }
        public override int ReadByte() { ct.ThrowIfCancellationRequested(); int b = base.ReadByte(); if (b < 0) throw new InvalidDataException("Поток LZMA обрезан"); return b; }
    }
    private sealed class LimitedOutput(int limit, CancellationToken ct) : MemoryStream
    {
        private void Check(int count) { ct.ThrowIfCancellationRequested(); if (Length + count > limit) throw new InvalidDataException("Превышен размер блока LZMA"); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
