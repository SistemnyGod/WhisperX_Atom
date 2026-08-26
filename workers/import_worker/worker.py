from __future__ import annotations

import hashlib
import json
import logging
import os
import re
import shutil
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from whisperx_atom.media_policy import ALLOWED_AUDIO_EXTENSIONS, MAX_UPLOAD_BYTES
from workers.media_worker.media_worker import probe_audio
from workers.runtime_heartbeat import start_sync_heartbeat

LOG = logging.getLogger("whisperx-atom.import-worker")
SAFE_CHARS = re.compile(r"[^\w.()\- ]+", re.UNICODE)


class TransientImportError(RuntimeError):
    """The source is valid, but the import dependency should be retried."""


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


def atomic_copy(source: Path, target: Path, *, expected_sha256: str | None = None) -> None:
    """Copy across filesystems without exposing a partial target."""
    target.parent.mkdir(parents=True, exist_ok=True)
    expected_size = source.stat().st_size
    if target.exists():
        if target.stat().st_size == expected_size and (
            expected_sha256 is None or sha256_file(target) == expected_sha256
        ):
            return
        raise FileExistsError(f"target already exists with different content: {target}")

    temporary = target.with_name(f".{target.name}.{os.getpid()}.{time.time_ns()}.part")
    try:
        with source.open("rb") as input_file, temporary.open("xb") as output_file:
            shutil.copyfileobj(input_file, output_file, length=4 * 1024 * 1024)
            output_file.flush()
            os.fsync(output_file.fileno())
        if temporary.stat().st_size != expected_size:
            raise OSError("copied file size mismatch")
        if expected_sha256 is not None and sha256_file(temporary) != expected_sha256:
            raise OSError("copied file checksum mismatch")
        shutil.copystat(source, temporary)
        temporary.replace(target)
    finally:
        temporary.unlink(missing_ok=True)


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
        atomic_copy(path, target, expected_sha256=digest)
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
    try:
        with urlopen(request, timeout=30) as response:
            return json.loads(response.read().decode("utf-8"))
    except HTTPError as exc:
        # Do not move a valid source file to rejected/ for an infrastructure
        # outage.  Keep response bodies out of logs because they can contain
        # deployment details or accidentally echoed credentials.
        if exc.code >= 500 or exc.code == 429:
            raise TransientImportError(f"import API unavailable ({exc.code})") from exc
        raise RuntimeError(f"import API rejected request ({exc.code})") from exc
    except (URLError, TimeoutError, ConnectionError) as exc:
        raise TransientImportError("import API connection failed") from exc


class HotFolderImporter:
    def __init__(self) -> None:
        self.inbox = Path(os.getenv("INBOX_ROOT", "/data/inbox"))
        self.staging = Path(os.getenv("STAGING_ROOT", "/data/staging"))
        self.archive = Path(os.getenv("ARCHIVE_ROOT", "/data/archive"))
        self.rejected = Path(os.getenv("REJECTED_ROOT", "/data/rejected"))
        self.api_url = os.getenv("API_URL", "http://api:8080")
        self.token = os.getenv("IMPORT_WORKER_TOKEN", "")
        self.interval = float(os.getenv("IMPORT_INTERVAL_SECONDS", "5"))
        self.batch_size = max(1, int(os.getenv("IMPORT_BATCH_SIZE", "8")))
        self.max_stable_paths = max(self.batch_size * 4, int(os.getenv("IMPORT_MAX_STABLE_PATHS", "2048")))
        self._stable: dict[str, tuple[int, int, int]] = {}
        self._retry: dict[str, tuple[int, float]] = {}
        self._retry_delays = (5.0, 15.0, 30.0, 60.0, 300.0)

    def scan_once(self) -> int:
        self.inbox.mkdir(parents=True, exist_ok=True)
        processed = 0
        inspected = 0
        try:
            entries = os.scandir(self.inbox)
        except OSError as exc:
            # A hot folder can contain thousands of files. Do not crash the
            # service when Windows/Docker cannot enumerate it in one attempt.
            LOG.warning("unable to scan hot folder %s: %s", self.inbox, exc)
            return 0

        with entries:
            for entry in entries:
                if inspected >= self.batch_size:
                    break
                inspected += 1
                try:
                    if not entry.is_file(follow_symlinks=False):
                        continue
                    path = Path(entry.path)
                    stat = entry.stat(follow_symlinks=False)
                except (FileNotFoundError, OSError):
                    continue
                key = str(path)
                retry = self._retry.get(key)
                if retry and time.monotonic() < retry[1]:
                    continue
                previous = self._stable.get(key)
                count = previous[2] + 1 if previous and previous[:2] == (stat.st_size, stat.st_mtime_ns) else 1
                self._stable[key] = (stat.st_size, stat.st_mtime_ns, count)
                if path.name.endswith(".part") or count < 2:
                    continue
                self._stable.pop(key, None)
                try:
                    if not is_candidate(path):
                        self._reject(path, "unsupported_extension")
                        continue
                    payload = import_payload(path, self.staging)
                    result = post_import(self.api_url, self.token, payload)
                    self._archive(Path(payload["storage_key"]), str(payload["sha256"]), str(payload["original_name"]))
                    path.unlink(missing_ok=True)
                    self._retry.pop(key, None)
                    LOG.info("registered %s as %s", payload["original_name"], result)
                    processed += 1
                except TransientImportError as exc:
                    attempts = (self._retry.get(key, (0, 0.0))[0] + 1)
                    delay = self._retry_delays[min(attempts - 1, len(self._retry_delays) - 1)]
                    self._retry[key] = (attempts, time.monotonic() + delay)
                    LOG.warning("transient import failure for %s; retained for retry in %.0fs (%s)", path, delay, type(exc).__name__)
                except Exception as exc:  # keep scanning other files
                    LOG.exception("failed to import %s", path)
                    if path.exists():
                        self._reject(path, type(exc).__name__ + ":" + str(exc))
                    self._retry.pop(key, None)

        # The worker is intentionally bounded. Keep the stability cache bounded
        # too, otherwise a noisy hot folder could become an unbounded RAM sink.
        while len(self._stable) > self.max_stable_paths:
            self._stable.pop(next(iter(self._stable)))
        return processed

    def _archive(self, staged: Path, digest: str, name: str) -> None:
        target = self.archive / digest / safe_filename(name)
        atomic_copy(staged, target, expected_sha256=digest)

    def _reject(self, path: Path, reason: str) -> None:
        target = self.rejected / safe_filename(path.name)
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            target = target.with_name(target.stem + "-" + str(int(time.time())) + target.suffix)
        atomic_copy(path, target)
        path.unlink(missing_ok=True)
        target.with_suffix(target.suffix + ".reason.txt").write_text(reason, encoding="utf-8")


def run() -> None:
    logging.basicConfig(level=os.getenv("LOG_LEVEL", "INFO"))
    importer = HotFolderImporter()
    heartbeat_stop, _ = start_sync_heartbeat(
        "import-worker",
        capabilities=lambda: {"inbox": str(importer.inbox), "importPipeline": "ready"},
    )
    try:
        while True:
            importer.scan_once()
            time.sleep(importer.interval)
    finally:
        heartbeat_stop.set()


if __name__ == "__main__":
    run()
