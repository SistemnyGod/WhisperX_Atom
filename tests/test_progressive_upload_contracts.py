from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(name: str) -> str:
    return (ROOT / name).read_text(encoding="utf-8")


def test_progressive_upload_gate_starts_server_delivery_before_stop():
    script = read("scripts/acceptance-progressive-upload.ps1")
    assert 'Invoke-HostCommand "START"' in script
    assert "localOnly = $false" in script
    assert 'Invoke-HostCommand "STOP"' in script
    assert script.index("localOnly = $false") < script.index('Invoke-HostCommand "STOP"')


def test_progressive_upload_gate_observes_server_binding_and_confirmed_chunks_mid_recording():
    script = read("scripts/acceptance-progressive-upload.ps1")
    assert 'serverSessionId' in script
    assert 'confirmedChunkCount' in script
    assert 'midpointConfirmed -le 0' in script
    assert 'PROGRESSIVE_UPLOAD_NOT_CONFIRMED_AT_MIDPOINT' in script
    assert 'bytesPendingObserved' in script
    assert 'PROGRESSIVE_UPLOAD_BYTES_PENDING_NOT_OBSERVED' in script


def test_progressive_upload_gate_requires_monotonic_tail_confirmation():
    script = read("scripts/acceptance-progressive-upload.ps1")
    assert 'finalConfirmed -lt $midpointConfirmed' in script
    assert 'PROGRESSIVE_UPLOAD_CONFIRMED_COUNT_REGRESSED' in script
    assert 'deliveryState -notin @("CONFIRMED", "COMPLETED")' in script


def test_progressive_upload_report_excludes_audio_credentials_and_tokens():
    script = read("scripts/acceptance-progressive-upload.ps1")
    assert 'audioIncluded = $false' in script
    assert 'credentialsIncluded = $false' in script
    assert 'tokensIncluded = $false' in script
