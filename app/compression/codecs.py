from app.compression.registry import METHODS, BY_ID, CODECS
from app.compression.limits import MAX_BLOCK_OUTPUT

import copy
import importlib
import importlib.metadata
import platform
import zlib

def catalog():
    result = []
    for method in METHODS:
        item = copy.deepcopy(method)
        item["available"] = True
        item["version"] = ("reference 1.0" if "Python" in item["implementation"]
                           else "CPython " + platform.python_version() + " stdlib")
        if item["id"] in ("gzip","deflate"):
            item["version"] = "zlib " + zlib.ZLIB_RUNTIME_VERSION
        if module := item.get("module"):
            try:
                importlib.import_module(module)
                package = {"lz4.frame": "lz4"}.get(module, module)
                item["version"] = importlib.metadata.version(package)
            except importlib.metadata.PackageNotFoundError:
                item["version"] = "Версия пакета неизвестна"
            except ImportError:
                item["available"] = False
                item["version"] = "Не установлен пакет " + module
        result.append(item)
    return result

def normalize_step(step):
    if not isinstance(step, dict):
        raise ValueError("Шаг должен быть объектом с методом и параметрами")
    method_id = step.get("method")
    if method_id == "ppmp":
        method_id = "ppmd"
    if not isinstance(method_id,str) or method_id not in BY_ID:
        raise ValueError("Неизвестный метод: " + str(method_id))
    method = BY_ID[method_id]
    if module := method.get("module"):
        try:
            importlib.import_module(module)
        except ImportError as e:
            raise ValueError(f"Для {method['name']} установите {module}") from e
    supplied = step.get("params", {})
    if not isinstance(supplied, dict):
        raise ValueError("Параметры должны быть объектом")
    if set(supplied) - {p["key"] for p in method["parameters"]}:
        raise ValueError(f"Неизвестные параметры {method['name']}")
    params = {}
    for p in method["parameters"]:
        value = supplied.get(p["key"], p["default"])
        if isinstance(value, bool) or not isinstance(value, int) or not p["min"] <= value <= p["max"]:
            raise ValueError(f"{p['label']}: требуется целое число от {p['min']} до {p['max']}")
        params[p["key"]] = value
    return dict(method=method_id, params=params)

def encode(data: bytes, step: dict) -> bytes:
    normalized = normalize_step(step)
    codec = CODECS[normalized["method"]]
    return codec.encode(data, **normalized["params"])


def decode(data: bytes, step: dict) -> bytes:
    normalized = normalize_step(step)
    codec = CODECS[normalized["method"]]
    return codec.decode(data, **normalized["params"])