from __future__ import annotations

import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_python_test_lock_pins_every_package_with_sha256() -> None:
    lock = (ROOT / "requirements.test.lock.txt").read_text(encoding="utf-8")
    entries = [block for block in re.split(r"\n(?=[a-zA-Z0-9_.-]+==)", lock) if "==" in block]
    assert entries
    for entry in entries:
        first_line = entry.splitlines()[0]
        assert re.match(r"^[a-zA-Z0-9_.-]+==[^\s\\]+", first_line)
        assert re.search(r"--hash=sha256:[0-9a-f]{64}", entry)


def test_repository_nuget_cache_is_scoped_below_artifacts() -> None:
    config = (ROOT / "NuGet.Config").read_text(encoding="utf-8")
    assert 'key="globalPackagesFolder" value="artifacts/nuget-packages"' in config
    assert '<clear />' in config
    assert "http://" not in config


def test_dotnet_runner_can_seed_only_resolved_packages() -> None:
    script = (ROOT / "scripts" / "prepare-nuget-cache.ps1").read_text(encoding="utf-8")
    assert "project.assets.json" in script
    assert "PackageReference" in script
    assert "library.Value.type" in script
    assert "Copy-Item -LiteralPath $sourcePackage" in script
    assert "NUGET_SEED_PACKAGES_MISSING" in script
    assert "$AllCached" in script
