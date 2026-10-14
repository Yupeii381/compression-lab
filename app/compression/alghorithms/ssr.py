"""SSR: frequency-ranked tokens represented as base-alphabet integers.

Version 1 uses Unicode characters (UTF-8 with surrogateescape for arbitrary
input bytes). Unlike C# char, a Python character is a Unicode code point,
not a UTF-16 code unit. The mathematical block formula is unchanged.
This is positional coding, not combinatorial permutation ranking.
Own format SSR1; not compatible with the original C# .ssr container.
Tokenization and ranking are separate from the integer block functions so
future word/pattern modes can reuse the mathematical core. A new tokenizer
requires a versioned format and lossless token boundary rules.
"""
import collections
import struct
import zlib

from app.compression.limits import MAX_BLOCK_OUTPUT

_HEADER = struct.Struct('<4sQQIII')  # magic, byte count, token count, block size, alphabet, CRC
_MAX_BLOCK_SIZE = 16384
_MAX_ALPHABET = 0x110080


def tokenize(data):
    return list(data.decode('utf-8', errors='surrogateescape'))


def build_ranks(tokens):
    counts = collections.Counter(tokens)
    ordered = sorted(counts, key=lambda token: (-counts[token], token))
    return {token: rank for rank, token in enumerate(ordered, 1)}


def encode_block(block, token_to_rank):
    result = 0
    base = len(token_to_rank)
    for token in block:
        rank = token_to_rank[token] - 1
        result = result * base + rank
    return result


def encode_all_blocks(blocks, token_to_rank):
    return [encode_block(block, token_to_rank) for block in blocks]


def decode_block(encoded, rank_to_token, block_size):
    base = len(rank_to_token)
    if encoded < 0 or block_size < 0 or (block_size and not base):
        raise ValueError('Неверный блок SSR')
    block = [None] * block_size
    for i in range(block_size - 1, -1, -1):
        encoded, rank = divmod(encoded, base)
        block[i] = rank_to_token[rank + 1]
    if encoded:
        raise ValueError('Число SSR выходит за длину блока')
    return block


def decode_all_blocks(encoded_blocks, block_size, original_token_length, rank_to_token):
    if block_size <= 0 or original_token_length < 0:
        raise ValueError('Неверный размер SSR')
    expected = (original_token_length + block_size - 1) // block_size
    if len(encoded_blocks) != expected:
        raise ValueError('Неверное число блоков SSR')
    result = []
    for i, encoded in enumerate(encoded_blocks):
        current_size = min(block_size, original_token_length - block_size * i)
        result.extend(decode_block(encoded, rank_to_token, current_size))
    return result


def _validate_block_size(block_size):
    if isinstance(block_size, bool) or not isinstance(block_size, int) or not 1 <= block_size <= _MAX_BLOCK_SIZE:
        raise ValueError(f'Размер блока SSR: от 1 до {_MAX_BLOCK_SIZE}')


def encode(data: bytes, *, block_size: int = 2000) -> bytes:
    _validate_block_size(block_size)
    if len(data) > MAX_BLOCK_OUTPUT:
        raise ValueError('Превышен размер блока')
    tokens = tokenize(data)
    token_to_rank = build_ranks(tokens)
    out = bytearray(_HEADER.pack(b'SSR1', len(data), len(tokens), block_size,
                                len(token_to_rank), zlib.crc32(data)))
    # The order in the table is the rank order, starting at 1.
    for token in token_to_rank:
        raw = token.encode('utf-8', errors='surrogateescape')
        out.append(len(raw))
        out.extend(raw)
    for start in range(0, len(tokens), block_size):
        encoded = encode_block(tokens[start:start + block_size], token_to_rank)
        raw = encoded.to_bytes((encoded.bit_length() + 7) // 8, 'big')
        out.extend(struct.pack('<I', len(raw)))
        out.extend(raw)
    return bytes(out)


def decode(data: bytes, *, block_size: int = 2000) -> bytes:
    # Actual block size and alphabet are stored in the encoded stream.
    _validate_block_size(block_size)
    if len(data) < _HEADER.size:
        raise ValueError('Оборванный заголовок SSR')
    magic, byte_count, token_count, stored_size, alphabet, checksum = _HEADER.unpack_from(data)
    if magic != b'SSR1':
        raise ValueError('Неверный заголовок SSR')
    _validate_block_size(stored_size)
    if byte_count > MAX_BLOCK_OUTPUT or token_count > byte_count:
        raise ValueError('Превышен размер блока или неверное число токенов SSR')
    if not token_count <= byte_count <= 4 * token_count:
        raise ValueError('Неверная длина данных SSR')
    if alphabet > min(token_count, _MAX_ALPHABET) or bool(alphabet) != bool(token_count):
        raise ValueError('Неверный алфавит SSR')
    rank_to_token, seen, position = {}, set(), _HEADER.size
    for rank in range(1, alphabet + 1):
        if position >= len(data):
            raise ValueError('Оборванный словарь SSR')
        width = data[position]
        position += 1
        if not 1 <= width <= 4 or position + width > len(data):
            raise ValueError('Неверный символ SSR')
        raw = data[position:position + width]
        position += width
        token = raw.decode('utf-8', errors='surrogateescape')
        if len(token) != 1 or token in seen:
            raise ValueError('Неверный или повторяющийся символ SSR')
        seen.add(token)
        rank_to_token[rank] = token
    out = bytearray()
    for start in range(0, token_count, stored_size):
        current_size = min(stored_size, token_count - start)
        if position + 4 > len(data):
            raise ValueError('Оборванный блок SSR')
        width = struct.unpack_from('<I', data, position)[0]
        position += 4
        maximum = (current_size * (alphabet - 1).bit_length() + 7) // 8
        if width > maximum or position + width > len(data):
            raise ValueError('Неверная длина числа SSR')
        raw = data[position:position + width]
        position += width
        if raw and raw[0] == 0:
            raise ValueError('Неоднозначное представление числа SSR')
        encoded = int.from_bytes(raw, 'big')
        block = decode_block(encoded, rank_to_token, current_size)
        restored = ''.join(block).encode('utf-8', errors='surrogateescape')
        if len(out) + len(restored) > byte_count:
            raise ValueError('SSR выходит за исходный размер')
        out.extend(restored)
    if position != len(data) or len(out) != byte_count:
        raise ValueError('Неверный размер потока SSR')
    if zlib.crc32(out) != checksum:
        raise ValueError('Не совпадает контрольная сумма SSR')
    return bytes(out)
