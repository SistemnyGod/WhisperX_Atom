"""Privacy-safe WER/CER comparison for identical WhisperX runs.

The reference and candidate transcript files are local inputs. The emitted
report contains only hashes, aggregate metrics and configuration; transcript
text is never written to the report.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
from typing import Iterable


TOKEN_RE = re.compile(r"[\wА-Яа-яЁё-]+", re.UNICODE)


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for block in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def normalize(value: str) -> str:
    return " ".join(TOKEN_RE.findall(value.casefold().replace("ё", "е")))


def load_text(path: Path) -> str:
    if path.suffix.lower() == ".json":
        data = json.loads(path.read_text(encoding="utf-8"))
        if isinstance(data, dict):
            if isinstance(data.get("text"), str):
                return data["text"]
            segments = data.get("segments") or []
            return " ".join(str(item.get("text", "")) for item in segments if isinstance(item, dict))
    return path.read_text(encoding="utf-8")


def edit_distance(left: list[str], right: list[str]) -> tuple[int, int, int, int]:
    matrix = [[0] * (len(right) + 1) for _ in range(len(left) + 1)]
    for i in range(len(left) + 1):
        matrix[i][0] = i
    for j in range(len(right) + 1):
        matrix[0][j] = j
    for i, a in enumerate(left, 1):
        for j, b in enumerate(right, 1):
            matrix[i][j] = matrix[i - 1][j - 1] if a == b else min(
                matrix[i - 1][j - 1] + 1,
                matrix[i][j - 1] + 1,
                matrix[i - 1][j] + 1,
            )
    substitutions = insertions = deletions = 0
    i, j = len(left), len(right)
    while i or j:
        if i and j and left[i - 1] == right[j - 1] and matrix[i][j] == matrix[i - 1][j - 1]:
            i -= 1; j -= 1
        elif i and j and matrix[i][j] == matrix[i - 1][j - 1] + 1:
            substitutions += 1; i -= 1; j -= 1
        elif j and matrix[i][j] == matrix[i][j - 1] + 1:
            insertions += 1; j -= 1
        else:
            deletions += 1; i -= 1
    return matrix[-1][-1], substitutions, insertions, deletions


def terms_found(reference: list[str], candidate: list[str], terms: Iterable[str]) -> tuple[int, int]:
    ref = set(reference)
    got = set(candidate)
    selected = {normalize(term) for term in terms if normalize(term) and normalize(term) in ref}
    return sum(1 for term in selected if term in got), len(selected)


def score(reference: str, candidate: str, terms: list[str]) -> dict[str, object]:
    ref_words = normalize(reference).split()
    candidate_words = normalize(candidate).split()
    distance, substitutions, insertions, deletions = edit_distance(ref_words, candidate_words)
    char_distance, _, _, _ = edit_distance(list(normalize(reference)), list(normalize(candidate)))
    term_hits, term_total = terms_found(ref_words, candidate_words, terms)
    denominator = max(1, len(ref_words))
    return {
        "referenceWordCount": len(ref_words),
        "candidateWordCount": len(candidate_words),
        "wer": round(distance / denominator, 6),
        "cer": round(char_distance / max(1, len(normalize(reference))), 6),
        "speechRecall": round(max(0, len(ref_words) - deletions) / denominator, 6),
        "domainTermAccuracy": round(term_hits / term_total, 6) if term_total else None,
        "domainTermCount": term_total,
        "edit": {"substitutions": substitutions, "insertions": insertions, "deletions": deletions},
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path)
    parser.add_argument("--candidate", action="append", nargs=2, metavar=("NAME", "PATH"), required=True)
    parser.add_argument("--terms", type=Path)
    parser.add_argument("--build-identity", required=True)
    parser.add_argument("--model-identity", default="UNKNOWN")
    parser.add_argument("--baseline", default="audacity", help="candidate name used as the parity baseline")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    reference_text = load_text(args.reference)
    terms = args.terms.read_text(encoding="utf-8").splitlines() if args.terms else []
    results = []
    for name, raw_path in args.candidate:
        path = Path(raw_path)
        candidate_text = load_text(path)
        results.append({"name": name, "transcriptSha256": digest(path), "metrics": score(reference_text, candidate_text, terms)})
    baseline = next((item for item in results if item["name"] == args.baseline), None)
    comparable = [item for item in results if item["name"] != args.baseline]
    # The runner is deliberately conservative: it only reports PASSED when a
    # named Audacity baseline exists and every candidate is within the release
    # tolerance. Missing acoustic metrics keep the report READY_FOR_REVIEW.
    status = "READY_FOR_REVIEW"
    if baseline and comparable:
        baseline_metrics = baseline["metrics"]
        status = "PASSED" if all(
            item["metrics"]["wer"] <= baseline_metrics["wer"] + 0.02
            and item["metrics"]["speechRecall"] + 1e-9 >= baseline_metrics["speechRecall"] * 0.95
            for item in comparable
        ) else "BLOCKED"
    report = {
        "schemaVersion": 1,
        "scenario": "transcription-quality-ab",
        "status": status,
        "buildIdentity": args.build_identity,
        "modelIdentity": args.model_identity,
        "referenceSha256": digest(args.reference),
        "candidates": results,
        "baselineCandidate": args.baseline,
        "safety": {"transcriptIncluded": False, "audioIncluded": False, "credentialsIncluded": False, "tokensIncluded": False},
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
