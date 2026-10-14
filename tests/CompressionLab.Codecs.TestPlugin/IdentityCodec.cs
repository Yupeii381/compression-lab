using CompressionLab.Contracts;

namespace CompressionLab.Codecs.TestPlugin;

/// <summary>Integration fixture: an external plugin which must survive application updates.</summary>
public sealed class IdentityCodec : ICompressionCodec
{
    public CodecDescriptor Descriptor => new("test-identity", "Test identity", "1", "Update integration fixture", []);

    public byte[] Encode(byte[] input, IReadOnlyDictionary<string, int> options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return input.ToArray();
    }

    public byte[] Decode(byte[] input, IReadOnlyDictionary<string, int> options, int maxOutputBytes, CancellationToken cancellationToken)
    {
        if (input.Length > maxOutputBytes) throw new InvalidDataException("Output bound exceeded");
        return Encode(input, options, cancellationToken);
    }
}
