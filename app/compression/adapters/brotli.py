"""Library adapter; parameter names match the platform catalog."""
import importlib

def encode(data: bytes, *, quality: int=5, lgwin: int=22) -> bytes:
    return importlib.import_module('brotli').compress(data, quality=quality, lgwin=lgwin)

def decode(data: bytes, *, quality: int=5, lgwin: int=22) -> bytes:
    return importlib.import_module('brotli').decompress(data)
