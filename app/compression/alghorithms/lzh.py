from app.compression.common.lz_format import _lz_encode, _lz_decode
from app.compression.limits import MAX_BLOCK_OUTPUT

def encode(data, window_kib=32, search_depth=16):
    """Two-pass LZSS + static Huffman; own format, not an LHA archive."""
    return _lz_encode(data, 'lzh', window_kib, search_depth)

def decode(data, *, window_kib=32, search_depth=16):
    return _lz_decode(data, 'lzh')