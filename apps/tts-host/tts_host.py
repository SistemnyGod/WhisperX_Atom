from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import sys
import time
from pathlib import Path

from protocol import ProtocolError, parse_request
from silero_runtime import SileroRuntime

BUILD_IDENTITY = os.environ.get("WHISPERX_BUILD_IDENTITY", "")
MODEL_NAME = "v5_5_ru"
_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
_STILL_ACTIVE = 259


def _load_build_identity(root: Path) -> str:
    """Resolve the packaged identity when the parent did not inject it.

    Voice Host still supplies ``WHISPERX_BUILD_IDENTITY`` for rolling
    compatibility, but a standalone TtsHost health probe must not silently
    report an empty identity.  The build manifest is shipped beside the
    executable and contains no secrets.
    """
    if BUILD_IDENTITY.strip():
        return BUILD_IDENTITY.strip()
    manifest = root / "build-identity.json"
    try:
        payload = json.loads(manifest.read_text(encoding="utf-8"))
        value = payload.get("buildIdentity")
        return value.strip() if isinstance(value, str) else ""
    except (OSError, ValueError, TypeError):
        return ""


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


def _parent_is_alive(parent_pid: int | None) -> bool:
    """Return false when the owning host process has exited on Windows."""
    if not parent_pid or os.name != "nt":
        return True
    try:
        import ctypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        handle = kernel32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, int(parent_pid))
        if not handle:
            return False
        try:
            exit_code = ctypes.c_ulong()
            if not kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
                return False
            return int(exit_code.value) == _STILL_ACTIVE
        finally:
            kernel32.CloseHandle(handle)
    except Exception:
        # A same-user process should be queryable.  If the platform denies the
        # probe, keep the host alive and let the owning pipe decide shutdown;
        # this avoids killing TTS on a transient Windows API failure.
        return True


def run(model_path: Path, temp_root: Path, parent_pid: int | None, cpu_threads: int) -> int:
    if not model_path.is_file():
        _log("TTS_MODEL_MISSING")
        return 2
    _cleanup_old(temp_root)
    runtime: SileroRuntime | None = None
    for raw in sys.stdin:
        operation: str | None = None
        if not _parent_is_alive(parent_pid):
            _log("TTS_PARENT_EXITED")
            return 0
        try:
            # Windows PowerShell 5.1's redirected StreamWriter emits an
            # UTF-8 BOM before the first line. Accept it without relaxing
            # JSON parsing for the remainder of the protocol stream.
            raw = raw.lstrip("\ufeff")
            payload = json.loads(raw)
            request = parse_request(payload)
            operation = request.operation
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
                else:
                    runtime.configure_cpu_threads(request.cpu_threads)
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
            # Ping contains no meeting/user text, so its sanitized exception
            # message is safe and materially improves packaging diagnostics.
            detail = f":{error}" if operation == "ping" else ""
            _log(f"TTS_RUNTIME_ERROR:{type(error).__name__}{detail}")
            response = {"ok": False, "errorCode": "TTS_SYNTHESIS_FAILED"}
        print(json.dumps(response, ensure_ascii=False, separators=(",", ":")), flush=True)
    return 0


def main() -> int:
    # JSONL is UTF-8 by contract. Do not inherit the Windows console codepage
    # in a frozen process; utf-8-sig accepts both normal UTF-8 and the BOM
    # emitted by Windows PowerShell 5.1 redirected input.
    if hasattr(sys.stdin, "buffer"):
        sys.stdin = io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8-sig", newline=None)
    if hasattr(sys.stdout, "buffer"):
        sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", newline="\n", write_through=True)
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--parent-pid", type=int, default=None)
    args = parser.parse_args()
    # In a PyInstaller onedir build module files live below ``_internal``,
    # while release assets are installed beside TtsHost.exe. Resolve assets
    # from the executable directory in frozen mode so the same layout works
    # in staging, the installer and rollback copies.
    root = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parent
    global BUILD_IDENTITY
    BUILD_IDENTITY = _load_build_identity(root)
    model = root / "Models" / "silero-v5_5_ru" / "v5_5_ru.pt"
    temp = Path(os.environ.get("ATOM_TTS_TEMP_ROOT", Path.home() / "AppData" / "Local" / "WhisperXAtom" / "TTS" / "Temp"))
    return run(model, temp, args.parent_pid, 4)


if __name__ == "__main__":
    raise SystemExit(main())
