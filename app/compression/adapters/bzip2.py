"""Library adapter; parameter names match the platform catalog."""
import bz2

def encode(data: bytes, *, level: int = 6 ) -> bytes:
    return bz2.compress(data, compresslevel=level)

def encode(data: bytes, *, level: int = 6 ) -> bytes:
    return bz2.decompress(data)
