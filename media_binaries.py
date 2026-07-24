from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path


class MissingBinaryError(RuntimeError):
    """Raised when a required external binary cannot be located."""


def _unique_paths(paths: list[Path]) -> list[Path]:
    seen: set[str] = set()
    unique: list[Path] = []
    for path in paths:
        key = str(path.resolve(strict=False)).lower()
        if key in seen:
            continue
        seen.add(key)
        unique.append(path)
    return unique


def _candidate_roots(extra_roots: list[Path] | None = None) -> list[Path]:
    roots: list[Path] = []
    cwd = Path.cwd()
    module_dir = Path(__file__).resolve().parent
    roots.extend([cwd, module_dir])

    if extra_roots:
        roots.extend(Path(root) for root in extra_roots)

    if getattr(sys, "frozen", False):
        exe_dir = Path(sys.executable).resolve().parent
        roots.extend([exe_dir, exe_dir.parent])
        meipass = getattr(sys, "_MEIPASS", None)
        if meipass:
            roots.append(Path(meipass))

    return _unique_paths(roots)


def _binary_candidates(binary_name: str, root: Path) -> list[Path]:
    file_name = binary_name if binary_name.lower().endswith(".exe") else f"{binary_name}.exe"
    relative_dirs = [
        Path("."),
        Path("bin"),
        Path("ffmpeg"),
        Path("ffmpeg/bin"),
        Path("tools"),
        Path("tools/ffmpeg"),
        Path("tools/ffmpeg/bin"),
        Path("third_party/ffmpeg"),
        Path("third_party/ffmpeg/bin"),
    ]
    return [(root / rel / file_name).resolve(strict=False) for rel in relative_dirs]


def resolve_binary(binary_name: str, extra_roots: list[Path] | None = None) -> Path | None:
    env_var = f"{binary_name.upper()}_PATH"
    explicit_path = os.getenv(env_var, "").strip()
    if explicit_path:
        candidate = Path(explicit_path).expanduser()
        if candidate.is_dir():
            candidate = candidate / f"{binary_name}.exe"
        if candidate.exists():
            return candidate.resolve()

    bin_dir = os.getenv("FFMPEG_BIN_DIR", "").strip()
    if bin_dir:
        candidate = Path(bin_dir).expanduser() / f"{binary_name}.exe"
        if candidate.exists():
            return candidate.resolve()

    found = shutil.which(binary_name) or shutil.which(f"{binary_name}.exe")
    if found:
        return Path(found).resolve()

    for root in _candidate_roots(extra_roots):
        for candidate in _binary_candidates(binary_name, root):
            if candidate.exists():
                return candidate.resolve()
    return None


def _missing_binary_message(binary_name: str, extra_roots: list[Path] | None = None) -> str:
    searched_roots = [str(path) for path in _candidate_roots(extra_roots)]
    places = "\n".join(f"- {root}" for root in searched_roots)
    env_var = f"{binary_name.upper()}_PATH"
    return (
        f"Не найден внешний инструмент `{binary_name}.exe`.\n"
        f"Он нужен для чтения и предобработки аудио.\n\n"
        f"Проверьте один из вариантов:\n"
        f"- добавьте `{binary_name}.exe` в PATH;\n"
        f"- укажите полный путь в переменной окружения `{env_var}`;\n"
        f"- либо положите `ffmpeg.exe` и `ffprobe.exe` рядом с приложением или в папку `ffmpeg\\bin`.\n\n"
        f"Папки, где приложение уже пыталось искать бинарники:\n{places}"
    )


def require_binary(binary_name: str, extra_roots: list[Path] | None = None) -> Path:
    resolved = resolve_binary(binary_name, extra_roots=extra_roots)
    if resolved is None:
        raise MissingBinaryError(_missing_binary_message(binary_name, extra_roots=extra_roots))
    return resolved


def require_ffmpeg_tools(
    *,
    require_ffprobe: bool = False,
    extra_roots: list[Path] | None = None,
) -> tuple[Path, Path | None]:
    ffmpeg_path = require_binary("ffmpeg", extra_roots=extra_roots)
    ffprobe_path = require_binary("ffprobe", extra_roots=extra_roots) if require_ffprobe else None
    return ffmpeg_path, ffprobe_path


def media_has_audio_stream(path: str | Path, extra_roots: list[Path] | None = None) -> bool:
    ffprobe_path = require_binary("ffprobe", extra_roots=extra_roots)
    command = [
        str(ffprobe_path),
        "-v",
        "error",
        "-select_streams",
        "a:0",
        "-show_entries",
        "stream=codec_type",
        "-of",
        "csv=p=0",
        str(path),
    ]
    try:
        output = subprocess.check_output(command, stderr=subprocess.STDOUT, timeout=30)
    except Exception:
        return False
    return any(line.strip().lower() == "audio" for line in output.decode("utf-8", errors="ignore").splitlines())
