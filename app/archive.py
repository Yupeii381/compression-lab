"""Portable CLAB container: JSON manifest followed by independently coded blocks.

Stage sizes are stored to bound and verify every reverse transformation. The
payload size and the complete archive size are both reported in benchmarks.
"""
from __future__ import annotations
import hashlib
import json
import struct
from .codecs import decode, normalize_step, MAX_BLOCK_OUTPUT

MAGIC = b"CLAB1\r\n"


def write_header(stream, manifest):
    data = json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode()
    stream.write(MAGIC + struct.pack("<I", len(data)) + data)


def write_block(stream, sizes, payload):
    stream.write(struct.pack("<" + "I" * len(sizes), *sizes))
    stream.write(struct.pack("<I", len(payload)))
    stream.write(payload)


def read_exact(stream, size):
    data = stream.read(size)
    if len(data) != size:
        raise ValueError("Оборванный контейнер")
    return data


def restore(source, destination):
    """Restores an app-generated archive, checking length and original SHA-256.

    Exposed as a local CLI, not as a network decompression endpoint.
    """
    sha, total = hashlib.sha256(), 0
    with open(source, "rb") as src, open(destination, "wb") as dst:
        if read_exact(src, len(MAGIC)) != MAGIC:
            raise ValueError("Это не архив Compression Lab")
        header_size = struct.unpack("<I", read_exact(src, 4))[0]
        if header_size > 65536:
            raise ValueError("Слишком большой заголовок")
        manifest = json.loads(read_exact(src, header_size))
        steps = [normalize_step(s) for s in manifest["steps"]]
        if not 1 <= len(steps) <= 6:
            raise ValueError("Некорректная цепочка")
        while first := src.read(4):
            sizes = [struct.unpack("<I", first)[0]]
            sizes.extend(struct.unpack("<" + "I" * len(steps), read_exact(src, 4*len(steps))))
            payload_size = struct.unpack("<I", read_exact(src, 4))[0]
            if max(sizes + [payload_size]) > MAX_BLOCK_OUTPUT or payload_size != sizes[-1]:
                raise ValueError("Некорректные размеры блока")
            block = read_exact(src, payload_size)
            for index in range(len(steps)-1, -1, -1):
                block = decode(block, steps[index])
                if len(block) != sizes[index]:
                    raise ValueError("Неверная длина восстановленного шага")
            total += len(block)
            if total > manifest["original_size"]:
                raise ValueError("Превышен исходный размер")
            sha.update(block)
            dst.write(block)
    if total != manifest["original_size"] or sha.hexdigest() != manifest["sha256"]:
        from pathlib import Path
        Path(destination).unlink(missing_ok=True)
        raise ValueError("Проверка SHA-256 не пройдена")
    return manifest
