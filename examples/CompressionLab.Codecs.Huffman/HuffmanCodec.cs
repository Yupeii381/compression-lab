using System.Buffers.Binary;
using CompressionLab.Contracts;

namespace CompressionLab.Codecs.Huffman;

/// <summary>Teaching example: static Huffman coding of bytes, independently for each block.</summary>
public sealed class HuffmanCodec : ICompressionCodec
{
    private const int AlphabetSize = 256;
    private const int HeaderSize = 4 + AlphabetSize * sizeof(int);
    private static ReadOnlySpan<byte> Magic => "HUF1"u8;

    public CodecDescriptor Descriptor => new("huffman-example", "Хаффман (учебный)", "1",
        "Кодирование байтов по частотам. Таблица частот сохраняется в каждом блоке; на коротких данных возможен рост размера.", []);

    public byte[] Encode(byte[] input, IReadOnlyDictionary<string, int> options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frequencies = new int[AlphabetSize];
        for (var index = 0; index < input.Length; index++)
        {
            CheckCancellation(index, cancellationToken);
            frequencies[input[index]]++;
        }

        var tree = BuildTree(frequencies);
        var codes = BuildCodes(tree);
        var bitCount = CountBits(frequencies, codes);
        var result = new byte[EncodedLength(bitCount)];
        Magic.CopyTo(result);
        for (var symbol = 0; symbol < AlphabetSize; symbol++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4 + symbol * sizeof(int)), frequencies[symbol]);
        }

        long bitPosition = 0;
        for (var index = 0; index < input.Length; index++)
        {
            CheckCancellation(index, cancellationToken);
            foreach (var bit in codes[input[index]])
            {
                if (bit != 0)
                {
                    result[HeaderSize + (int)(bitPosition / 8)] |= (byte)(1 << (7 - (int)(bitPosition % 8)));
                }

                bitPosition++;
            }
        }

        return result;
    }

    public byte[] Decode(byte[] input, IReadOnlyDictionary<string, int> options, int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxOutputBytes < 0 || input.Length < HeaderSize || !input.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Некорректный заголовок Хаффмана");
        }

        var frequencies = new int[AlphabetSize];
        long outputLength = 0;
        for (var symbol = 0; symbol < AlphabetSize; symbol++)
        {
            var frequency = BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(4 + symbol * sizeof(int)));
            if (frequency < 0)
            {
                throw new InvalidDataException("Отрицательная частота символа");
            }

            frequencies[symbol] = frequency;
            outputLength += frequency;
        }

        // Validate the declared output BEFORE allocating it.
        if (outputLength > maxOutputBytes)
        {
            throw new InvalidDataException("Превышен допустимый размер распакованного блока");
        }

        var tree = BuildTree(frequencies);
        var bitCount = CountBits(frequencies, BuildCodes(tree));
        if (input.Length != EncodedLength(bitCount) ||
            (bitCount % 8 != 0 && (input[^1] & ((1 << (8 - (int)(bitCount % 8))) - 1)) != 0))
        {
            throw new InvalidDataException("Неверная длина или завершающие биты блока Хаффмана");
        }

        var result = new byte[(int)outputLength];
        var remaining = frequencies.ToArray();
        long bitPosition = 0;
        for (var index = 0; index < result.Length; index++)
        {
            CheckCancellation(index, cancellationToken);
            var node = tree!;
            while (!node.IsLeaf)
            {
                if (bitPosition >= bitCount)
                {
                    throw new InvalidDataException("Оборван код символа Хаффмана");
                }

                var bit = (input[HeaderSize + (int)(bitPosition / 8)] >> (7 - (int)(bitPosition % 8))) & 1;
                node = bit == 0 ? node.Left! : node.Right!;
                bitPosition++;
            }

            if (--remaining[node.Symbol] < 0)
            {
                throw new InvalidDataException("Данные не соответствуют таблице частот");
            }

            result[index] = (byte)node.Symbol;
        }

        if (bitPosition != bitCount)
        {
            throw new InvalidDataException("Лишние коды в блоке Хаффмана");
        }

        return result;
    }

    private static Node? BuildTree(int[] frequencies)
    {
        var queue = new PriorityQueue<Node, (long Frequency, int MinSymbol)>();
        for (var symbol = 0; symbol < AlphabetSize; symbol++)
        {
            if (frequencies[symbol] > 0)
            {
                queue.Enqueue(new Node(symbol, null, null), (frequencies[symbol], symbol));
            }
        }

        while (queue.Count > 1)
        {
            queue.TryDequeue(out var left, out var leftPriority);
            queue.TryDequeue(out var right, out var rightPriority);
            // A fixed tie-breaker is essential: decoder must reconstruct the SAME tree.
            var priority = (leftPriority.Frequency + rightPriority.Frequency,
                Math.Min(leftPriority.MinSymbol, rightPriority.MinSymbol));
            queue.Enqueue(new Node(-1, left, right), priority);
        }

        return queue.Count == 0 ? null : queue.Dequeue();
    }

    private static byte[][] BuildCodes(Node? tree)
    {
        var codes = new byte[AlphabetSize][];
        if (tree is not null)
        {
            Visit(tree, []);
        }

        return codes;

        void Visit(Node node, byte[] prefix)
        {
            if (node.IsLeaf)
            {
                // One-symbol blocks need no bits: the frequency already gives the repetition count.
                codes[node.Symbol] = prefix;
                return;
            }

            Visit(node.Left!, [.. prefix, 0]);
            Visit(node.Right!, [.. prefix, 1]);
        }
    }

    private static long CountBits(int[] frequencies, byte[][] codes) =>
        Enumerable.Range(0, AlphabetSize).Sum(symbol => (long)frequencies[symbol] * (codes[symbol]?.Length ?? 0));

    private static int EncodedLength(long bitCount)
    {
        var length = HeaderSize + (bitCount + 7) / 8;
        return length <= int.MaxValue ? (int)length : throw new InvalidDataException("Слишком большой блок Хаффмана");
    }

    private static void CheckCancellation(int index, CancellationToken token)
    {
        if ((index & 4095) == 0)
        {
            token.ThrowIfCancellationRequested();
        }
    }

    private sealed record Node(int Symbol, Node? Left, Node? Right)
    {
        public bool IsLeaf => Left is null;
    }
}
