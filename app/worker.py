"""One isolated benchmark process. File I/O is excluded from codec timings."""
from __future__ import annotations
import argparse
import hashlib
import os
import platform
import statistics
import time
from .archive import write_header, write_block
from .codecs import encode, decode, catalog, MAX_BLOCK_OUTPUT
from .storage import Store, now


def peak_rss():
    try:
        import resource
        value = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
        return int(value if platform.system() == "Darwin" else value * 1024)
    except ImportError:
        return None


def execute(store, task):
    file = store.get_file(task["file_id"])
    src_path = store.blob_path(file)
    steps = task["steps"]
    temp = store.root / "artifacts" / (task["id"] + ".partial")
    target = store.root / "artifacts" / (task["id"] + ".clab")
    encode_samples, decode_samples, stage_samples = [], [], []
    payload_size = 0
    total_runs = task["repeats"] + task["warmups"]
    manifest = dict(version=1, filename=file["name"], original_size=file["size"],
                    sha256=file["sha256"], steps=steps, block_kib=task["block_kib"])
    last_update = 0
    try:
        for repetition in range(total_runs):
            measured = repetition >= task["warmups"]
            save = repetition == task["warmups"]
            output = open(temp, "wb") if save else None
            if output:
                write_header(output, manifest)
            enc_ms = dec_ms = read_bytes = run_payload = 0
            stages = [dict(method=s["method"], input_bytes=0, output_bytes=0, encode_ms=0.0, decode_ms=0.0) for s in steps]
            original_hash, restored_hash = hashlib.sha256(), hashlib.sha256()
            try:
                with open(src_path, "rb", buffering=1024*1024) as src:
                    while raw := src.read(task["block_kib"] * 1024):
                        original_hash.update(raw)
                        read_bytes += len(raw)
                        block, sizes = raw, [len(raw)]
                        for i, step in enumerate(steps):
                            stages[i]["input_bytes"] += len(block)
                            start = time.perf_counter_ns()
                            block = encode(block, step)
                            elapsed = (time.perf_counter_ns()-start) / 1e6
                            stages[i]["encode_ms"] += elapsed
                            enc_ms += elapsed
                            stages[i]["output_bytes"] += len(block)
                            sizes.append(len(block))
                            if len(block) > MAX_BLOCK_OUTPUT:
                                raise ValueError("Цепочка создала блок больше 128 МиБ")
                        run_payload += len(block)
                        if output:
                            write_block(output, sizes, block)
                        for i in range(len(steps)-1, -1, -1):
                            start = time.perf_counter_ns()
                            block = decode(block, steps[i])
                            elapsed = (time.perf_counter_ns()-start) / 1e6
                            stages[i]["decode_ms"] += elapsed
                            dec_ms += elapsed
                            if len(block) != sizes[i]:
                                raise ValueError("Неверная длина восстановленного шага")
                        if block != raw:
                            raise ValueError("Восстановленные данные отличаются от исходных")
                        restored_hash.update(block)
                        current = time.monotonic()
                        if current-last_update > 0.3:
                            progress = (repetition + read_bytes/max(1,file["size"])) / total_runs
                            if not store.update_running(task["id"], progress=min(0.99,progress)):
                                raise InterruptedError("Задача отменена")
                            last_update = current
                if original_hash.hexdigest() != file["sha256"] or restored_hash.digest() != original_hash.digest():
                    raise ValueError("Исходный файл изменён или проверка SHA-256 не пройдена")
            finally:
                if output:
                    output.close()
            if measured:
                encode_samples.append(enc_ms)
                decode_samples.append(dec_ms)
                stage_samples.append(stages)
                payload_size = run_payload
        os.replace(temp, target)
        encode_ms, decode_ms = statistics.median(encode_samples), statistics.median(decode_samples)
        stage_result = []
        for i in range(len(steps)):
            stage = dict(stage_samples[0][i])
            for metric in ("encode_ms","decode_ms"):
                stage[metric] = round(statistics.median(s[i][metric] for s in stage_samples), 4)
            stage_result.append(stage)
        archive_size = target.stat().st_size
        result = dict(input_bytes=file["size"], output_bytes=archive_size,
            payload_bytes=payload_size, overhead_bytes=archive_size-payload_size,
            reduction_pct=round((1-archive_size/file["size"])*100,4) if file["size"] else None,
            ratio=round(file["size"]/archive_size,4) if archive_size else 0,
            encode_ms=round(encode_ms,4), decode_ms=round(decode_ms,4),
            encode_mib_s=round(file["size"]/(1024*1024)/max(encode_ms/1000,1e-9),4),
            decode_mib_s=round(file["size"]/(1024*1024)/max(decode_ms/1000,1e-9),4),
            encode_samples_ms=encode_samples, decode_samples_ms=decode_samples,
            verified=True, sha256=file["sha256"], stages=stage_result,
            peak_rss_bytes=peak_rss(), artifact=target.name, repetitions=task["repeats"],
            block_kib=task["block_kib"], environment=dict(python=platform.python_version(),system=platform.system(),processor=platform.processor(),
                codec_versions={m["id"]:m["version"] for m in catalog() if m["id"] in {s["method"] for s in steps}}))
        if not store.update_running(task["id"], status="completed", progress=1,
                                    result=result, finished_at=now(), error=None):
            target.unlink(missing_ok=True)
    except BaseException:
        temp.unlink(missing_ok=True)
        target.unlink(missing_ok=True)
        raise


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-dir", required=True)
    parser.add_argument("--task-id", required=True)
    args = parser.parse_args()
    store = Store(args.data_dir)
    task = store.get_task(args.task_id)
    if not task or task["status"] != "running":
        return
    # Per-process memory guard on Unix. Windows still has block-size and timeout limits.
    try:
        import resource
        resource.setrlimit(resource.RLIMIT_AS, (1536 << 20, 1536 << 20))
    except (ImportError, ValueError, OSError):
        pass
    try:
        execute(store, task)
    except InterruptedError:
        return
    except BaseException as error:
        store.update_running(task["id"], status="failed", error=str(error)[:1000] or type(error).__name__, finished_at=now())


if __name__ == "__main__":
    main()
