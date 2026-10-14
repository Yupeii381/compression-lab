namespace CompressionLab.Contracts;

public sealed record CodecParameter(string Key, string Label, int Default, int Min, int Max);
public sealed record CodecDescriptor(string Id, string Name, string Version, string Description,
    IReadOnlyList<CodecParameter> Parameters);

/// <summary>Instances must be stateless/thread-safe. The host supplies a fresh block per call.
/// Decode must enforce maxOutputBytes before allocating or writing output.</summary>
public interface ICompressionCodec
{
    CodecDescriptor Descriptor { get; }
    byte[] Encode(byte[] input, IReadOnlyDictionary<string, int> options, CancellationToken cancellationToken);
    byte[] Decode(byte[] input, IReadOnlyDictionary<string, int> options, int maxOutputBytes, CancellationToken cancellationToken);
}
