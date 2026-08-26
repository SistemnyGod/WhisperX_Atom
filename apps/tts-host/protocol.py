from __future__ import annotations

from dataclasses import dataclass
import re

from text_normalizer import normalize_text

PROTOCOL_VERSION = 1
ALLOWED_OPERATIONS = {"ping", "synthesize", "shutdown"}
ALLOWED_SPEAKERS = {"aidar", "eugene", "baya", "kseniya", "xenia"}
ALLOWED_SAMPLE_RATES = {24000, 48000}


class ProtocolError(ValueError):
    def __init__(self, code: str, message: str = "invalid request") -> None:
        super().__init__(message)
        self.code = code


@dataclass(frozen=True)
class Request:
    request_id: str
    operation: str
    text: str = ""
    speaker: str = "aidar"
    sample_rate: int = 48000
    cpu_threads: int = 4


def parse_request(payload: object) -> Request:
    if not isinstance(payload, dict):
        raise ProtocolError("TTS_PROTOCOL_INVALID")
    if payload.get("schemaVersion") != PROTOCOL_VERSION:
        raise ProtocolError("TTS_PROTOCOL_VERSION_MISMATCH")
    request_id = payload.get("id")
    operation = payload.get("op")
    if not isinstance(request_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", request_id):
        raise ProtocolError("TTS_PROTOCOL_INVALID")
    if operation not in ALLOWED_OPERATIONS:
        raise ProtocolError("TTS_PROTOCOL_INVALID")
    # Paths, commands, model overrides and arbitrary expressions are never
    # accepted from the parent process.
    forbidden = {"modelPath", "outputPath", "audioPath", "command", "python", "expression"}
    if forbidden.intersection(payload):
        raise ProtocolError("TTS_PROTOCOL_INVALID")
    if operation != "synthesize":
        return Request(request_id, operation)
    text = payload.get("text")
    if not isinstance(text, str):
        raise ProtocolError("TTS_TEXT_INVALID")
    text = normalize_text(text)
    if not 1 <= len(text) <= 600:
        raise ProtocolError("TTS_TEXT_INVALID")
    speaker = payload.get("speaker", "aidar")
    if speaker not in ALLOWED_SPEAKERS:
        raise ProtocolError("TTS_VOICE_INVALID")
    sample_rate = payload.get("sampleRate", 48000)
    if sample_rate not in ALLOWED_SAMPLE_RATES:
        raise ProtocolError("TTS_SAMPLE_RATE_INVALID")
    threads = payload.get("cpuThreads", 4)
    if not isinstance(threads, int) or not 1 <= threads <= 32:
        raise ProtocolError("TTS_CPU_THREADS_INVALID")
    return Request(request_id, operation, text, speaker, sample_rate, threads)
