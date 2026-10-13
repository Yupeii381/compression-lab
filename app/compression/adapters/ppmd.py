"""Library adapter; parameter names match the platform catalog."""
import importlib

def encode(data: bytes, *, order: int=6, memory_mib: int=16) -> bytes:
    return importlib.import_module('pyppmd').compress(data, max_order=order, mem_size=memory_mib << 20, variant='I')

def decode(data: bytes, *, order: int=6, memory_mib: int=16) -> bytes:
    return importlib.import_module('pyppmd').decompress(data, max_order=order, mem_size=memory_mib << 20, variant='I')
