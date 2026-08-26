from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_controlled_fallback_passes_distinct_vad_and_chunk_options():
    pipeline = read("app/transcription_pipeline.py")
    processing = read("whisperx_atom/processing.py")
    assert "def run_asr_pass(self, ctx: PipelineContext, vad_onset: float, chunk_size: int, beam_size: int)" in pipeline
    assert "model.transcribe(source, batch_size=self.config.batch_size, chunk_size=chunk_size)" in pipeline
    # The current processing boundary owns the ASR engine.  The legacy
    # pipeline call is intentionally gone; both primary and fallback passes
    # use the same large-v3 wrapper with explicit decoding options.
    assert "self._asr_engine.transcribe" in processing
    assert "vad_onset=config.vad_onset" in processing
    assert "vad_onset=thresholds.fallback_vad_onset" in processing
    assert "chunk_size=thresholds.fallback_chunk_size" in processing
    for field in ("primary_vad_onset", "primary_chunk_size", "fallback_vad_onset", "fallback_chunk_size", "fallback_effective"):
        assert field in processing


def test_whisperx_wrapper_reloads_vad_without_loading_second_large_model():
    pipeline = read("app/transcription_pipeline.py")
    cache = pipeline.split("class ModelCacheManager:", 1)[1].split("class TranscriptionPipeline:", 1)[0]
    assert "base_model = existing.model" in cache
    assert "del self._asr[key]" in cache
    assert "gc.collect()" in cache and "torch.cuda.empty_cache()" in cache
    assert "model=base_model" in cache
    assert "vad_onset" in cache and "chunk_size" in cache


def test_word_timestamps_missing_does_not_trigger_second_asr_pass():
    quality = read("whisperx_atom/transcript_quality.py")
    retryable = quality.split("retryable_reasons =", 1)[1].split("\n", 1)[0]
    assert "WORD_TIMESTAMPS_MISSING" not in retryable
