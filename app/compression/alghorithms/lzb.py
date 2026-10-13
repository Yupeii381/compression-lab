from app.compression.common.lz_format import _lz_encode, _lz_decode
from app.compression.limits import MAX_BLOCK_OUTPUT

def encode(data, window_kib=32, search_depth=16):
    """LZB reference: growing distance width, gamma(length-2), max match 258."""
    return _lz_encode(data, 'lzb', window_kib, search_depth)

def decode(data, *, window_kib=32, search_depth=16):
    return _lz_decode(data, 'lzb')
