"""Library adapter; parameter names match the platform catalog."""
import zlib

def encode(data: bytes, *, level: int=6, window_bits: int=15) -> bytes:
    wbits = -window_bits
    c = zlib.compressobj(level, zlib.DEFLATED, wbits)
    return c.compress(data) + c.flush()

def decode(data: bytes, *, level: int=6, window_bits: int=15) -> bytes:
    return zlib.decompress(data, -window_bits)
