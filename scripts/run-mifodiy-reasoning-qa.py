"""Run the synthetic Intelligence v2 preflight without sending text to a server."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from workers.summary_worker.intelligence_qa import (  # noqa: E402
    PRODUCTION_CASE_COUNTS,
    evaluate_intent_cases,
    generate_production_reasoning_cases,
    generate_reasoning_cases,
    validate_reasoning_corpus,
)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "acceptance" / "mifodiy-reasoning-preflight.json")
    parser.add_argument("--production", action="store_true", help="use the 700-case release matrix")
    args = parser.parse_args()
    cases = generate_production_reasoning_cases() if args.production else generate_reasoning_cases()
    corpus = validate_reasoning_corpus(cases, PRODUCTION_CASE_COUNTS if args.production else None)
    evaluation = evaluate_intent_cases(cases)
    report = {
        "schema": "mifodiy-intelligence-acceptance-v1" if args.production else "mifodiy-reasoning-preflight-v1",
        "mode": "OFFLINE_PREFLIGHT" if args.production else "UNIT_PREFLIGHT",
        "status": "PASSED" if corpus["valid"] and evaluation["failed"] == 0 and (not args.production or evaluation["caseCount"] >= 700) else "FAILED",
        "corpus": corpus,
        "evaluation": {
            "caseCount": evaluation["caseCount"],
            "passed": evaluation["passed"],
            "failed": evaluation["failed"],
            "accuracy": evaluation["accuracy"],
            # Only hashes and metrics leave the local process.
            "results": evaluation["results"],
        },
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    # Keep console output privacy-safe and bounded; the full report is written
    # to the local artifact and contains hashes/metrics only.
    print(json.dumps({
        "schema": report["schema"],
        "mode": report["mode"],
        "status": report["status"],
        "caseCount": report["evaluation"]["caseCount"],
        "passed": report["evaluation"]["passed"],
        "failed": report["evaluation"]["failed"],
        "corpusValid": report["corpus"]["valid"],
    }, ensure_ascii=False, separators=(",", ":")))
    return 0 if corpus["valid"] and evaluation["failed"] == 0 and (not args.production or evaluation["caseCount"] >= 700) else 1


if __name__ == "__main__":
    raise SystemExit(main())
