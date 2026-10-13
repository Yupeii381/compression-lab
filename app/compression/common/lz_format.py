"""Общий формат LZSS/LZB/LZH: заголовки, токены и контрольная сумма.

Форматы SaaS, версия 1; не совместимы с LHA.
LZB: растущая ширина расстояния, gamma(length-2), совпадения до 258 байт.
LZH: статический Huffman символов/длин и классов расстояний.
"""
import collections
import struct
import zlib
from app.compression.limits import MAX_BLOCK_OUTPUT
from app.compression.common.bits import _BitWriter, _BitReader
from app.compression.common.canonical import canonical, _code_lengths, _huff_reader
from app.compression.common.lz_matching import _lz_options, _lz_tokens

_LZ_MAGIC = {'lzss': b'LS1', 'lzb': b'LB1', 'lzh': b'LH1'}
_LZ_HEADER = struct.Struct('<3sQII')

def _lz_encode(data, mode, window_kib=32, search_depth=16):
    window = _lz_options(window_kib, search_depth)
    if len(data) > MAX_BLOCK_OUTPUT:
        raise ValueError('Превышен размер блока')
    header = _LZ_HEADER.pack(_LZ_MAGIC[mode], len(data), window, zlib.crc32(data))
    writer = _BitWriter()
    if mode == 'lzh':
        symbols, distances = collections.Counter(), collections.Counter()
        for value, length in _lz_tokens(data, window, search_depth, mode):
            symbols[256+length-3 if length else value] += 1
            if length:
                distances[(value-1).bit_length()] += 1
        lengths = _code_lengths(symbols, 512)
        dlengths = _code_lengths(distances, 17)
        codes, dcodes = canonical(lengths), canonical(dlengths)
        header += bytes(lengths) + bytes(dlengths)
    position = 0
    for value, length in _lz_tokens(data, window, search_depth, mode):
        if mode == 'lzh':
            writer.put(*codes[256+length-3 if length else value])
            if length:
                width = (value-1).bit_length()
                writer.put(*dcodes[width])
                if width:
                    writer.put(value-1-(1 << (width-1)), width-1)
        else:
            writer.put(bool(length), 1)
            if not length:
                writer.put(value, 8)
            elif mode == 'lzss':
                writer.put(value-1, (window-1).bit_length())
                writer.put(length-3, 8)
            else:
                writer.put(value-1, (min(window, position)-1).bit_length())
                writer.gamma(length-2)
        position += length or 1
    return header + writer.finish()

def _lz_decode(data, mode):
    if len(data) < _LZ_HEADER.size:
        raise ValueError('Оборванный заголовок LZ')
    magic, size, window, checksum = _LZ_HEADER.unpack_from(data)
    if magic != _LZ_MAGIC[mode] or window % 1024 or not 1024 <= window <= 65536:
        raise ValueError('Неверный заголовок LZ')
    if size > MAX_BLOCK_OUTPUT:
        raise ValueError('Превышен размер блока')
    start = _LZ_HEADER.size
    if mode == 'lzh':
        if len(data) < start + 529:
            raise ValueError('Оборванные таблицы LZH')
        symbol_reader = _huff_reader(data[start:start+512])
        distance_reader = _huff_reader(data[start+512:start+529])
        start += 529
    reader, out = _BitReader(data[start:]), bytearray()
    while len(out) < size:
        if mode == 'lzh':
            symbol = symbol_reader(reader)
            if symbol < 256:
                out.append(symbol)
                continue
            length = symbol-256+3
            width = distance_reader(reader)
            distance = 1 if width == 0 else 1 + (1 << (width-1)) + reader.get(width-1)
        else:
            if not reader.get(1):
                out.append(reader.get(8))
                continue
            if mode == 'lzss':
                distance = reader.get((window-1).bit_length())+1
                length = reader.get(8)+3
            else:
                if not out:
                    raise ValueError("Ссылка до начала данных")
                distance = reader.get((min(window, len(out))-1).bit_length())+1
                length = reader.gamma(9)+2
        if not 0 < distance <= min(window, len(out)) or not 3 <= length <= 258:
            raise ValueError('Неверная ссылка LZ')
        if len(out)+length > size:
            raise ValueError('Ссылка выходит за размер блока')
        for _ in range(length):
            out.append(out[-distance])  # Overlapping matches are intentional.
    reader.finish()
    if zlib.crc32(out) != checksum:
        raise ValueError('Не совпадает контрольная сумма LZ')
    return bytes(out)
