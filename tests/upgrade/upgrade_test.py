#!/usr/bin/env python3
"""H5 (docs/roadmap-next.md): тест обновления сервера.

1. Собирает Watcher.Server прошлого релиза (последний тег vX.Y.Z, не равный
   текущей версии из Directory.Build.props) и текущего кода.
2. Поднимает старый сервер, заливает данные через его API.
3. Останавливает его, поднимает текущий на тех же данных — миграции, вход,
   хосты, записи, файлы (sha256), консоль, журнал доступа должны быть на месте.
4. Текущим бинарником: --backup --include-blobs → --restore в чистый каталог →
   сервер на восстановленных данных, те же проверки.

Запуск из корня репозитория: python3 tests/upgrade/upgrade_test.py [--prev-tag vX.Y.Z]
Нужны git (с тегами), dotnet SDK 8, свободные порты 5198/5199.
"""
import argparse
import hashlib
import http.cookiejar
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ADMIN_PASSWORD = "upgrade-test-pass-123"
VIEWER_PASSWORD = "viewer-test-pass-123"
VIDEO = [("upg-pc-1", b"video-one " * 100), ("upg-pc-2", b"video-two " * 300)]
TERM_OUT = b"Script started\n$ uname -a\nLinux upg-srv\n"
TERM_TIMING = b"0.1 15\n0.2 16\n"


def log(msg):
    print(f"[upgrade-test] {msg}", flush=True)


def run(cmd, **kw):
    log("$ " + " ".join(cmd))
    return subprocess.run(cmd, check=True, cwd=kw.pop("cwd", ROOT), **kw)


def current_version():
    props = open(os.path.join(ROOT, "Directory.Build.props"), encoding="utf-8").read()
    # Раздельные версии (0.14): сервер — ServerVersion; у старых релизов — Version.
    m = re.search(r"<ServerVersion>([^<]+)</ServerVersion>", props) or re.search(r"<Version>([^<]+)</Version>", props)
    return m.group(1)


def previous_tag():
    """Последний релизный тег-предок HEAD, кроме тега на самом HEAD.

    Релизный PR (версия уже поднята, тега ещё нет) и main после релиза (тег на
    более раннем коммите) — оба сравниваются с последним выпущенным релизом.
    """
    def git(*a):
        return subprocess.run(["git", *a], cwd=ROOT, check=True, capture_output=True, text=True).stdout.split()
    at_head = set(git("tag", "--points-at", "HEAD"))
    for t in git("tag", "--merged", "HEAD", "--sort=-v:refname"):
        if re.fullmatch(r"v\d+\.\d+\.\d+", t) and t not in at_head:
            return t
    sys.exit("нет тега прошлого релиза — нужен git fetch --tags (в CI: fetch-depth: 0)")


def publish(src_root, out):
    run(["dotnet", "publish", os.path.join(src_root, "src", "Watcher.Server"), "-c", "Release", "-o", out, "-v", "quiet", "--nologo"])


class Server:
    def __init__(self, bin_dir, data_dir, port):
        self.bin_dir, self.data_dir, self.port = bin_dir, data_dir, port
        self.base = f"http://127.0.0.1:{port}"
        self.proc = None

    def env(self):
        e = dict(os.environ)
        e.update({
            "Server__DataDir": self.data_dir,
            "Server__DbPath": os.path.join(self.data_dir, "watcher.db"),
            "Server__AdminUser": "admin",
            "Server__AdminPassword": ADMIN_PASSWORD,
            "Server__CookieSecure": "false",
            "Server__AllowAnonymousAgents": "true",
            "Kestrel__Endpoints__Http__Url": self.base,
        })
        return e

    def cli(self, *args):
        run(["dotnet", os.path.join(self.bin_dir, "Watcher.Server.dll"), *args], env=self.env(), cwd=self.bin_dir)

    def start(self):
        self.log = open(os.path.join(self.data_dir, f"server-{self.port}.log"), "ab")
        self.proc = subprocess.Popen(["dotnet", os.path.join(self.bin_dir, "Watcher.Server.dll")],
                                     cwd=self.bin_dir, env=self.env(), stdout=self.log, stderr=subprocess.STDOUT)
        for _ in range(120):
            if self.proc.poll() is not None:
                raise RuntimeError(f"сервер упал при старте, лог: {self.log.name}")
            try:
                urllib.request.urlopen(self.base + "/login.html", timeout=2).read()
                log(f"сервер {self.bin_dir} поднят на {self.base}")
                return
            except (urllib.error.URLError, ConnectionError):
                time.sleep(0.5)
        raise RuntimeError("сервер не поднялся за 60 с")

    def stop(self):
        self.proc.terminate()
        self.proc.wait(timeout=30)
        self.log.close()


