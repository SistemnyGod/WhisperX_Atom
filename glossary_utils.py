from __future__ import annotations

import re
from pathlib import Path


DEFAULT_GLOSSARY_FILENAME = "glossary.txt"
DEFAULT_HOTWORDS_FILENAME = "hotwords.txt"
GLOSSARY_SEPARATORS = ("=>", "->", "→", " - ", " — ")


def glossary_file_path(base_dir: Path) -> Path:
    return Path(base_dir) / DEFAULT_GLOSSARY_FILENAME


def hotwords_file_path(base_dir: Path) -> Path:
    return Path(base_dir) / DEFAULT_HOTWORDS_FILENAME


def load_glossary_text(base_dir: Path, inline_text: str = "") -> str:
    parts: list[str] = []
    file_path = glossary_file_path(base_dir)
    if file_path.exists():
        try:
            file_text = file_path.read_text(encoding="utf-8").strip()
        except Exception:
            file_text = ""
        if file_text:
            parts.append(file_text)
    inline_text = (inline_text or "").strip()
    if inline_text:
        parts.append(inline_text)
    return "\n".join(parts).strip()


def load_hotwords_text(base_dir: Path, inline_text: str = "") -> str:
    parts: list[str] = []
    file_path = hotwords_file_path(base_dir)
    if file_path.exists():
        try:
            file_text = file_path.read_text(encoding="utf-8").strip()
        except Exception:
            file_text = ""
        if file_text:
            parts.append(file_text)
    inline_text = (inline_text or "").strip()
    if inline_text:
        parts.append(inline_text)
    return "\n".join(parts).strip()


def parse_glossary_rules(raw_text: str):
    rules = []
    if not raw_text:
        return rules
    for idx, raw_line in enumerate(raw_text.splitlines(), start=1):
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue

        source = None
        target = None
        for separator in GLOSSARY_SEPARATORS:
            if separator in line:
                source, target = [part.strip() for part in line.split(separator, 1)]
                break
        if not source:
            continue

        pattern = source[3:].strip() if source.startswith("re:") else re.escape(source)
        try:
            rules.append((re.compile(pattern, flags=re.IGNORECASE), target or "", idx))
        except re.error:
            continue
    return rules


def normalize_text(text: str) -> str:
    text = re.sub(r"\s+([,.:;!?])", r"\1", text)
    text = re.sub(r"\s{2,}", " ", text)
    return text.strip()


def apply_glossary_rules(text: str, rules):
    updated = text
    replacements = 0
    for pattern, target, _line_no in rules:
        updated, count = pattern.subn(target, updated)
        replacements += int(count)
    return normalize_text(updated), replacements
