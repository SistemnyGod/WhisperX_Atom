import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "apps" / "tts-host"))

from protocol import ProtocolError, parse_request  # noqa: E402
from text_normalizer import normalize_text  # noqa: E402


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

    def test_model_manifest_is_noncommercial_pilot_and_no_model_is_tracked(self):
        manifest = json.loads((ROOT / "apps" / "tts-host" / "model-manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["modelId"], "silero-v5_5_ru")
        self.assertIn("aidar", manifest["speakers"])
        self.assertIn("non-commercial", manifest["license"])
        self.assertFalse((ROOT / "apps" / "tts-host" / "v5_5_ru.pt").exists())


if __name__ == "__main__":
    unittest.main()
