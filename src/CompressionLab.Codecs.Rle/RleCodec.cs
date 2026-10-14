using CompressionLab.Contracts;
namespace CompressionLab.Codecs.Rle;

/// <summary>Byte pairs (count 1..255, value). A standalone plugin example.</summary>
public sealed class RleCodec : ICompressionCodec
{
    public CodecDescriptor Descriptor => new("rle", "RLE", "1", "Серии байтов; отдельная DLL, пример собственного кодека", []);
    public byte[] Encode(byte[] input, IReadOnlyDictionary<string, int> options, CancellationToken ct)
    {
        using var output = new MemoryStream();
        for (int i = 0; i < input.Length;)
        {
            ct.ThrowIfCancellationRequested();
            int count = 1;
            while (count < 255 && i + count < input.Length && input[i + count] == input[i]) count++;
            output.WriteByte((byte)count); output.WriteByte(input[i]); i += count;
        }
        return output.ToArray();
    }
    public byte[] Decode(byte[] input, IReadOnlyDictionary<string, int> options, int maxOutputBytes, CancellationToken ct)
    {
        if (input.Length % 2 != 0) throw new InvalidDataException("Повреждённый RLE");
        using var output = new MemoryStream();
        for (int i = 0; i < input.Length; i += 2)
        {
            ct.ThrowIfCancellationRequested();
            int count = input[i];
            if (count == 0 || output.Length + count > maxOutputBytes) throw new InvalidDataException("Недопустимый размер RLE");
            for (int j = 0; j < count; j++) output.WriteByte(input[i + 1]);
        }
        return output.ToArray();
    }
}
