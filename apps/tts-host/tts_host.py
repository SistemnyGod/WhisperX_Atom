from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import time
from pathlib import Path

from protocol import ProtocolError, parse_request
from silero_runtime import SileroRuntime

BUILD_IDENTITY = os.environ.get("WHISPERX_BUILD_IDENTITY", "")
MODEL_NAME = "v5_5_ru"


def _log(message: str) -> None:
    # Never write request text, names or paths to stdout/stderr.
    print(message, file=sys.stderr, flush=True)


def _cleanup_old(temp_root: Path) -> None:
    try:
        now = time.time()
        for candidate in temp_root.glob("*.wav"):
            if now - candidate.stat().st_mtime > 24 * 3600:
                candidate.unlink(missing_ok=True)
    except OSError:
        pass


def run(model_path: Path, temp_root: Path, parent_pid: int | None, cpu_threads: int) -> int:
    if not model_path.is_file():
        _log("TTS_MODEL_MISSING")
        return 2
    _cleanup_old(temp_root)
    runtime: SileroRuntime | None = None
    for raw in sys.stdin:
        if parent_pid:
            try:
                if os.name == "nt":
                    import ctypes
                    # Avoid holding a process handle; failure is non-fatal.
                    ctypes.windll.kernel32.GetExitCodeProcess
            except Exception:
                pass
        try:
            payload = json.loads(raw)
            request = parse_request(payload)
            if request.operation == "ping":
                if payload.get("buildIdentity") and BUILD_IDENTITY and payload["buildIdentity"] != BUILD_IDENTITY:
                    raise ProtocolError("TTS_BUILD_IDENTITY_MISMATCH")
                if runtime is None:
                    runtime = SileroRuntime(model_path, temp_root, cpu_threads)
                    runtime.load()
                response = {"ok": True, "state": "READY", "engine": "SILERO", "model": MODEL_NAME, "buildIdentity": BUILD_IDENTITY, "modelLoadMs": runtime.model_load_ms}
            elif request.operation == "synthesize":
                if runtime is None:
                    runtime = SileroRuntime(model_path, temp_root, request.cpu_threads)
                    runtime.load()
                response = {"ok": True, "state": "READY", "engine": "SILERO", "model": MODEL_NAME, "voice": request.speaker, "buildIdentity": BUILD_IDENTITY, **runtime.synthesize(request.text, request.speaker, request.sample_rate, request.request_id)}
            else:
                response = {"ok": True, "state": "STOPPED"}
                print(json.dumps(response, ensure_ascii=False), flush=True)
                break
        except ProtocolError as error:
            response = {"ok": False, "errorCode": error.code}
        except Exception as error:
            # Preserve privacy: report only the exception class, never its
            # message because synthesis failures can include request data.
            _log(f"TTS_RUNTIME_ERROR:{type(error).__name__}")
            response = {"ok": False, "errorCode": "TTS_SYNTHESIS_FAILED"}
        print(json.dumps(response, ensure_ascii=False, separators=(",", ":")), flush=True)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--parent-pid", type=int, default=None)
    args = parser.parse_args()
    # In a PyInstaller onedir build module files live below ``_internal``,
    # while release assets are installed beside TtsHost.exe. Resolve assets
    # from the executable directory in frozen mode so the same layout works
    # in staging, the installer and rollback copies.
    root = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parent
    model = root / "Models" / "silero-v5_5_ru" / "v5_5_ru.pt"
    temp = Path(os.environ.get("ATOM_TTS_TEMP_ROOT", Path.home() / "AppData" / "Local" / "WhisperXAtom" / "TTS" / "Temp"))
    return run(model, temp, args.parent_pid, 4)


if __name__ == "__main__":
    raise SystemExit(main())
