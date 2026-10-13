"""Library adapter; parameter names match the platform catalog."""
import zlib

def encode(data: bytes, *, level: int=6) -> bytes:
    wbits = 31
    c = zlib.compressobj(level, zlib.DEFLATED, wbits)
    return c.compress(data) + c.flush()

def decode(data: bytes, *, level: int=6) -> bytes:
    return zlib.decompress(data, 31)
