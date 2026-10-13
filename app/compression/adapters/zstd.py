"""Library adapter; parameter names match the platform catalog."""
import importlib
from app.compression.limits import MAX_BLOCK_OUTPUT

def encode(data: bytes, *, level: int=3) -> bytes:
    return importlib.import_module('zstandard').ZstdCompressor(level=level, write_checksum=True).compress(data)

def decode(data: bytes, *, level: int=3) -> bytes:
    return importlib.import_module('zstandard').ZstdDecompressor().decompress(data, max_output_size=MAX_BLOCK_OUTPUT)
