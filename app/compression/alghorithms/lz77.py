from app.compression.limits import MAX_BLOCK_OUTPUT

import collections
import struct

def encode(data, window_kib=32, search_depth=16):
    out, literals, index = bytearray(b"L71"), bytearray(), {}
    window, i = window_kib * 1024, 0
    def flush():
        if literals:
            out.append(len(literals)-1)
            out.extend(literals)
            literals.clear()
    while i < len(data):
        best, distance = 0, 0
        key = data[i:i+3]
        candidates = index.get(key, ()) if len(key) == 3 else ()
        for pos in reversed(candidates):
            if i - pos > window:
                break
            length, maximum = 3, min(130, len(data)-i)
            while length < maximum and data[pos+length] == data[i+length]:
                length += 1
            if length > best:
                best, distance = length, i-pos
                if best == maximum:
                    break
        advance = best if best >= 3 else 1
        if best >= 3:
            flush()
            out.append(128 + best - 3)
            out.extend(struct.pack("<I", distance))
        else:
            literals.append(data[i])
            if len(literals) == 128:
                flush()
        for pos in range(i, i+advance):
            key = data[pos:pos+3]
            if len(key) == 3:
                bucket = index.get(key)
                if bucket is None:
                    bucket = index[key] = collections.deque(maxlen=search_depth)
                bucket.append(pos)
        # A bounded hash index avoids retention growing with the whole block.
        if len(index) > 131072:
            index.clear()
        i += advance
    flush()
    return bytes(out)


def decode(data, *, window_kib=32, search_depth=16):
    if data[:3] != b"L71":
        raise ValueError("Неверный заголовок LZ77")
    out, i = bytearray(), 3
    while i < len(data):
        tag = data[i]
        i += 1
        if tag < 128:
            n = tag+1
            if i+n > len(data):
                raise ValueError("Оборванный поток LZ77")
            out.extend(data[i:i+n])
            i += n
        else:
            if i+4 > len(data):
                raise ValueError("Оборванная ссылка LZ77")
            distance = struct.unpack_from("<I", data, i)[0]
            i += 4
            if not 0 < distance <= len(out):
                raise ValueError("Неверная ссылка LZ77")
            for _ in range(tag-128+3):
                out.append(out[-distance])
        if len(out) > MAX_BLOCK_OUTPUT:
            raise ValueError("Превышен размер блока")
    return bytes(out)