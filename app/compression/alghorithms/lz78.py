from app.compression.limits import MAX_BLOCK_OUTPUT

import struct

def encode(data, dictionary_size=65536):
    out = bytearray(b"L81" + struct.pack("<I", dictionary_size))
    trie, next_code, prefix = {}, 1, 0
    for byte in data:
        key = (prefix, byte)
        if key in trie:
            prefix = trie[key]
        else:
            out.extend(struct.pack("<IB", prefix, byte))
            trie[key] = next_code
            next_code += 1
            prefix = 0
            if next_code >= dictionary_size:
                trie.clear()
                next_code = 1
    if prefix:
        out.extend(struct.pack("<I", prefix))
    return bytes(out)


def decode(data, *, dictionary_size=65536):
    if data[:3] != b"L81" or len(data) < 7:
        raise ValueError("Неверный заголовок LZ78")
    limit = struct.unpack_from("<I", data, 3)[0]
    if not 256 <= limit <= 262144:
        raise ValueError("Неверный словарь LZ78")
    dictionary, out, i = [b""], bytearray(), 7
    while i < len(data):
        if i+4 > len(data):
            raise ValueError("Оборванный поток LZ78")
        prefix = struct.unpack_from("<I", data, i)[0]
        i += 4
        if prefix >= len(dictionary):
            raise ValueError("Неверная ссылка LZ78")
        phrase = dictionary[prefix]
        if i < len(data):
            phrase += bytes([data[i]])
            i += 1
            dictionary.append(phrase)
        out.extend(phrase)
        if len(dictionary) >= limit:
            dictionary = [b""]
        if len(out) > MAX_BLOCK_OUTPUT:
            raise ValueError("Превышен размер блока")
    return bytes(out)