from whisperx_atom.diarization_policy import (
    is_cuda_oom,
    resolve_speaker_bounds,
    should_release_asr,
)


def test_legacy_speaker_defaults_use_bounded_deployment_policy():
    assert resolve_speaker_bounds(1, 12, configured_min=1, configured_max=8) == (1, 8)


def test_explicit_speaker_limits_are_preserved():
    assert resolve_speaker_bounds(2, 12, configured_min=1, configured_max=8) == (2, 12)
    assert resolve_speaker_bounds(4, 3, configured_min=1, configured_max=8) == (4, 4)


def test_low_vram_policy_is_conservative():
    assert should_release_asr(free_vram_mb=1900, threshold_mb=2048, enabled=True)
    assert not should_release_asr(free_vram_mb=4096, threshold_mb=2048, enabled=True)
    assert not should_release_asr(free_vram_mb=1900, threshold_mb=2048, enabled=False)
    assert is_cuda_oom(RuntimeError("CUDA out of memory"))
    assert not is_cuda_oom(RuntimeError("invalid audio"))

