import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from http.server import ThreadingHTTPServer
from urllib.error import HTTPError
from urllib.request import Request, urlopen
import zipfile

module_path = Path(__file__).resolve().parents[1] / "serve-pack-catalog.py"
spec = importlib.util.spec_from_file_location("pack_catalog_server", module_path)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PackCatalogServerTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.archive = Path(self.temp.name) / "pack.zip"
        with zipfile.ZipFile(self.archive, "w") as pack:
            pack.writestr("modrinth.index.json", json.dumps({"versionId": "V4.3.0", "dependencies": {"minecraft": "1.21.1"}}))
        self.icon = Path(self.temp.name) / "icon.png"
        self.icon.write_bytes(b"test-image")
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), module.create_handler(self.archive, self.icon, self.icon))
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.base = f"http://127.0.0.1:{self.server.server_port}"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        self.temp.cleanup()

    def test_catalog_uses_internal_version_and_correct_hash(self):
        with urlopen(self.base + "/catalog.json") as response:
            pack = json.load(response)["packs"][0]
        self.assertEqual("V4.3.0", pack["version"])
        self.assertEqual("1.21.1", pack["minecraft"])
        self.assertEqual(hashlib.sha256(self.archive.read_bytes()).hexdigest(), pack["sha256"])
        self.assertEqual("/assets/icon", pack["icon"])

    def test_complete_and_range_download(self):
        with urlopen(self.base + "/packs/starrail.zip") as response:
            self.assertEqual(self.archive.read_bytes(), response.read())
        with urlopen(Request(self.base + "/packs/starrail.zip", headers={"Range": "bytes=0-63"})) as response:
            self.assertEqual(206, response.status)
            self.assertEqual(self.archive.read_bytes()[:64], response.read())

    def test_head_and_images(self):
        with urlopen(Request(self.base + "/packs/starrail.zip", method="HEAD")) as response:
            self.assertEqual(str(self.archive.stat().st_size), response.headers["Content-Length"])
            self.assertEqual(b"", response.read())
        with urlopen(self.base + "/assets/icon") as response:
            self.assertEqual(b"test-image", response.read())

    def test_path_traversal_and_invalid_range(self):
        for path in ("/../pack.zip", "/%2e%2e/pack.zip", "/unknown"):
            with self.assertRaises(HTTPError) as error:
                urlopen(self.base + path)
            self.assertEqual(404, error.exception.code)
        with self.assertRaises(HTTPError) as error:
            urlopen(Request(self.base + "/packs/starrail.zip", headers={"Range": "bytes=999999999-"}))
        self.assertEqual(416, error.exception.code)


if __name__ == "__main__":
    unittest.main()
