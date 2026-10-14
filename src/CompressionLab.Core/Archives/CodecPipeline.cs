namespace CompressionLab.Core.Archives;

internal sealed class CodecPipeline(CodecRegistry registry, CodecStep[] steps)
{
    public EncodedBlock Encode(byte[] original, CancellationToken cancellationToken)
    {
        var encoded = original;
        var lengths = new int[steps.Length + 1];
        lengths[0] = original.Length;

        for (var index = 0; index < steps.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = steps[index];
            encoded = registry.Get(step.Id).Encode(encoded, step.Options, cancellationToken);

            if (encoded.Length > ArchiveEngine.MaxIntermediate)
            {
                throw new InvalidDataException("Промежуточный блок больше 32 МиБ");
            }

            lengths[index + 1] = encoded.Length;
        }

        return new(encoded, lengths);
    }

    public byte[] Decode(EncodedBlock block, CancellationToken cancellationToken)
    {
        var restored = block.Payload;

        for (var index = steps.Length - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = steps[index];
            restored = registry.Get(step.Id).Decode(restored, step.Options, block.StageLengths[index], cancellationToken);

            if (restored.Length != block.StageLengths[index])
            {
                throw new InvalidDataException("Неверный размер восстановленного блока");
            }
        }

        return restored;
    }
}
