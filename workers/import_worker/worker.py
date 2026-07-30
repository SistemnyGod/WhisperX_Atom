from __future__ import annotations

import hashlib
import json
import logging
import os
import re
import shutil
import time
from pathlib import Path
from urllib.request import Request, urlopen

from whisperx_atom.media_policy import ALLOWED_AUDIO_EXTENSIONS, MAX_UPLOAD_BYTES
from workers.media_worker.media_worker import probe_audio

LOG = logging.getLogger("whisperx-atom.import-worker")
SAFE_CHARS = re.compile(r"[^\w.()\- ]+", re.UNICODE)


def safe_filename(name: str) -> str:
    value = Path(name).name
    value = SAFE_CHARS.sub("_", value).strip(" .")
    return value or "audio-upload"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(4 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_candidate(path: Path) -> bool:
    return path.is_file() and path.suffix.lower() in ALLOWED_AUDIO_EXTENSIONS and not path.name.endswith(".part")


def import_payload(path: Path, staging: Path) -> dict[str, object]:
    if path.stat().st_size > MAX_UPLOAD_BYTES:
        raise ValueError("media exceeds configured size limit")
    probe_audio(path)
    digest = sha256_file(path)
    target_dir = staging / digest
    target_dir.mkdir(parents=True, exist_ok=True)
    target = target_dir / safe_filename(path.name)
    if path.resolve() != target.resolve():
        path.replace(target)
    return {
        "original_name": path.name,
        "source_type": "hot-folder",
        "source_path": str(target),
        "storage_key": str(target),
        "size_bytes": target.stat().st_size,
        "sha256": digest,
    }


def post_import(api_url: str, token: str, payload: dict[str, object]) -> dict:
    body = json.dumps(payload).encode("utf-8")
    request = Request(api_url.rstrip("/") + "/api/internal/imports", data=body, method="POST", headers={
        "Content-Type": "application/json",
        "X-Import-Worker-Token": token,
    })
    with urlopen(request, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


class HotFolderImporter:
    def __init__(self) -> None:
        self.inbox = Path(os.getenv("INBOX_ROOT", "/data/inbox"))
        self.staging = Path(os.getenv("STAGING_ROOT", "/data/staging"))
        self.archive = Path(os.getenv("ARCHIVE_ROOT", "/data/archive"))
        self.rejected = Path(os.getenv("REJECTED_ROOT", "/data/rejected"))
        self.api_url = os.getenv("API_URL", "http://api:8080")
        self.token = os.getenv("IMPORT_WORKER_TOKEN", "")
        self.interval = float(os.getenv("IMPORT_INTERVAL_SECONDS", "5"))
        self._stable: dict[str, tuple[int, int, int]] = {}

    def scan_once(self) -> int:
        self.inbox.mkdir(parents=True, exist_ok=True)
        processed = 0
        current: set[str] = set()
        for path in sorted(self.inbox.iterdir()):
            if not path.is_file():
                continue
            current.add(str(path))
            try:
                stat = path.stat()
            except FileNotFoundError:
                continue
            key = str(path)
            previous = self._stable.get(key)
            count = previous[2] + 1 if previous and previous[:2] == (stat.st_size, stat.st_mtime_ns) else 1
            self._stable[key] = (stat.st_size, stat.st_mtime_ns, count)
            if count < 2:
                continue
            self._stable.pop(key, None)
            try:
                if not is_candidate(path):
                    self._reject(path, "unsupported_extension")
                    continue
                payload = import_payload(path, self.staging)
                result = post_import(self.api_url, self.token, payload)
                self._archive(Path(payload["storage_key"]), str(payload["sha256"]), str(payload["original_name"]))
                LOG.info("registered %s as %s", payload["original_name"], result)
                processed += 1
            except Exception as exc:  # keep scanning other files
                LOG.exception("failed to import %s", path)
                if path.exists():
                    self._reject(path, type(exc).__name__ + ":" + str(exc))
        self._stable = {key: value for key, value in self._stable.items() if key in current}
        return processed

    def _archive(self, staged: Path, digest: str, name: str) -> None:
        target = self.archive / digest / safe_filename(name)
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists():
            shutil.copy2(staged, target)

    def _reject(self, path: Path, reason: str) -> None:
        target = self.rejected / safe_filename(path.name)
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            target = target.with_name(target.stem + "-" + str(int(time.time())) + target.suffix)
        path.replace(target)
        target.with_suffix(target.suffix + ".reason.txt").write_text(reason, encoding="utf-8")


def run() -> None:
    logging.basicConfig(level=os.getenv("LOG_LEVEL", "INFO"))
    importer = HotFolderImporter()
    while True:
        importer.scan_once()
        time.sleep(importer.interval)


if __name__ == "__main__":
    run()
