from app.compression.common.lz_format import _lz_encode, _lz_decode
from app.compression.limits import MAX_BLOCK_OUTPUT

def encode(data, window_kib=32, search_depth=16):
    """LZSS: 1-bit tag, 8-bit literal or fixed-width distance and length."""
    return _lz_encode(data, 'lzss', window_kib, search_depth)

def decode(data, *, window_kib=32, search_depth=16):
    return _lz_decode(data, 'lzss')
