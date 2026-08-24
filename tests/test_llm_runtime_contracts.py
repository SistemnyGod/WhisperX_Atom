from pathlib import Path

from workers.summary_worker.llama_subprocess import resolve_llm_manifest_path, resolve_llm_model_path


def test_model_path_is_derived_from_one_configured_root(monkeypatch):
    monkeypatch.delenv("LLM_MODEL_PATH", raising=False)
    monkeypatch.setenv("LLM_MODEL_ROOT", "/models")
    monkeypatch.setenv("LLM_MODEL_DIR", "qwen3-8b")
    monkeypatch.setenv("LLM_MODEL_FILE", "Qwen3-8B-Q5_K_M.gguf")

    path = resolve_llm_model_path()

    assert path == Path("/models/qwen3-8b/Qwen3-8B-Q5_K_M.gguf")
    assert resolve_llm_manifest_path(path) == Path("/models/qwen3-8b/Qwen3-8B-Q5_K_M.gguf.manifest.json")


def test_explicit_legacy_model_path_remains_supported(monkeypatch):
    monkeypatch.setenv("LLM_MODEL_PATH", "/models/custom/model.gguf")
    assert resolve_llm_model_path() == Path("/models/custom/model.gguf")
