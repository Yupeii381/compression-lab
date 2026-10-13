"""Hold an OS lock so only one host schedules work in a data directory."""
import os
from contextlib import contextmanager
from pathlib import Path


@contextmanager
def exclusive_host(data_dir):
    path = Path(data_dir)/"host.lock"
    stream = open(path,"a+b")
    stream.seek(0)
    try:
        if os.name == "nt":
            import msvcrt
            if path.stat().st_size == 0:
                stream.write(b"0")
                stream.flush()
            stream.seek(0)
            msvcrt.locking(stream.fileno(),msvcrt.LK_NBLCK,1)
        else:
            import fcntl
            fcntl.flock(stream.fileno(),fcntl.LOCK_EX|fcntl.LOCK_NB)
    except (OSError,IOError):
        stream.close()
        raise SystemExit("В этом каталоге данных уже работает хост. Остановите его или выберите другой --data-dir.")
    try:
        yield
    finally:
        stream.close()
