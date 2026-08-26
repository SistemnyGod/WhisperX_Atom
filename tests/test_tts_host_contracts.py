import json
import io
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "apps" / "tts-host"))

from protocol import ProtocolError, parse_request  # noqa: E402
from text_normalizer import normalize_text  # noqa: E402
import tts_host  # noqa: E402


class TtsHostContractTests(unittest.TestCase):
    def test_ping_and_shutdown_are_minimal(self):
        self.assertEqual(parse_request({"schemaVersion": 1, "id": "1", "op": "ping"}).operation, "ping")
        self.assertEqual(parse_request({"schemaVersion": 1, "id": "2", "op": "shutdown"}).operation, "shutdown")

    def test_synthesis_validation(self):
        request = parse_request({"schemaVersion": 1, "id": "abc", "op": "synthesize", "text": "  Привет\nмир ", "speaker": "aidar", "sampleRate": 48000, "cpuThreads": 4})
        self.assertEqual(request.text, "Привет мир")
        for payload in (
            {"schemaVersion": 1, "id": "x", "op": "synthesize", "text": "x" * 601},
            {"schemaVersion": 1, "id": "x", "op": "synthesize", "text": "x", "speaker": "en"},
            {"schemaVersion": 1, "id": "x", "op": "synthesize", "text": "x", "sampleRate": 16000},
            {"schemaVersion": 1, "id": "x", "op": "synthesize", "text": "x", "outputPath": "C:\\secret.wav"},
        ):
            with self.assertRaises(ProtocolError):
                parse_request(payload)
        with self.assertRaises(ProtocolError):
            parse_request({"schemaVersion": 1, "id": "../escape", "op": "synthesize", "text": "тест"})

    def test_text_normalizer_does_not_log_or_route(self):
        self.assertEqual(normalize_text("ё  /  не команда"), "ё / не команда")

    def test_runtime_accepts_a_bom_on_first_jsonl_line(self):
        class FakeRuntime:
            model_load_ms = 1

            def __init__(self, *_args, **_kwargs):
                pass

            def load(self):
                pass

        with tempfile.TemporaryDirectory() as directory:
            model = Path(directory) / "model.pt"
            model.write_bytes(b"pilot")
            output = io.StringIO()
            request = '\ufeff{"schemaVersion":1,"id":"bom","op":"ping"}\n'
            with patch.object(tts_host, "SileroRuntime", FakeRuntime), patch.object(sys, "stdin", io.StringIO(request)), patch.object(sys, "stdout", output):
                self.assertEqual(tts_host.run(model, Path(directory) / "temp", None, 4), 0)
            response = json.loads(output.getvalue())
            self.assertTrue(response["ok"])
            self.assertEqual(response["state"], "READY")

    def test_synthesis_threads_are_applied_after_preflight_ping(self):
        class FakeRuntime:
            model_load_ms = 1

            def __init__(self, *_args, **_kwargs):
                self.cpu_threads = _args[2] if len(_args) > 2 else 4

            def load(self):
                pass

            def configure_cpu_threads(self, value):
                self.cpu_threads = value

            def synthesize(self, text, speaker, sample_rate, request_id):
                return {"audioPath": "", "durationMs": 1, "synthesisMs": 1, "modelLoadMs": 1, "cpuThreads": self.cpu_threads}

        with tempfile.TemporaryDirectory() as directory:
            model = Path(directory) / "model.pt"
            model.write_bytes(b"pilot")
            output = io.StringIO()
            request = (
                '{"schemaVersion":1,"id":"ping","op":"ping"}\n'
                '{"schemaVersion":1,"id":"synth","op":"synthesize","text":"тест","speaker":"aidar","sampleRate":24000,"cpuThreads":2}\n'
            )
            with patch.object(tts_host, "SileroRuntime", FakeRuntime), patch.object(sys, "stdin", io.StringIO(request)), patch.object(sys, "stdout", output):
                self.assertEqual(tts_host.run(model, Path(directory) / "temp", None, 4), 0)
            responses = [json.loads(line) for line in output.getvalue().splitlines()]
            self.assertEqual(responses[1]["cpuThreads"], 2)

    def test_packaged_identity_is_used_when_environment_is_empty(self):
        identity = "1.0.1+" + "a" * 40
        with patch.object(tts_host, "BUILD_IDENTITY", ""), patch.object(
            Path, "read_text", return_value=json.dumps({"buildIdentity": identity})
        ):
            self.assertEqual(tts_host._load_build_identity(Path("C:/packaged")), identity)

    def test_model_manifest_is_noncommercial_pilot_and_no_model_is_tracked(self):
        manifest = json.loads((ROOT / "apps" / "tts-host" / "model-manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["modelId"], "silero-v5_5_ru")
        self.assertIn("aidar", manifest["speakers"])
        self.assertIn("non-commercial", manifest["license"])
        self.assertFalse((ROOT / "apps" / "tts-host" / "v5_5_ru.pt").exists())

    def test_tts_host_uses_only_russian_local_assets(self):
        publisher = (ROOT / "scripts" / "publish-tts-host.ps1").read_text(encoding="utf-8")
        self.assertIn("silero-v5_5_ru", publisher)
        self.assertNotIn("PIPER_JARVIS", publisher)
        self.assertNotIn("piper.exe", publisher)

    def test_tts_host_checks_parent_and_applies_request_threads_after_ping(self):
        source = (ROOT / "apps" / "tts-host" / "tts_host.py").read_text(encoding="utf-8")
        runtime = (ROOT / "apps" / "tts-host" / "silero_runtime.py").read_text(encoding="utf-8")
        self.assertIn("_parent_is_alive", source)
        self.assertIn("configure_cpu_threads", source)
        self.assertIn("torch.set_num_threads(requested)", runtime)


if __name__ == "__main__":
    unittest.main()
