from app.compression.limits import MAX_BLOCK_OUTPUT
from app.compression.common import canonical
from app.compression.common.bits import _BitReader, _BitWriter

import collections
import heapq
import struct
import zlib

def _lz_options(window_kib, search_depth):
    for value, low, high in ((window_kib, 1, 64), (search_depth, 1, 128)):
        if isinstance(value, bool) or not isinstance(value, int) or not low <= value <= high:
            raise ValueError('Неверные параметры LZ')
    return window_kib * 1024


def _lz_tokens(data, window, search_depth, mode):
    """Yield (literal, 0) or (distance, length); bounded hash-chain search."""
    index, i = {}, 0
    offset_bits = (window - 1).bit_length()
    while i < len(data):
        best, distance, saving = 0, 0, 0
        key = data[i:i+3]
        maximum = min(258, len(data) - i)
        for pos in reversed(index.get(key, ()) if len(key) == 3 else ()):
            d = i - pos
            if d > window:
                break
            length = 3
            while length < maximum and data[pos+length] == data[i+length]:
                length += 1
            cost = (1 + (min(window, i)-1).bit_length() + 2*(length-2).bit_length()-1
                    if mode == 'lzb' else 1 + offset_bits + 8)
            gain = 9 * length - cost
            if gain > saving:
                best, distance, saving = length, d, gain
            if length == maximum:
                break
        advance = best or 1
        yield (distance, best) if best else (data[i], 0)
        for pos in range(i, i+advance):
            key = data[pos:pos+3]
            if len(key) == 3:
                bucket = index.get(key)
                if bucket is None:
                    bucket = index[key] = collections.deque(maxlen=search_depth)
                bucket.append(pos)
        if len(index) > 131072:
            index.clear()
        i += advance