class Client:
    def __init__(self, base, user=None, password=None):
        self.base = base
        self.op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        if user:
            status, _ = self.json("POST", "/api/login", {"username": user, "password": password})
            assert status == 200, f"вход {user}: {status}"

    def request(self, method, path, body=None, headers=None):
        req = urllib.request.Request(self.base + path, method=method, data=body,
                                     headers={"X-Requested-With": "XMLHttpRequest", **(headers or {})})
        try:
            with self.op.open(req, timeout=30) as r:
                return r.status, r.read()
        except urllib.error.HTTPError as e:
            return e.code, e.read()

    def json(self, method, path, data=None):
        status, raw = self.request(method, path, None if data is None else json.dumps(data).encode(),
                                   {"Content-Type": "application/json"})
        return status, (json.loads(raw) if raw else None)

    def multipart(self, path, parts):
        boundary = uuid.uuid4().hex
        body = b""
        for name, filename, data in parts:
            body += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{filename}\"\r\n"
                     f"Content-Type: application/octet-stream\r\n\r\n").encode() + data + b"\r\n"
        body += f"--{boundary}--\r\n".encode()
        return self.request("POST", path, body, {"Content-Type": f"multipart/form-data; boundary={boundary}"})


def seed(base):
    """Данные через API старого сервера. Возвращает ожидаемое для проверки."""
    admin = Client(base, "admin", ADMIN_PASSWORD)
    status, group = admin.json("POST", "/api/groups", {"name": "Бухгалтерия"})
    assert status == 200, f"группа: {status}"
    status, _ = admin.json("POST", "/api/users", {"username": "upg-viewer", "role": "viewer", "password": VIEWER_PASSWORD})
    assert status == 200, f"пользователь: {status}"

    agent = Client(base)
    now = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    for host, data in VIDEO:
        status, _ = agent.json("POST", "/api/heartbeat", {"host": host, "ip": "10.9.0.1", "version": "0.0.1", "droppedSegments": 0})
        assert status == 200, f"heartbeat {host}: {status}"
        meta = json.dumps({"host": host, "startedAt": now, "endedAt": now, "durationSec": 3, "frames": 6, "motionEvents": 1, "startReason": "motion"}).encode()
        name = f"{host}_upgrade.mp4"
        status, raw = agent.multipart("/api/upload", [("video", name, data), ("meta", name + ".json", meta)])
        assert status == 200, f"upload {host}: {status} {raw[:200]}"
    tmeta = json.dumps({"kind": "terminal-output", "host": "upg-srv", "sessionUser": "root", "startedAt": now, "endedAt": now,
                        "durationSec": 1, "exitCode": 0, "outFile": "upg.out", "timingFile": "upg.timing"}).encode()
    status, raw = agent.multipart("/api/upload-terminal", [("meta", "meta.json", tmeta), ("out", "upg.out", TERM_OUT), ("timing", "upg.timing", TERM_TIMING)])
    assert status == 200, f"upload-terminal: {status} {raw[:200]}"

    # Просмотр видео — запись в журнале доступа, которая должна пережить обновление.
    status, segs = admin.json("GET", "/api/segments")
    assert status == 200 and len(segs) == len(VIDEO), f"сегменты на старом сервере: {status} {segs}"
    admin.request("GET", f"/api/segments/{segs[0]['id']}/video")
    log("данные залиты: 2 хоста с видео, 1 с консолью, группа, viewer, просмотр в журнале")
    return {"group": group["id"]}


