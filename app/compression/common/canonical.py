import heapq

def canonical(lengths):
    code, previous, codes = 0, 0, {}
    for length, symbol in sorted((n, s) for s, n in enumerate(lengths) if n):
        code <<= length - previous
        codes[symbol] = (code, length)
        code += 1
        previous = length
    return codes

def _code_lengths(frequencies, count):
    heap = [(freq, symbol, symbol) for symbol, freq in frequencies.items()]
    heapq.heapify(heap)
    lengths, seq = [0] * count, count
    if len(heap) == 1:
        lengths[heap[0][2]] = 1
    elif heap:
        while len(heap) > 1:
            a, b = heapq.heappop(heap), heapq.heappop(heap)
            heapq.heappush(heap, (a[0]+b[0], seq, (a[2], b[2])))
            seq += 1
        stack = [(heap[0][2], 0)]
        while stack:
            node, depth = stack.pop()
            if isinstance(node, int):
                lengths[node] = depth
            else:
                stack.extend(((node[0], depth+1), (node[1], depth+1)))
    return lengths


def _huff_reader(lengths):
    # Reject oversubscribed and incomplete trees (except a one-symbol tree).
    maximum = max(lengths)
    used = sum(bool(n) for n in lengths)
    if maximum > 32:
        raise ValueError('Слишком длинный код LZH')
    if used:
        total = sum(1 << (maximum-n) for n in lengths if n)
        if total != 1 << maximum and not (used == 1 and maximum == 1):
            raise ValueError('Неверная таблица LZH')
    lookup = {(code, width): symbol for symbol, (code, width) in canonical(lengths).items()}
    def read(reader):
        code = 0
        for width in range(1, maximum+1):
            code = (code << 1) | reader.get(1)
            symbol = lookup.get((code, width))
            if symbol is not None:
                return symbol
        raise ValueError('Неверный код LZH')
    return read