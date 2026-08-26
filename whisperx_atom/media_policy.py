from __future__ import annotations

import os

CHUNK_SIZE = 4 * 1024 * 1024
MAX_UPLOAD_BYTES = int(os.getenv("MAX_UPLOAD_BYTES", str(8 * 1024 * 1024 * 1024)))
# No implicit recording duration limit.  This value is shared by import/API
# policy code and remains configurable as an explicit deployment safety cap.
# A value of 0 means recordings of any duration are accepted (subject to the
# byte limit and available storage).
try:
    MAX_DURATION_MS = max(0, int(os.getenv("MAX_MEDIA_DURATION_MS", "0")))
except (TypeError, ValueError):
    # The duration cap is optional; an invalid value must not block startup.
    MAX_DURATION_MS = 0
ALLOWED_AUDIO_EXTENSIONS = frozenset({".wav", ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus"})
VIDEO_EXTENSIONS = frozenset({".mp4", ".mkv", ".mov", ".webm", ".avi"})

