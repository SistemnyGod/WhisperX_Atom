"""Run the synthetic Intelligence v2 preflight without sending text to a server."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from workers.summary_worker.intelligence_qa import (  # noqa: E402
    evaluate_intent_cases,
    generate_reasoning_cases,
    validate_reasoning_corpus,
)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "acceptance" / "mifodiy-reasoning-preflight.json")
    args = parser.parse_args()
    cases = generate_reasoning_cases()
    corpus = validate_reasoning_corpus(cases)
    evaluation = evaluate_intent_cases(cases)
    report = {
        "schema": "mifodiy-reasoning-preflight-v1",
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
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if corpus["valid"] and evaluation["failed"] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())

