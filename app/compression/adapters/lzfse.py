"""Library adapter; parameter names match the platform catalog."""
import importlib

def encode(data: bytes) -> bytes:
    return importlib.import_module('lzfse').compress(data)

def decode(data: bytes) -> bytes:
    if data == b'bvx-\x00\x00\x00\x00bvx$':
        return b''
    return importlib.import_module('lzfse').decompress(data)
