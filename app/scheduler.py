"""Durable priority queue; CPU work runs outside the HTTP server in processes."""
from __future__ import annotations
import os
import subprocess
import sys
import threading
import time
from pathlib import Path
from .storage import now


class Scheduler:
    def __init__(self, store):
        self.store = store
        self.active = {}
        self.stop_event = threading.Event()
        self.thread = None

    def start(self):
        self.store.recover()
        for partial in (self.store.root / "artifacts").glob("*.partial"):
            partial.unlink(missing_ok=True)
        self.thread = threading.Thread(target=self.loop, daemon=True, name="compression-planner")
        self.thread.start()

    @staticmethod
    def kill(process):
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=2)

    def loop(self):
        while not self.stop_event.is_set():
            try:
                for task_id, (process, started, timeout) in list(self.active.items()):
                    task = self.store.get_task(task_id)
                    cancelled = not task or task["status"] == "cancelled"
                    expired = time.monotonic()-started > timeout
                    if cancelled or expired:
                        self.kill(process)
                        for ext in (".partial", ".clab"):
                            (self.store.root / "artifacts" / (task_id+ext)).unlink(missing_ok=True)
                        if expired and not cancelled:
                            self.store.update_running(task_id, status="failed", error=f"Превышен лимит времени: {timeout} с", finished_at=now())
                    if process.poll() is not None:
                        self.store.update_running(task_id, status="failed", error=f"Рабочий процесс завершился без результата (код {process.returncode})", finished_at=now())
                        del self.active[task_id]
                settings = self.store.settings()
                if not settings["paused"]:
                    while len(self.active) < settings["workers"] and not self.stop_event.is_set():
                        task_id = self.store.claim_next()
                        if task_id is None:
                            break
                        task = self.store.get_task(task_id)
                        process = subprocess.Popen([sys.executable, "-m", "app.worker", "--data-dir", str(self.store.root),
                            "--task-id", task_id], cwd=Path(__file__).resolve().parent.parent,
                            env=os.environ.copy(), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                        self.active[task_id] = (process, time.monotonic(), task["timeout_seconds"])
            except Exception as error:
                print("Scheduler:", str(error), file=sys.stderr)
            self.stop_event.wait(0.2)

    def stop(self):
        self.stop_event.set()
        if self.thread:
            self.thread.join(timeout=5)
        for process, *_ in list(self.active.values()):
            self.kill(process)
