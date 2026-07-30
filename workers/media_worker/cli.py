from __future__ import annotations

import json
import os
from pathlib import Path

from .media_worker import prepare_media


def main() -> None:
    root = Path(os.getenv("MEDIA_ROOT", "/data"))
    inbox = root / "staging"
    output = root / "derived"
    inbox.mkdir(parents=True, exist_ok=True)
    output.mkdir(parents=True, exist_ok=True)
    # The durable NATS consumer is added after the tusd hook is enabled. This
    # CLI remains useful for deterministic media smoke tests.
    for source in sorted(inbox.iterdir()):
        if not source.is_file() or source.name.endswith(".part"):
            continue
        derivatives = prepare_media(source, output / source.stem)
        (output / source.stem / "manifest.json").write_text(
            json.dumps(
                {
                    "sha256": derivatives.sha256,
                    "duration_ms": derivatives.duration_ms,
                    "archive_flac": str(derivatives.archive_flac),
                    "preview_opus": str(derivatives.preview_opus),
                    "asr_wav": str(derivatives.asr_wav),
                },
                ensure_ascii=False,
                indent=2,
            ),
            encoding="utf-8",
        )


if __name__ == "__main__":
    main()

