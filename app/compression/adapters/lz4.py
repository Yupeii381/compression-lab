"""Library adapter; parameter names match the platform catalog."""
import importlib

def encode(data: bytes, *, level: int=0) -> bytes:
    return importlib.import_module('lz4.frame').compress(data, compression_level=level, content_checksum=True)

def decode(data: bytes, *, level: int=0) -> bytes:
    return importlib.import_module('lz4.frame').decompress(data)
