#!/usr/bin/env python3
"""Run a bounded, privacy-safe PREVIEW/APPLY meeting-pipeline repair.

The runner only calls the existing authenticated meeting endpoints.  It never
reads transcript, summary, or evidence content and writes reports containing
meeting IDs plus stage/status metadata only.  APPLY is idempotent in the API:
existing ASR/V2/Summary jobs are reused and ASR is never replayed.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


RETRYABLE_STATUSES = {"FAILED", "ERROR", "REQUIRES_RETRY"}


def _request(
    url: str,
    *,
    method: str = "GET",
    payload: dict[str, Any] | None = None,
    authorization: str | None = None,
    cookie: str | None = None,
) -> Any:
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    headers = {"Accept": "application/json"}
    if body is not None:
        headers["Content-Type"] = "application/json"
    if authorization:
        headers["Authorization"] = authorization
    if cookie:
        headers["Cookie"] = cookie
    request = Request(url, data=body, headers=headers, method=method)
    with urlopen(request, timeout=30) as response:
        raw = response.read()
    return json.loads(raw.decode("utf-8")) if raw else {}


def _value(item: dict[str, Any], name: str) -> Any:
    if name in item:
        return item[name]
    return item.get(name[:1].upper() + name[1:])


def _needs_repair(chain: dict[str, Any]) -> bool:
    # A meeting without V1 is not repairable here; replaying ASR belongs to a
    # separate explicit reprocess action and is intentionally excluded.
    if not _value(chain, "transcriptV1Id"):
        return False
    if not _value(chain, "summaryId"):
        return True
    status = str(_value(chain, "summaryStatus") or "").upper()
    return status in RETRYABLE_STATUSES


def _safe_actions(result: Any) -> list[dict[str, Any]]:
    if not isinstance(result, dict):
        return []
    actions = result.get("actions") or result.get("Actions") or []
    safe: list[dict[str, Any]] = []
    for action in actions:
        if not isinstance(action, dict):
            continue
        # IDs are useful for idempotency diagnostics; no stage text/content is
        # returned by the API and no transcript/evidence payload is copied.
        safe.append(
            {
                "stage": _value(action, "stage"),
                "state": _value(action, "state"),
                "reason": _value(action, "reason"),
                "jobId": _value(action, "jobId"),
                "transcriptId": _value(action, "transcriptId"),
            }
        )
    return safe


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Bounded meeting pipeline repair")
    parser.add_argument("--api-url", required=True, help="Authenticated API base URL")
    parser.add_argument("--mode", choices=("PREVIEW", "APPLY"), default="PREVIEW")
    parser.add_argument("--authorization", help="Authorization header; never written to the report")
    parser.add_argument("--cookie", help="Cookie header; never written to the report")
    parser.add_argument("--limit", type=int, default=200)
    parser.add_argument("--max-cases", type=int, default=500)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args(argv)
    if not args.authorization and not args.cookie:
        parser.error("one of --authorization or --cookie is required")
    if not 1 <= args.limit <= 500 or not 1 <= args.max_cases <= 500:
        parser.error("--limit and --max-cases must be between 1 and 500")

    base = args.api_url.rstrip("/")
    common = {"authorization": args.authorization, "cookie": args.cookie}
    meetings: list[dict[str, Any]] = []
    offset = 0
    errors: list[dict[str, str]] = []
    while len(meetings) < args.max_cases:
        try:
            page = _request(
                f"{base}/api/meetings?{urlencode({'limit': args.limit, 'offset': offset})}",
                **common,
            )
        except (HTTPError, URLError, TimeoutError, OSError) as exc:
            errors.append({"stage": "LIST", "error": type(exc).__name__})
            break
        if not isinstance(page, list) or not page:
            break
        meetings.extend(item for item in page if isinstance(item, dict))
        if len(page) < args.limit:
            break
        offset += args.limit

    candidates: list[str] = []
    inspected = 0
    for meeting in meetings[: args.max_cases]:
        meeting_id = _value(meeting, "id")
        if not meeting_id:
            continue
        inspected += 1
        try:
            chains = _request(f"{base}/api/meetings/{meeting_id}/pipeline", **common)
            if not isinstance(chains, list):
                continue
            if any(isinstance(chain, dict) and _needs_repair(chain) for chain in chains):
                candidates.append(str(meeting_id))
        except (HTTPError, URLError, TimeoutError, OSError) as exc:
            errors.append({"meetingId": str(meeting_id), "stage": "PIPELINE", "error": type(exc).__name__})

    results: list[dict[str, Any]] = []
    for meeting_id in candidates:
        try:
            response = _request(
                f"{base}/api/meetings/{meeting_id}/pipeline/repair",
                method="POST",
                payload={"mode": args.mode},
                **common,
            )
            actions = _safe_actions(response)
            results.append({"meetingId": meeting_id, "actions": actions})
            if any(str(item.get("state") or "").upper() in {"FAILED", "BLOCKED", "ERROR"} for item in actions):
                errors.append({"meetingId": meeting_id, "stage": "REPAIR", "error": "ACTION_NOT_COMPLETED"})
        except (HTTPError, URLError, TimeoutError, OSError) as exc:
            errors.append({"meetingId": meeting_id, "stage": "REPAIR", "error": type(exc).__name__})

    report = {
        "schemaVersion": 1,
        "mode": args.mode,
        "inspectedMeetingCount": inspected,
        "candidateCount": len(candidates),
        "completedCount": len(results),
        "errorCount": len(errors),
        "results": results,
        "errors": errors,
        "privacy": {"transcriptText": False, "summaryText": False, "evidenceText": False, "audio": False},
    }
    serialized = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if args.report:
        args.report.write_text(serialized, encoding="utf-8")
    else:
        print(serialized, end="")
    return 0 if not errors else 2


if __name__ == "__main__":
    raise SystemExit(main())
