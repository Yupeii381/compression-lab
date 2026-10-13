"""Run the local host or restore a CLAB archive."""
from __future__ import annotations
import argparse
import os
import secrets
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
if (ROOT / "vendor").is_dir():
    sys.path.insert(0,str(ROOT / "vendor"))
    os.environ["PYTHONPATH"] = str(ROOT / "vendor") + os.pathsep + os.environ.get("PYTHONPATH", "")


def main():
    parser = argparse.ArgumentParser(description="Compression Lab — локальная лаборатория сжатия")
    parser.add_argument("--host",default="127.0.0.1")
    parser.add_argument("--network",action="store_true",help="Доступ из сети: слушать 0.0.0.0 и требовать ключ")
    parser.add_argument("--port",type=int,default=8080)
    parser.add_argument("--data-dir",default=str(ROOT / "data"))
    parser.add_argument("--max-upload-mib",type=int,default=1024)
    parser.add_argument("--storage-limit-gib",type=int,default=20)
    parser.add_argument("--proxy-headers",action="store_true",help="Доверять HTTPS-прокси только с loopback-адресов")
    parser.add_argument("--ssl-certfile",help="PEM сертификат для прямого HTTPS")
    parser.add_argument("--ssl-keyfile",help="PEM ключ для прямого HTTPS")
    parser.add_argument("--restore",metavar="ARCHIVE")
    parser.add_argument("--output",metavar="FILE")
    args = parser.parse_args()
    if args.restore:
        if not args.output:
            parser.error("Для --restore укажите --output")
        output = Path(args.output).resolve()
        if output.exists():
            parser.error("Выходной файл уже существует. Выберите другое имя.")
        from app.archive import restore
        try:
            manifest = restore(args.restore,output)
        except Exception as e:
            output.unlink(missing_ok=True)
            raise SystemExit(str(e))
        print("Восстановлено:",manifest["filename"],"SHA-256:",manifest["sha256"])
        return
    if not 1 <= args.port <= 65535 or args.max_upload_mib < 1 or args.storage_limit_gib < 1:
        parser.error("Проверьте порт и лимиты")
    if bool(args.ssl_keyfile) != bool(args.ssl_certfile):
        parser.error("Укажите и --ssl-certfile, и --ssl-keyfile")
    if args.network:
        args.host = "0.0.0.0"
    data_dir = Path(args.data_dir).resolve()
    data_dir.mkdir(parents=True,exist_ok=True)
    key = os.environ.get("CLAB_ACCESS_KEY")
    if args.host not in ("127.0.0.1","localhost","::1") and not key:
        key_file = data_dir / "host-key.txt"
        if key_file.exists():
            key = key_file.read_text().strip()
        else:
            key = secrets.token_urlsafe(32)
            fd = os.open(key_file,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
            with os.fdopen(fd,"w") as out:
                out.write(key+"\n")
        print("Ключ доступа:",key,flush=True)
    if args.host not in ("127.0.0.1","localhost","::1") and (not key or len(key) < 16):
        parser.error("Для сети нужен ключ CLAB_ACCESS_KEY длиной не меньше 16 символов")
    print(f"Compression Lab: {'https' if args.ssl_certfile else 'http'}://localhost:{args.port}",flush=True)
    print("Файлы и результаты:",data_dir,flush=True)
    from app.server import create_app
    import uvicorn
    from app.host_lock import exclusive_host
    with exclusive_host(data_dir):
        app = create_app(data_dir,host=args.host,port=args.port,access_key=key,
                         max_upload_mib=args.max_upload_mib,storage_limit_gib=args.storage_limit_gib)
        # Exactly one web process owns the scheduler. Do not use --workers/reload.
        uvicorn.run(app,host=args.host,port=args.port,log_level="warning",
                    proxy_headers=args.proxy_headers,forwarded_allow_ips="127.0.0.1,::1",
                    ssl_certfile=args.ssl_certfile,ssl_keyfile=args.ssl_keyfile)


if __name__ == "__main__":
    main()
