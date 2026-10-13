"""SQLite metadata and immutable, content-addressed local files."""
from __future__ import annotations

import contextlib
import datetime as dt
import hashlib
import json
import os
import sqlite3
import uuid
from pathlib import Path


def now():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def uid():
    return uuid.uuid4().hex


class Store:
    def __init__(self, root):
        self.root = Path(root).resolve()
        for name in ("blobs", "artifacts", "tmp"):
            (self.root / name).mkdir(parents=True, exist_ok=True)
        self.db_path = self.root / "lab.sqlite3"
        with self.connect() as db:
            db.executescript("""
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS files(
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, size INTEGER NOT NULL,
                    sha256 TEXT NOT NULL, created_at TEXT NOT NULL, kind TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS experiments(
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL,
                    config TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS tasks(
                    id TEXT PRIMARY KEY, experiment_id TEXT NOT NULL REFERENCES experiments(id),
                    file_id TEXT NOT NULL REFERENCES files(id), label TEXT NOT NULL,
                    steps TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'queued',
                    priority INTEGER NOT NULL DEFAULT 0, position INTEGER NOT NULL,
                    created_at TEXT NOT NULL, start_at TEXT NOT NULL,
                    started_at TEXT, finished_at TEXT, progress REAL NOT NULL DEFAULT 0,
                    result TEXT, error TEXT,
                    repeats INTEGER NOT NULL, warmups INTEGER NOT NULL,
                    block_kib INTEGER NOT NULL, timeout_seconds INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_queue ON tasks(status, priority, position);
                CREATE TABLE IF NOT EXISTS presets(
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, pipelines TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT OR IGNORE INTO settings VALUES('paused', 'false');
                INSERT OR IGNORE INTO settings VALUES('workers', '1');
            """)

    @contextlib.contextmanager
    def connect(self):
        db = sqlite3.connect(self.db_path, timeout=30)
        db.row_factory = sqlite3.Row
        db.execute("PRAGMA foreign_keys=ON")
        db.execute("PRAGMA busy_timeout=30000")
        try:
            yield db
            db.commit()
        except Exception:
            db.rollback()
            raise
        finally:
            db.close()

    def get_file(self, file_id):
        with self.connect() as db:
            row = db.execute("SELECT * FROM files WHERE id=?", (file_id,)).fetchone()
            return dict(row) if row else None

    def blob_path(self, file):
        # Only database-owned hashes are accepted; never a client filename.
        sha = file["sha256"]
        if len(sha) != 64 or any(c not in "0123456789abcdef" for c in sha):
            raise ValueError("Некорректный хеш")
        return self.root / "blobs" / sha[:2] / sha

    def finish_upload(self, temp_path, name, size, sha256):
        safe_name = name.replace("\\", "/").split("/")[-1]
        safe_name = "".join(c for c in safe_name if c >= " " and c != "\x7f")[:240] or "file.bin"
        record = dict(id=uid(), name=safe_name, size=size, sha256=sha256,
                      created_at=now(), kind=Path(safe_name).suffix.lower().lstrip(".") or "bin")
        target = self.blob_path(record)
        target.parent.mkdir(exist_ok=True)
        # Both concurrent uploads have identical bytes if their SHA-256 matches.
        if target.exists():
            Path(temp_path).unlink(missing_ok=True)
        else:
            os.replace(temp_path, target)
        with self.connect() as db:
            db.execute("INSERT INTO files VALUES(:id,:name,:size,:sha256,:created_at,:kind)", record)
        return record

    def add_bytes(self, name, data):
        temp = self.root / "tmp" / uid()
        temp.write_bytes(data)
        return self.finish_upload(temp, name, len(data), hashlib.sha256(data).hexdigest())

    def files(self):
        with self.connect() as db:
            return [dict(r) for r in db.execute("SELECT * FROM files ORDER BY created_at DESC")]

    def delete_file(self, file_id):
        with self.connect() as db:
            file = db.execute("SELECT * FROM files WHERE id=?", (file_id,)).fetchone()
            if not file:
                return False
            if db.execute("SELECT 1 FROM tasks WHERE file_id=? LIMIT 1", (file_id,)).fetchone():
                raise ValueError("Файл используется в экспериментах. Он сохранён для воспроизводимости.")
            db.execute("DELETE FROM files WHERE id=?", (file_id,))
            if not db.execute("SELECT 1 FROM files WHERE sha256=?", (file["sha256"],)).fetchone():
                self.blob_path(file).unlink(missing_ok=True)
            return True

    def settings(self):
        with self.connect() as db:
            return {r["key"]: json.loads(r["value"]) for r in db.execute("SELECT * FROM settings")}

    def set_setting(self, key, value):
        with self.connect() as db:
            db.execute("INSERT OR REPLACE INTO settings VALUES(?,?)", (key, json.dumps(value)))

    def create_experiment(self, name, file_ids, pipelines, options):
        experiment_id, timestamp = uid(), now()
        with self.connect() as db:
            # Keep validation and inserts atomic in case another client deletes a file.
            db.execute("BEGIN IMMEDIATE")
            for file_id in file_ids:
                if not db.execute("SELECT 1 FROM files WHERE id=?", (file_id,)).fetchone():
                    raise ValueError("Исходный файл не найден")
            db.execute("INSERT INTO experiments VALUES(?,?,?,?)", (experiment_id, name, timestamp,
                json.dumps(dict(file_ids=file_ids, pipelines=pipelines, **options), ensure_ascii=False)))
            position = db.execute("SELECT COALESCE(MAX(position),0) FROM tasks").fetchone()[0]
            for file_id in file_ids:
                for pipeline in pipelines:
                    position += 1
                    db.execute("""INSERT INTO tasks(id,experiment_id,file_id,label,steps,
                        priority,position,created_at,start_at,repeats,warmups,block_kib,timeout_seconds)
                        VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                        (uid(), experiment_id, file_id, pipeline["label"], json.dumps(pipeline["steps"]),
                         options["priority"], position, timestamp, options["start_at"],
                         options["repeats"], options["warmups"], options["block_kib"], options["timeout_seconds"]))
        return experiment_id

    @staticmethod
    def task_record(row):
        item = dict(row)
        item["steps"] = json.loads(item["steps"])
        item["result"] = json.loads(item["result"]) if item["result"] else None
        return item

    def get_task(self, task_id):
        with self.connect() as db:
            row = db.execute("""SELECT t.*, f.name AS filename, f.size AS input_size,
                f.sha256 AS input_sha256 FROM tasks t JOIN files f ON f.id=t.file_id
                WHERE t.id=?""", (task_id,)).fetchone()
            return self.task_record(row) if row else None

    def tasks(self, experiment_id=None):
        with self.connect() as db:
            query = """SELECT t.*,f.name AS filename,f.size AS input_size,f.sha256 AS input_sha256,
                e.name AS experiment_name FROM tasks t JOIN files f ON f.id=t.file_id
                JOIN experiments e ON e.id=t.experiment_id"""
            args = ()
            if experiment_id:
                query += " WHERE t.experiment_id=?"
                args = (experiment_id,)
            query += " ORDER BY t.priority DESC,t.position ASC"
            return [self.task_record(r) for r in db.execute(query, args)]

    def experiments(self):
        tasks = self.tasks()
        grouped = {}
        for t in tasks:
            grouped.setdefault(t["experiment_id"], []).append(t)
        with self.connect() as db:
            result = []
            for row in db.execute("SELECT * FROM experiments ORDER BY created_at DESC"):
                item = dict(row)
                item["config"] = json.loads(item["config"])
                item["tasks"] = grouped.get(item["id"], [])
                statuses = [t["status"] for t in item["tasks"]]
                item["status"] = ("running" if "running" in statuses else "queued" if "queued" in statuses
                    else "failed" if "failed" in statuses else "cancelled" if "cancelled" in statuses else "completed")
                item["completed"] = statuses.count("completed")
                item["total"] = len(statuses)
                result.append(item)
            return result

    def claim_next(self):
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("""SELECT id FROM tasks WHERE status='queued' AND start_at<=?
                ORDER BY priority DESC,position ASC LIMIT 1""", (now(),)).fetchone()
            if not row:
                return None
            db.execute("UPDATE tasks SET status='running',started_at=?,error=NULL,progress=0 WHERE id=?", (now(), row["id"]))
            return row["id"]

    def update_running(self, task_id, **fields):
        allowed = {"progress", "result", "status", "error", "finished_at"}
        if not set(fields) <= allowed:
            raise ValueError("Некорректные поля")
        values = [json.dumps(v, ensure_ascii=False) if k == "result" else v for k, v in fields.items()]
        with self.connect() as db:
            cursor = db.execute("UPDATE tasks SET " + ",".join(k+"=?" for k in fields) + " WHERE id=? AND status='running'", values + [task_id])
            return cursor.rowcount > 0

    def recover(self):
        with self.connect() as db:
            db.execute("UPDATE tasks SET status='queued',progress=0,error='Возобновлено после перезапуска' WHERE status='running'")

    def cancel(self, task_id):
        with self.connect() as db:
            db.execute("UPDATE tasks SET status='cancelled',finished_at=? WHERE id=? AND status IN ('running','queued')", (now(), task_id))

    def retry(self, task_id):
        with self.connect() as db:
            cursor = db.execute("""UPDATE tasks SET status='queued',progress=0,result=NULL,error=NULL,
                started_at=NULL,finished_at=NULL,start_at=? WHERE id=? AND status IN ('failed','cancelled')""", (now(), task_id))
            return bool(cursor.rowcount)

    def promote(self, task_id):
        with self.connect() as db:
            high = db.execute("SELECT COALESCE(MAX(priority),0)+1 FROM tasks WHERE status='queued'").fetchone()[0]
            db.execute("UPDATE tasks SET priority=? WHERE id=? AND status='queued'", (high, task_id))

    def presets(self):
        with self.connect() as db:
            return [dict(id=r["id"],name=r["name"],pipelines=json.loads(r["pipelines"])) for r in db.execute("SELECT * FROM presets ORDER BY created_at DESC")]

    def save_preset(self, name, pipelines):
        preset_id = uid()
        with self.connect() as db:
            db.execute("INSERT INTO presets VALUES(?,?,?,?)", (preset_id,name,json.dumps(pipelines,ensure_ascii=False),now()))
        return preset_id
