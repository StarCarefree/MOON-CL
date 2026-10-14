"""Small LAN pack catalog/artifact server. No third-party dependencies; Python 3.10+.
Run --help. Bind to 127.0.0.1 by default; use a trusted HTTPS reverse proxy for public hosting.
"""
import argparse
import hashlib
import json
import mimetypes
import re
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit


def create_handler(archive: Path, icon: Path | None = None, background: Path | None = None):
    suffix = archive.suffix.lower()
    if suffix not in (".zip", ".mrpack"):
        raise ValueError("Archive must be ZIP or MRPACK")
    version, minecraft = "unknown", ""
    with zipfile.ZipFile(archive) as pack:
        names = pack.namelist()
        index = next((n for n in names if n == "modrinth.index.json"), None)
        if index:
            if pack.getinfo(index).file_size > 4 * 1024 * 1024:
                raise ValueError("Oversized pack metadata")
            data = json.loads(pack.read(index))
            version = data.get("versionId", version)
            minecraft = data.get("dependencies", {}).get("minecraft", "")
    digest = hashlib.sha256()
    with archive.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    files = {f"/packs/starrail{suffix}": archive}
    if icon:
        files["/assets/icon"] = icon
    if background:
        files["/assets/background"] = background
    catalog = json.dumps({"packs": [{
        "id": "starrail-harpy-express", "name": "StarRailHarpyExpress",
        "description": "SRE 哈比列车 · 服务器预设整合包", "version": version,
        "minecraft": minecraft, "downloadUrl": f"/packs/starrail{suffix}",
        "fileName": f"StarRailHarpyExpress{suffix}", "sha256": digest.hexdigest(),
        "icon": "/assets/icon" if icon else "",
        "background": "/assets/background" if background else "",
        "shareUrl": "https://pan.quark.cn/s/e3f623dd58d6",
        "sharePath": "SRE哈比列车/4.4.0/公开版（不含地图）StarRailHarpyExpress V4.4.0-beta1.mrpack"
    }]}, ensure_ascii=False).encode("utf-8")

    class Handler(BaseHTTPRequestHandler):
        def do_HEAD(self):
            self.respond(False)

        def do_GET(self):
            self.respond(True)

        def respond(self, body):
            path = urlsplit(self.path).path
            if path in ("/", "/catalog.json"):
                self.send_response(200)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(catalog)))
                self.send_header("Cache-Control", "no-cache")
                self.end_headers()
                if body:
                    self.wfile.write(catalog)
                return
            target = files.get(path)
            if target is None or not target.is_file():
                self.send_error(404)
                return
            size = target.stat().st_size
            start, end = 0, size - 1
            range_header = self.headers.get("Range")
            if range_header:
                match = re.fullmatch(r"bytes=(\d+)-(\d*)", range_header)
                if not match:
                    self.send_error(416)
                    return
                start = int(match[1])
                end = min(int(match[2]), size - 1) if match[2] else size - 1
                if start > end or start >= size:
                    self.send_response(416)
                    self.send_header("Content-Range", f"bytes */{size}")
                    self.end_headers()
                    return
            self.send_response(206 if range_header else 200)
            self.send_header("Content-Type", mimetypes.guess_type(target.name)[0] or "application/octet-stream")
            self.send_header("Content-Length", str(end - start + 1))
            self.send_header("Accept-Ranges", "bytes")
            if range_header:
                self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
            self.end_headers()
            if body:
                try:
                    with target.open("rb") as stream:
                        stream.seek(start)
                        remaining = end - start + 1
                        while remaining:
                            block = stream.read(min(131072, remaining))
                            if not block:
                                break
                            self.wfile.write(block)
                            remaining -= len(block)
                except (BrokenPipeError, ConnectionResetError):
                    pass

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8091)
    parser.add_argument("--archive", type=Path, default=Path(r"G:\download\(PCL推荐使用)StarRailHarpyExpress V4.3.3-Release (1).zip"))
    parser.add_argument("--icon", type=Path)
    parser.add_argument("--background", type=Path)
    args = parser.parse_args()
    for path in (args.archive, args.icon, args.background):
        if path is not None and not path.is_file():
            parser.error(f"File does not exist: {path}")
    server = ThreadingHTTPServer((args.host, args.port), create_handler(args.archive, args.icon, args.background))
    print(f"Catalog: http://{args.host}:{server.server_port}/catalog.json", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
