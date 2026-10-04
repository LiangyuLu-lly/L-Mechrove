"""Exercise the real PHP receiver in an isolated directory, without production data."""
import hashlib
import json
import pathlib
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


def run():
    with tempfile.TemporaryDirectory(prefix="lmechrevo-log-test-") as temporary:
        root = pathlib.Path(temporary)
        shutil.copyfile(pathlib.Path(__file__).with_name("log.php"), root / "log.php")
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        with subprocess.Popen(["php", "-S", f"127.0.0.1:{port}", "-t", str(root)],
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) as server:
            url = f"http://127.0.0.1:{port}/log.php"
            requests = 0

            def request(data=None, method="POST"):
                nonlocal requests
                body = json.dumps(data, ensure_ascii=False).encode() if data is not None else None
                req = urllib.request.Request(url, body, {"Content-Type": "application/json"}, method=method)
                try:
                    response = urllib.request.urlopen(req, timeout=5)
                except urllib.error.HTTPError as error:
                    response = error
                with response:
                    requests += 1
                    return response.status, json.loads(response.read())

            try:
                for _ in range(50):
                    try:
                        status, _ = request(method="GET")
                        assert status == 405
                        break
                    except urllib.error.URLError:
                        time.sleep(0.1)
                else:
                    raise AssertionError("PHP test server did not start")
                original = {"id": "testinstallation", "ver": "0.290.6", "batch": "a" * 32,
                            "log": "真实 PHP 接收测试😀\nfirst failure"}
                status, receipt = request(original)
                assert status == 200 and receipt["ok"]
                assert receipt["batch"] == original["batch"]
                assert receipt["bytes"] == len(original["log"].encode())
                assert receipt["sha256"] == hashlib.sha256(original["log"].encode()).hexdigest()
                path = root / "data/logs/testinstallation.txt"
                before = path.read_bytes()
                status, receipt = request(original)
                assert status == 200 and receipt["duplicate"] and path.read_bytes() == before
                status, _ = request(dict(original, log="different content"))
                assert status == 409 and path.read_bytes() == before
                status, _ = request(dict(original, batch="b" * 32, log="second failure"))
                assert status == 200
                history = path.read_text()
                assert "first failure" in history and "second failure" in history
                status, _ = request({"id": "testinstallation", "log": "legacy client evidence"})
                assert status == 200 and "first failure" in path.read_text()
                status, _ = request(dict(original, batch="c" * 32, log="中" * 40000))
                assert status == 413
                status, _ = request(dict(original, id=[]))
                assert status == 400
                status, _ = request(dict(original, batch="../../outside"))
                assert status == 400
                for index in range(4):
                    status, _ = request(dict(original, batch=f"{index:032x}", log="界" * 20000 + str(index)))
                    assert status == 200
                assert path.stat().st_size <= 98304
                assert path.read_text().endswith("3")
                meta = path.with_suffix('.json')
                saved_meta = meta.read_bytes()
                saved_history = path.read_bytes()
                meta.write_text('broken metadata')
                status, _ = request(dict(original, batch='d' * 32))
                assert status == 500 and path.read_bytes() == saved_history
                meta.write_bytes(saved_meta)
                print(f"PHP log receiver: {requests} HTTP cases passed (receipts, deduplication, history, bounds, legacy, validation, corrupt metadata)")
            finally:
                server.terminate()
                server.wait(timeout=5)


if __name__ == "__main__":
    run()
