from app.compression.limits import MAX_BLOCK_OUTPUT
from app.compression.common.canonical import canonical

import heapq
import collections
import struct


def encode(data):
    frequencies = collections.Counter(data)
    heap, seq = [], 0
    for symbol, freq in sorted(frequencies.items()):
        heapq.heappush(heap, (freq, seq, symbol))
        seq += 1
    lengths = [0] * 256
    if len(heap) == 1:
        lengths[heap[0][2]] = 1
    elif heap:
        while len(heap) > 1:
            a, b = heapq.heappop(heap), heapq.heappop(heap)
            heapq.heappush(heap, (a[0] + b[0], seq, (a[2], b[2])))
            seq += 1
        stack = [(heap[0][2], 0)]
        while stack:
            node, depth = stack.pop()
            if isinstance(node, int):
                lengths[node] = depth
            else:
                stack.extend([(node[0], depth+1), (node[1], depth+1)])
    codes = canonical(lengths)
    out = bytearray(b"HF1" + struct.pack("<Q", len(data)) + bytes(lengths))
    acc = bits = 0
    for symbol in data:
        code, width = codes[symbol]
        acc = (acc << width) | code
        bits += width
        while bits >= 8:
            bits -= 8
            out.append((acc >> bits) & 255)
        acc &= (1 << bits) - 1
    if bits:
        out.append(acc << (8-bits))
    return bytes(out)


def decode(data):
    if data[:3] != b"HF1" or len(data) < 267:
        raise ValueError("Неверный заголовок Huffman")
    size = struct.unpack_from("<Q", data, 3)[0]
    if size > MAX_BLOCK_OUTPUT:
        raise ValueError("Превышен размер блока")
    if not size:
        return b""
    lookup = {(c, n): s for s, (c, n) in canonical(data[11:267]).items()}
    out, code, width = bytearray(), 0, 0
    for byte in data[267:]:
        for shift in range(7, -1, -1):
            code = (code << 1) | ((byte >> shift) & 1)
            width += 1
            symbol = lookup.get((code, width))
            if symbol is not None:
                out.append(symbol)
                code = width = 0
                if len(out) == size:
                    return bytes(out)
    raise ValueError("Оборванный поток Huffman")