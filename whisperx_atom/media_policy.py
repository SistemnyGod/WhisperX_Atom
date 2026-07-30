from __future__ import annotations

import os

CHUNK_SIZE = 4 * 1024 * 1024
MAX_UPLOAD_BYTES = int(os.getenv("MAX_UPLOAD_BYTES", str(8 * 1024 * 1024 * 1024)))
MAX_DURATION_MS = int(os.getenv("MAX_MEDIA_DURATION_MS", str(4 * 60 * 60 * 1000)))
ALLOWED_AUDIO_EXTENSIONS = frozenset({".wav", ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus"})
VIDEO_EXTENSIONS = frozenset({".mp4", ".mkv", ".mov", ".webm", ".avi"})

