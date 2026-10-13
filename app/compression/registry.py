from app.compression.alghorithms import rle, huffman, lz77, lz78, lzb, lzw, lzh, lzss
from app.compression.adapters import brotli, bzip2, deflate, gzip, lz4, lzfse, lzma1, lzma2, ppmd, zstd

def number(key, label, default, low, high, step=1):
    return dict(key=key, label=label, type="number", default=default,
                min=low, max=high, step=step)

METHODS = [
    dict(id="zstd", name="Zstandard", family="LZ", description="Современное сжатие: LZ77 и энтропийное кодирование.", module="zstandard", implementation="libzstd · C", parameters=[number("level", "Уровень", 3, 1, 22)]),
    dict(id="lzma2", name="LZMA2", family="LZ", description="LZMA2 в контейнере XZ, с настраиваемым словарём.", implementation="liblzma · C", parameters=[number("level", "Уровень", 6, 0, 9), number("dictionary_mib", "Словарь, МиБ", 8, 1, 64)]),
    dict(id="lzma1", name="LZMA1", family="LZ", description="LZMA1 в исходном контейнере .lzma.", implementation="liblzma · C", parameters=[number("level", "Уровень", 6, 0, 9), number("dictionary_mib", "Словарь, МиБ", 8, 1, 64)]),
    dict(id="gzip", name="Gzip", family="LZ", description="DEFLATE с заголовком Gzip и контрольной суммой.", implementation="zlib · C", parameters=[number("level", "Уровень", 6, 0, 9)]),
    dict(id="deflate", name="DEFLATE", family="LZ", description="LZ77 + Huffman; поток без внешнего контейнера.", implementation="zlib · C", parameters=[number("level", "Уровень", 6, 0, 9), number("window_bits", "Окно, бит", 15, 9, 15)]),
    dict(id="lz4", name="LZ4", family="LZ", description="Быстрый LZ77-кодек с проверкой целостности кадра.", module="lz4.frame", implementation="liblz4 · C", parameters=[number("level", "Уровень", 0, 0, 16)]),
    dict(id="lzfse", name="LZFSE", family="LZ", description="Оригинальная реализация Apple: LZ + FSE.", module="lzfse", implementation="Apple LZFSE · C", parameters=[]),
    dict(id="brotli", name="Brotli", family="LZ", description="LZ77, Huffman и встроенный словарь.", module="brotli", implementation="Google Brotli · C/C++", parameters=[number("quality", "Качество", 5, 0, 11), number("lgwin", "Окно, бит", 22, 10, 24)]),
    dict(id="ppmd", name="PPMd", family="PPM", description="Prediction by Partial Matching, вариант I.2. «PPMP» принято как PPMd.", module="pyppmd", implementation="PPMd I.2 · C", parameters=[number("order", "Порядок модели", 6, 2, 16), number("memory_mib", "Память, МиБ", 16, 1, 128)]),
    dict(id="bzip2", name="Bzip2", family="BWT", description="Burrows–Wheeler, MTF, RLE и Huffman.", implementation="libbzip2 · C", parameters=[number("level", "Уровень", 6, 1, 9)]),
    dict(id="huffman", name="Huffman", family="Entropy", description="Канонический побайтовый код Хаффмана; таблица кодов входит в размер.", implementation="Эталонная · Python", parameters=[]),
    dict(id="rle", name="RLE", family="RLE", description="PackBits: серии и блоки буквальных байтов.", implementation="Эталонная · Python", parameters=[number("min_run", "Минимальная серия", 3, 2, 8)]),
    dict(id="lz77", name="LZ77", family="LZ", description="Скользящее окно и поиск совпадений; собственный формат токенов.", implementation="Эталонная · Python", parameters=[number("window_kib", "Окно, КиБ", 32, 1, 64), number("search_depth", "Глубина поиска", 16, 1, 128)]),
    dict(id="lz78", name="LZ78", family="LZ", description="Словарь фраз, пары индекс–символ; сброс словаря при заполнении.", implementation="Эталонная · Python", parameters=[number("dictionary_size", "Фраз в словаре", 65536, 256, 262144)]),
    dict(id="lzw", name="LZW", family="LZ", description="Фиксированные 16-битные коды со сбросом словаря.", implementation="Эталонная · Python", parameters=[number("dictionary_size", "Кодов в словаре", 65536, 512, 65536)]),
    dict(id="lzss", name="LZSS", family="LZ", description="LZSS: битовые флаги и ссылки фиксированной ширины.", implementation="Эталонная · Python", parameters=[number("window_kib", "Окно, КиБ", 32, 1, 64), number("search_depth", "Глубина поиска", 16, 1, 128)]),
    dict(id="lzb", name="LZB", family="LZ", description="LZB: растущая ширина расстояния + gamma-коды длин; фразы до 258 байт.", implementation="Эталонная · Python", parameters=[number("window_kib", "Окно, КиБ", 32, 1, 64), number("search_depth", "Глубина поиска", 16, 1, 128)]),
    dict(id="lzh", name="LZH", family="LZ", description="LZSS + статический Huffman символов/длин и классов расстояний; не LHA.", implementation="Эталонная · Python", parameters=[number("window_kib", "Окно, КиБ", 32, 1, 64), number("search_depth", "Глубина поиска", 16, 1, 128)]),
]
BY_ID = {m["id"]: m for m in METHODS}

CODECS = {
    "zstd": zstd,
    "lzma2": lzma2,
    "lzma1": lzma1,
    "gzip": gzip,
    "deflate": deflate,
    "lz4": lz4,
    "lzfse": lzfse,
    "brotli": brotli,
    "ppmd": ppmd,
    "bzip2": bzip2,
    "huffman": huffman,
    "rle": rle,
    "lz77": lz77,
    "lz78": lz78,
    "lzw": lzw,
    "lzss": lzss,
    "lzb": lzb,
    "lzh": lzh,
}

if CODECS.keys() != BY_ID.keys():
    raise RuntimeError("Каталог и реализации кодеков не совпадают")