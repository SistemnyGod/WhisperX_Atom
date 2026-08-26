#!/usr/bin/env python3
"""Preview/apply historical transcript imports without leaking source text."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from urllib import request

# Direct invocation places ``scripts`` on sys.path; add the repository root so
# the shared package works without an editable install.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from whisperx_atom.historical_import import discover


def _post(url: str, payload: dict[str, object], authorization: str | None) -> dict[str, object]:
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    headers = {"Content-Type": "application/json", "Accept": "application/json"}
    if authorization:
        headers["Authorization"] = authorization
    req = request.Request(url, data=body, headers=headers, method="POST")
    with request.urlopen(req, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Historical meeting transcript importer")
    parser.add_argument("directory", type=Path)
    parser.add_argument("--mode", choices=("PREVIEW", "APPLY"), default="PREVIEW")
    parser.add_argument("--report", type=Path)
    parser.add_argument("--api-url", help="Authenticated API base URL, required for APPLY")
    parser.add_argument("--authorization", help="Authorization header value; never written to reports")
    args = parser.parse_args(argv)
    if not args.directory.is_dir():
        parser.error("directory must exist")
    if args.mode == "APPLY" and not args.api_url:
        parser.error("--api-url is required for APPLY")

    documents = discover(args.directory)
    report = {
        "schemaVersion": 1,
        "mode": args.mode,
        "sourceDirectory": args.directory.name,
        "count": len(documents),
        "items": [document.preview() for document in documents],
    }
    if args.mode == "APPLY":
        results: list[dict[str, object]] = []
        endpoint = args.api_url.rstrip("/") + "/api/admin/historical-imports"
        for document in documents:
            if document.decision != "IMPORT":
                continue
            result = _post(endpoint, document.api_payload("APPLY"), args.authorization)
            results.append({
                "sourceSha256": document.source_sha256,
                "canonicalContentSha256": document.canonical_content_sha256,
                "result": result,
            })
        report["applyResults"] = results
    output = json.dumps(report, ensure_ascii=False, indent=2)
    if args.report:
        args.report.write_text(output + "\n", encoding="utf-8")
    else:
        print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
