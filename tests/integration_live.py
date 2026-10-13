"""An actual TCP host, raw uploads, subprocess scheduling, and archive restore.

Run this script within a single execution environment (including network namespace).
"""
import hashlib
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0,str(ROOT))
from app.archive import restore
from app.codecs import catalog,normalize_step


def main():
    with tempfile.TemporaryDirectory() as directory:
        with socket.socket() as probe:
            probe.bind(("127.0.0.1",0))
            port = probe.getsockname()[1]
        key = "synthetic-integration-test-key-000000"
        env = {**os.environ,"CLAB_ACCESS_KEY":key}
        process = subprocess.Popen([sys.executable,str(ROOT/"run.py"),"--host","0.0.0.0",
            "--port",str(port),"--data-dir",directory],cwd=ROOT,env=env,
            stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        url = f"http://127.0.0.1:{port}"
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        def call(path, data=None, verb=None, authorized=True):
            headers = {"Authorization":"Bearer "+key} if authorized else {}
            if data is not None and not isinstance(data,bytes):
                data = json.dumps(data).encode()
                headers["Content-Type"] = "application/json"
            response = opener.open(urllib.request.Request(url+path,data=data,headers=headers,method=verb),timeout=10)
            if response.headers.get_content_type() == "application/json":
                return json.load(response)
            return response.read()
        try:
            deadline = time.monotonic()+10
            while True:
                try:
                    call("/api/session",authorized=False)
                    break
                except (urllib.error.URLError,ConnectionError):
                    if time.monotonic()>deadline:
                        raise
                    time.sleep(.05)
            try:
                call("/api/files",authorized=False)
                raise AssertionError("Unauthenticated request was allowed")
            except urllib.error.HTTPError as error:
                assert error.code == 401
            data = (b"2026-10-03 INFO api /files 200 repeated research data\n"*1200+b"\0"*80000)
            record = call("/api/files?name=integration.bin",data=data,verb="POST")
            assert call("/api/files/"+record["id"]+"/download") == data
            methods = [m for m in catalog() if m["available"]]
            pipelines = [dict(label=m["name"],steps=[normalize_step(dict(method=m["id"]))]) for m in methods]
            pipelines.append(dict(label="RLE + Huffman + Zstd",steps=[normalize_step(dict(method=m)) for m in ["rle","huffman","zstd"]]))
            experiment = call("/api/experiments",dict(name="Live integration",file_ids=[record["id"]],
                pipelines=pipelines,repeats=1,warmups=0,block_kib=64,timeout_seconds=30),"POST")
            deadline = time.monotonic()+40
            while True:
                tasks = call("/api/tasks")
                if all(t["status"] in ("completed","failed","cancelled") for t in tasks):
                    break
                if time.monotonic()>deadline:
                    raise AssertionError("Tasks did not complete within 40 seconds")
                time.sleep(.1)
            for task in tasks:
                assert task["status"] == "completed", (task["label"],task["error"])
                result = task["result"]
                archive = call("/api/tasks/"+task["id"]+"/download")
                assert len(archive) == result["output_bytes"]
                source = Path(directory)/(task["id"]+".clab")
                destination = Path(directory)/(task["id"]+".restored")
                source.write_bytes(archive)
                restore(source,destination)
                assert hashlib.sha256(destination.read_bytes()).hexdigest()==record["sha256"]
                print(task["label"],"OK",result["output_bytes"],"bytes",flush=True)
            exported = call("/api/experiments/"+experiment["id"]+"/export?format=json")
            assert len(exported["tasks"]) == len(pipelines)
            assert b"pipeline" in call("/api/experiments/"+experiment["id"]+"/export")
            future = call("/api/experiments",dict(name="Future",file_ids=[record["id"]],
                pipelines=pipelines[:1],start_at="2099-01-01T00:00:00+00:00"),"POST")
            task = next(t for t in call("/api/tasks") if t["experiment_id"]==future["id"])
            assert task["status"] == "queued"
            call("/api/tasks/"+task["id"]+"/cancel",verb="POST")
            assert next(t for t in call("/api/tasks") if t["id"]==task["id"])["status"]=="cancelled"
            print("TCP upload, 16 process tasks, downloads, restore, export, schedule and cancellation: OK",flush=True)
        finally:
            process.terminate()
            try:
                stdout,stderr = process.communicate(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                stdout,stderr = process.communicate()
            if process.returncode not in (0,-15):
                print(stdout,stderr,file=sys.stderr)


if __name__ == "__main__":
    main()
