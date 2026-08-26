"""Run the privacy-safe synthetic Mifodiy A→B→C lifecycle preflight."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from workers.summary_worker.intelligence_qa import (  # noqa: E402
    evaluate_synthetic_lifecycle,
    generate_synthetic_lifecycle_cases,
)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "acceptance" / "mifodiy-intelligence-e2e.json")
    args = parser.parse_args()
    cases = generate_synthetic_lifecycle_cases()
    evaluation = evaluate_synthetic_lifecycle(cases)
    report = {
        "schema": "mifodiy-intelligence-e2e-v1",
        "mode": "OFFLINE_PREFLIGHT",
        "status": "PASSED" if evaluation["failed"] == 0 else "FAILED",
        "caseCount": evaluation["caseCount"],
        "passed": evaluation["passed"],
        "failed": evaluation["failed"],
        "accuracy": evaluation["accuracy"],
        "privacy": {"questionsStored": False, "answersStored": False, "evidenceTextStored": False},
        "results": evaluation["results"],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: report[key] for key in ("schema", "mode", "status", "caseCount", "passed", "failed")}, ensure_ascii=False, separators=(",", ":")))
    return 0 if report["status"] == "PASSED" else 1


if __name__ == "__main__":
    raise SystemExit(main())
