from app.compression.limits import MAX_BLOCK_OUTPUT

import struct

def encode(data, dictionary_size=65536):
    out = bytearray(b"LW1" + struct.pack("<I", dictionary_size))
    dictionary, next_code, phrase = {bytes([b]): b for b in range(256)}, 256, b""
    for byte in data:
        candidate = phrase + bytes([byte])
        if candidate in dictionary:
            phrase = candidate
        else:
            out.extend(struct.pack("<H", dictionary[phrase]))
            if next_code < dictionary_size:
                dictionary[candidate] = next_code
                next_code += 1
            else:
                dictionary, next_code = {bytes([b]): b for b in range(256)}, 256
            phrase = bytes([byte])
    if phrase:
        out.extend(struct.pack("<H", dictionary[phrase]))
    return bytes(out)


def decode(data, *, dictionary_size=65536):
    if data[:3] != b"LW1" or len(data) < 7 or (len(data)-7) % 2:
        raise ValueError("Неверный поток LZW")
    limit = struct.unpack_from("<I", data, 3)[0]
    if not 512 <= limit <= 65536:
        raise ValueError("Неверный словарь LZW")
    dictionary, previous, out = [bytes([b]) for b in range(256)], None, bytearray()
    for (code,) in struct.iter_unpack("<H", data[7:]):
        if code < len(dictionary):
            phrase = dictionary[code]
        elif code == len(dictionary) and previous is not None:
            phrase = previous + previous[:1]
        else:
            raise ValueError("Неверный код LZW")
        out.extend(phrase)
        if previous is not None:
            dictionary.append(previous + phrase[:1])
        previous = phrase
        if len(dictionary) >= limit:
            # Encoder resets when it tries to insert *after* filling the table.
            dictionary = [bytes([b]) for b in range(256)]
            previous = None
        if len(out) > MAX_BLOCK_OUTPUT:
            raise ValueError("Превышен размер блока")
    return bytes(out)