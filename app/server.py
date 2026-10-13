"""Local HTTP API and web UI. No uploads or work are sent to cloud services."""
from __future__ import annotations

import base64
import csv
import datetime as dt
import hashlib
import hmac
import io
import json
import os
import platform
import secrets
import shutil
import socket
import time
from contextlib import asynccontextmanager
from pathlib import Path
from urllib.parse import urlsplit

from fastapi import FastAPI, Request, HTTPException
from fastapi.responses import FileResponse, JSONResponse, Response
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field

from .codecs import catalog, normalize_step
from .scheduler import Scheduler
from .storage import Store, now, uid

WEB_ROOT = Path(__file__).resolve().parent.parent / "web"


class ExperimentInput(BaseModel):
    name: str = Field(min_length=1, max_length=120)
    file_ids: list[str] = Field(min_length=1, max_length=32)
    pipelines: list[dict] = Field(min_length=1, max_length=32)
    repeats: int = Field(default=3, ge=1, le=10)
    warmups: int = Field(default=1, ge=0, le=3)
    block_kib: int = Field(default=1024, ge=64, le=4096)
    priority: int = Field(default=0, ge=0, le=100)
    timeout_seconds: int = Field(default=300, ge=10, le=1800)
    start_at: str | None = None


class LoginInput(BaseModel):
    key: str = Field(min_length=1,max_length=256)


class SettingsInput(BaseModel):
    paused: bool | None = None
    workers: int | None = Field(default=None,ge=1,le=4)


class PresetInput(BaseModel):
    name: str = Field(min_length=1,max_length=120)
    pipelines: list[dict] = Field(min_length=1,max_length=32)


def clean_pipelines(pipelines):
    cleaned = []
    for pipeline in pipelines:
        steps = pipeline.get("steps")
        if not isinstance(steps,list) or not 1 <= len(steps) <= 6:
            raise ValueError("В цепочке должно быть от 1 до 6 методов")
        label = pipeline.get("label")
        if not isinstance(label,str) or not label.strip() or len(label) > 120:
            raise ValueError("Укажите название цепочки до 120 символов")
        cleaned.append(dict(label=label.strip(),steps=[normalize_step(s) for s in steps]))
    return cleaned


