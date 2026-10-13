"""Library adapter; parameter names match the platform catalog."""
import lzma

def encode(data: bytes, *, level: int=6, dictionary_mib: int=8) -> bytes:
    filt = dict(id=lzma.FILTER_LZMA2, preset=level, dict_size=dictionary_mib << 20)
    return lzma.compress(data, format=lzma.FORMAT_XZ, filters=[filt])

def decode(data: bytes, *, level: int=6, dictionary_mib: int=8) -> bytes:
    return lzma.decompress(data, memlimit=256 << 20)