def verify(base, label, expected):
    status, _ = urllib.request.urlopen(base + "/healthz").status, None
    assert status == 200, f"{label}: /healthz {status}"
    admin = Client(base, "admin", ADMIN_PASSWORD)
    Client(base, "upg-viewer", VIEWER_PASSWORD)   # вход viewer'а — пароль пережил

    status, inv = admin.json("GET", "/api/inventory")
    assert status == 200, f"{label}: inventory {status}"
    names = {h["name"] for h in inv}
    assert {"upg-pc-1", "upg-pc-2", "upg-srv"} <= names, f"{label}: хосты {names}"

    status, segs = admin.json("GET", "/api/segments")
    assert status == 200 and len(segs) == len(VIDEO), f"{label}: сегменты {status} {segs}"
    for host, data in VIDEO:
        seg = next(s for s in segs if s["host"] == host)
        status, body = admin.request("GET", f"/api/segments/{seg['id']}/video")
        assert status == 200 and hashlib.sha256(body).digest() == hashlib.sha256(data).digest(), f"{label}: видео {host} не совпало ({status})"

    status, term = admin.json("GET", "/api/terminal-sessions")
    out = next(t for t in term if t["host"] == "upg-srv" and t["hasOutput"])
    status, body = admin.request("GET", f"/api/terminal-sessions/{out['id']}/out")
    assert status == 200 and body == TERM_OUT, f"{label}: вывод консоли не совпал ({status})"

    status, groups = admin.json("GET", "/api/groups")
    assert any(g["id"] == expected["group"] and g["name"] == "Бухгалтерия" for g in groups), f"{label}: группа {groups}"
    status, audit = admin.json("GET", "/api/audit?action=segment.video")
    assert status == 200 and audit["total"] >= 1, f"{label}: просмотр видео пропал из журнала"
    log(f"{label}: OK — вход, хосты, видео (sha256), консоль, группа, журнал доступа")


def schema_versions(db_path):
    import sqlite3
    c = sqlite3.connect(db_path)
    try:
        return c.execute("SELECT MAX(version), COUNT(*) FROM schema_migrations").fetchone()
    finally:
        c.close()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--prev-tag")
    ap.add_argument("--keep", action="store_true", help="не удалять рабочий каталог")
    args = ap.parse_args()

    cur = current_version()
    prev = args.prev_tag or previous_tag()
    work = tempfile.mkdtemp(prefix="watcher-upgrade-")
    log(f"прошлый релиз {prev} → текущий код {cur}, рабочий каталог {work}")
    servers = []
    try:
        prev_src = os.path.join(work, "prev-src")
        os.makedirs(prev_src)
        archive = subprocess.run(["git", "archive", prev], cwd=ROOT, check=True, capture_output=True).stdout
        subprocess.run(["tar", "-x", "-C", prev_src], input=archive, check=True)
        publish(prev_src, os.path.join(work, "prev-bin"))
        publish(ROOT, os.path.join(work, "cur-bin"))

        data = os.path.join(work, "data")
        os.makedirs(data)
        old = Server(os.path.join(work, "prev-bin"), data, 5199)
        servers.append(old)
        old.start()
        expected = seed(old.base)
        old.stop()
        servers.remove(old)
        before = schema_versions(os.path.join(data, "watcher.db"))
        log(f"схема после {prev}: v{before[0]}")

        new = Server(os.path.join(work, "cur-bin"), data, 5199)
        servers.append(new)
        new.start()
        verify(new.base, f"обновление {prev} → {cur}", expected)
        new.stop()
        servers.remove(new)
        after = schema_versions(os.path.join(data, "watcher.db"))
        log(f"схема после обновления: v{after[0]} (миграций {after[1]})")
        assert after[0] >= before[0], "версия схемы уменьшилась"

        backups = os.path.join(work, "backups")
        new.cli("--backup", backups, "--include-blobs")
        backup_dir = os.path.join(backups, os.listdir(backups)[0])
        restored = os.path.join(work, "restored")
        os.makedirs(restored)
        target = Server(os.path.join(work, "cur-bin"), restored, 5198)
        target.cli("--restore", backup_dir)
        servers.append(target)
        target.start()
        verify(target.base, "резервная копия → восстановление", expected)
        target.stop()
        servers.remove(target)
        log("ВСЁ OK")
    finally:
        for s in servers:
            try:
                s.stop()
            except Exception:
                pass
        if args.keep:
            log(f"рабочий каталог оставлен: {work}")
        else:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