def create_app(data_dir, *, host="127.0.0.1", port=8080, access_key=None,
               max_upload_mib=1024, storage_limit_gib=20, run_scheduler=True):
    store = Store(data_dir)
    scheduler = Scheduler(store)
    auth_required = bool(access_key)
    if host not in ("127.0.0.1","localhost","::1") and not auth_required:
        raise ValueError("Для сетевого хоста нужен ключ доступа")
    auth_secret = (access_key or secrets.token_urlsafe(32)).encode()
    login_attempts = {}

    def make_session():
        payload = str(int(time.time())+8*3600).encode()
        signature = hmac.new(auth_secret,payload,hashlib.sha256).hexdigest()
        return payload.decode()+"."+signature

    def authenticated(request):
        if not auth_required:
            return True
        header = request.headers.get("authorization","")
        if header.startswith("Bearer ") and secrets.compare_digest(header[7:].encode(), auth_secret):
            return True
        try:
            expires,signature = request.cookies.get("clab_session","").split(".")
            expected = hmac.new(auth_secret,expires.encode(),hashlib.sha256).hexdigest()
            return int(expires) > time.time() and hmac.compare_digest(signature,expected)
        except (ValueError,TypeError):
            return False

    @asynccontextmanager
    async def lifespan(app):
        if run_scheduler:
            scheduler.start()
        yield
        if run_scheduler:
            scheduler.stop()

    app = FastAPI(title="Compression Lab",version="1.0.0",lifespan=lifespan,
                  docs_url=None,redoc_url=None,openapi_url=None)
    app.state.store, app.state.scheduler = store,scheduler

    @app.middleware("http")
    async def boundary(request: Request, call_next):
        # Protect unauthenticated loopback mode against DNS rebinding and CSRF.
        if not auth_required:
            hostname = urlsplit(str(request.url)).hostname
            if hostname not in ("localhost","127.0.0.1","::1","testserver","terminal.local"):
                return JSONResponse({"detail":"Локальный режим: используйте localhost"},status_code=403)
        if request.method not in ("GET","HEAD","OPTIONS"):
            origin = request.headers.get("origin")
            expected = f"{request.url.scheme}://{request.headers.get('host','')}"
            if origin and origin != expected:
                return JSONResponse({"detail":"Запрос с другого сайта запрещён"},status_code=403)
        public = request.url.path in ("/api/session","/api/login","/api/logout")
        if request.url.path.startswith("/api/") and not public and not authenticated(request):
            return JSONResponse({"detail":"Введите ключ доступа к хосту"},status_code=401)
        response = await call_next(request)
        response.headers["X-Content-Type-Options"] = "nosniff"
        response.headers["Referrer-Policy"] = "no-referrer"
        response.headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'"
        response.headers["Cache-Control"] = "no-store" if request.url.path.startswith("/api/") else "no-cache"
        return response

    @app.exception_handler(ValueError)
    async def bad_value(request, error):
        return JSONResponse({"detail":str(error)},status_code=400)

    @app.get("/api/session")
    def session(request: Request):
        return dict(auth_required=auth_required,authenticated=authenticated(request))

    @app.post("/api/login")
    def login(body: LoginInput, request: Request):
        client = request.client.host if request.client else "unknown"
        stamp = time.monotonic()
        attempts = [s for s in login_attempts.get(client,[]) if stamp-s < 60]
        if len(attempts) >= 10:
            raise HTTPException(429,"Слишком много попыток. Попробуйте через минуту.")
        attempts.append(stamp)
        login_attempts[client] = attempts
        if auth_required and not secrets.compare_digest(body.key.encode(),auth_secret):
            raise HTTPException(401,"Неверный ключ доступа")
        response = JSONResponse({"authenticated":True})
        response.set_cookie("clab_session",make_session(),httponly=True,samesite="strict",
                            secure=request.url.scheme == "https",max_age=8*3600)
        return response

    @app.post("/api/logout")
    def logout():
        response = JSONResponse({"ok":True})
        response.delete_cookie("clab_session")
        return response

    @app.get("/api/methods")
    def methods():
        return catalog()

    @app.get("/api/files")
    def files():
        return store.files()

    @app.post("/api/files", status_code=201)
    async def upload(request: Request, name: str = "file.bin"):
        if len(name) > 1000:
            raise HTTPException(400,"Слишком длинное имя файла")
        try:
            length = int(request.headers.get("content-length",0))
        except ValueError:
            raise HTTPException(400,"Некорректная длина запроса")
        limit = max_upload_mib << 20
        if length > limit:
            raise HTTPException(413,f"Лимит файла: {max_upload_mib} МиБ")
        used = sum(f["size"] for f in store.files())
        free = shutil.disk_usage(store.root).free
        if used+length > storage_limit_gib << 30 or length > free-(64 << 20):
            raise HTTPException(507,"Недостаточно свободного места или достигнут лимит хранилища")
        temp = store.root / "tmp" / uid()
        size,sha = 0,hashlib.sha256()
        try:
            with open(temp,"wb",buffering=1024*1024) as stream:
                async for chunk in request.stream():
                    size += len(chunk)
                    if size > limit or used+size > storage_limit_gib << 30:
                        raise HTTPException(413,"Превышен лимит загрузки")
                    sha.update(chunk)
                    stream.write(chunk)
            return store.finish_upload(temp,name,size,sha.hexdigest())
        finally:
            temp.unlink(missing_ok=True)

    @app.delete("/api/files/{file_id}")
    def delete_file(file_id: str):
        if not store.delete_file(file_id):
            raise HTTPException(404,"Файл не найден")
        return dict(ok=True)

    @app.get("/api/files/{file_id}/download")
    def download_file(file_id: str):
        file = store.get_file(file_id)
        if not file:
            raise HTTPException(404,"Файл не найден")
        return FileResponse(store.blob_path(file),filename=file["name"],media_type="application/octet-stream")

    @app.post("/api/examples",status_code=201)
    def examples():
        existing = {f["name"] for f in store.files()}
        samples = {
            "server-events.jsonl": b''.join(json.dumps(dict(timestamp=f"2026-10-01T12:{i%60:02}:00Z",level="INFO",service="api",event="request_completed",route="/api/files",status=200,user_id=i%40),separators=(",",":")).encode()+b'\n' for i in range(4000)),
            "repeated-data.bin": (b"\x00"*400+b"\xff"*400+b"ABCDEF"*60)*300,
            "research-notes.txt": ("Исследование сжатия цифровых данных. LZ77 использует скользящее окно. Код Хаффмана строится по частотам символов. Сравниваем размер и скорость, проверяем восстановление.\n"*1500).encode(),
        }
        return [store.add_bytes(name,data) for name,data in samples.items() if name not in existing]

    @app.get("/api/experiments")
    def experiments():
        return store.experiments()

    @app.post("/api/experiments",status_code=201)
    def create_experiment(body: ExperimentInput):
        if not body.name.strip():
            raise HTTPException(400,"Укажите название эксперимента")
        pipelines = clean_pipelines(body.pipelines)
        file_ids = list(dict.fromkeys(body.file_ids))
        if len(file_ids)*len(pipelines) > 128:
            raise HTTPException(400,"Не больше 128 задач в одном эксперименте")
        if sum(t["status"] in ("queued","running") for t in store.tasks()) >= 512:
            raise HTTPException(429,"Очередь заполнена: дождитесь завершения задач")
        options = body.model_dump(exclude={"name","file_ids","pipelines","start_at"})
        timestamp = body.start_at or now()
        try:
            scheduled = dt.datetime.fromisoformat(timestamp.replace("Z","+00:00"))
            if scheduled.tzinfo is None:
                raise ValueError("Укажите часовой пояс времени запуска")
            options["start_at"] = scheduled.astimezone(dt.timezone.utc).isoformat()
        except (ValueError,TypeError):
            raise HTTPException(400,"Некорректное время запуска")
        experiment_id = store.create_experiment(body.name.strip(),file_ids,pipelines,options)
        return dict(id=experiment_id)

    @app.get("/api/tasks")
    def tasks():
        return store.tasks()

    @app.post("/api/tasks/{task_id}/{action}")
    def task_action(task_id: str, action: str):
        if not store.get_task(task_id):
            raise HTTPException(404,"Задача не найдена")
        if action == "cancel": store.cancel(task_id)
        elif action == "retry":
            if not store.retry(task_id):
                raise HTTPException(409,"Можно повторить отменённую или неудачную задачу")
        elif action == "promote": store.promote(task_id)
        else: raise HTTPException(404,"Действие не найдено")
        return dict(ok=True)

    @app.get("/api/tasks/{task_id}/download")
    def download_result(task_id: str):
        task = store.get_task(task_id)
        if not task or task["status"] != "completed":
            raise HTTPException(404,"Результат ещё не готов")
        path = store.root / "artifacts" / (task_id+".clab")
        if not path.exists():
            raise HTTPException(404,"Архив отсутствует на диске")
        name = task["filename"]+"."+"-".join(s["method"] for s in task["steps"])+".clab"
        return FileResponse(path,filename=name,media_type="application/octet-stream")

    @app.get("/api/experiments/{experiment_id}/export")
    def export(experiment_id: str, format: str = "csv"):
        found = next((e for e in store.experiments() if e["id"] == experiment_id),None)
        if not found:
            raise HTTPException(404,"Эксперимент не найден")
        if format == "json":
            return Response(json.dumps(found,ensure_ascii=False,indent=2),media_type="application/json",
                            headers={"Content-Disposition":'attachment; filename="experiment.json"'})
        buffer = io.StringIO()
        fields = ["file","pipeline","status","input_bytes","output_bytes","payload_bytes","overhead_bytes",
                  "reduction_pct","encode_ms","decode_ms","encode_mib_s","decode_mib_s","verified","sha256","parameters","block_kib","repetitions","error"]
        writer = csv.DictWriter(buffer,fieldnames=fields)
        writer.writeheader()
        for task in found["tasks"]:
            row = {k:v for k,v in (task["result"] or {}).items() if k in fields}
            row.update(file=task["filename"],pipeline=task["label"],status=task["status"],
                       input_bytes=task["input_size"],block_kib=task["block_kib"],repetitions=task["repeats"],
                       parameters=json.dumps(task["steps"],ensure_ascii=False),error=task["error"] or "")
            # CSV spreadsheet formula injection guard for untrusted filenames/labels.
            row = {k:("'"+v if isinstance(v,str) and v[:1] in ("=","+","-","@","\t","\r") else v) for k,v in row.items()}
            writer.writerow(row)
        return Response("\ufeff"+buffer.getvalue(),media_type="text/csv",
                        headers={"Content-Disposition":'attachment; filename="experiment.csv"'})

    @app.get("/api/presets")
    def presets():
        return store.presets()

    @app.post("/api/presets",status_code=201)
    def save_preset(body: PresetInput):
        if not body.name.strip():
            raise HTTPException(400,"Укажите название плана")
        return dict(id=store.save_preset(body.name.strip(),clean_pipelines(body.pipelines)))

    @app.delete("/api/presets/{preset_id}")
    def delete_preset(preset_id: str):
        with store.connect() as db:
            db.execute("DELETE FROM presets WHERE id=?",(preset_id,))
        return dict(ok=True)

    @app.patch("/api/settings")
    def settings(body: SettingsInput):
        for key,value in body.model_dump(exclude_none=True).items():
            store.set_setting(key,value)
        return store.settings()

    @app.get("/api/host")
    def host_info():
        settings = store.settings()
        tasks = store.tasks()
        files = store.files()
        disk = shutil.disk_usage(store.root)
        try:
            addresses = sorted({r[4][0] for r in socket.getaddrinfo(socket.gethostname(),port,socket.AF_INET) if not r[4][0].startswith("127.")})
        except OSError:
            addresses = []
        return dict(host=host,port=port,hostname=socket.gethostname(),local_only=host in ("127.0.0.1","localhost","::1"),
            auth_required=auth_required,addresses=addresses,storage_path=str(store.root),
            files_count=len(files),storage_bytes=sum(f["size"] for f in files),disk_free_bytes=disk.free,
            artifacts_bytes=sum(p.stat().st_size for p in (store.root/"artifacts").glob("*.clab")),
            storage_limit_gib=storage_limit_gib,max_upload_mib=max_upload_mib,
            system=platform.system(),python=platform.python_version(),cpu_count=os.cpu_count(),
            settings=settings,queued=sum(t["status"]=="queued" for t in tasks),
            running=sum(t["status"]=="running" for t in tasks),completed=sum(t["status"]=="completed" for t in tasks))

    app.mount("/static",StaticFiles(directory=WEB_ROOT),name="static")

    @app.get("/")
    def index():
        return FileResponse(WEB_ROOT/"index.html")

    return app
