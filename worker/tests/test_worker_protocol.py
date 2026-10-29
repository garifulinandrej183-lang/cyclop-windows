import json
import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
WORKER = ROOT / "worker" / "worker.py"


class WorkerProtocolTests(unittest.TestCase):
    def test_output_limit_is_reported_instead_of_returning_truncated_text(self):
        spec = importlib.util.spec_from_file_location("cyclop_worker_unit", WORKER)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)

        class FakeTokenizer:
            def encode(self, _text, out_type=str):
                return ["hello"]

            def decode_pieces(self, pieces):
                return "".join(pieces)

        class FakeTranslator:
            def translate_batch(self, _chunks, **kwargs):
                self.max_decoding_length = kwargs["max_decoding_length"]
                return [SimpleNamespace(hypotheses=[["token"] * 1024])]

        translator = FakeTranslator()
        worker = module.OfflineTranslator(Path("unused"))
        worker.loaded = ("en-ru", translator, FakeTokenizer())
        with self.assertRaises(module.TranslationOutputLimitError):
            worker.translate("hello", "en-ru")
        self.assertEqual(translator.max_decoding_length, 1024)

    def test_jsonl_ids_errors_and_no_text_in_diagnostics(self):
        secret = "NEVER_LOG_THIS_SENTINEL"
        with tempfile.TemporaryDirectory(prefix="cyclop-worker-protocol-") as models:
            lines = [
                {"id": "first", "text": secret, "direction": "en-ru"},
                {"id": "second", "text": "Привет", "direction": "ru-en"},
                {"id": "third", "text": "hello", "direction": "invalid"},
            ]
            proc = subprocess.run(
                [sys.executable, str(WORKER), models],
                input="".join(json.dumps(item, ensure_ascii=False) + "\n" for item in lines),
                text=True, encoding="utf-8", capture_output=True, timeout=15,
            )
        self.assertEqual(proc.returncode, 0, proc.stderr)
        responses = [json.loads(line) for line in proc.stdout.splitlines()]
        self.assertEqual([response["id"] for response in responses], ["first", "second", "third"])
        self.assertTrue(all(response.get("error") for response in responses))
        self.assertNotIn(secret, proc.stderr)
        self.assertEqual(len(proc.stdout.splitlines()), 3)

    def test_socket_creation_dns_and_connect_are_denied_before_os_calls(self):
        script = (
            "import sys, importlib.util; "
            f"spec=importlib.util.spec_from_file_location('cw', {str(WORKER)!r}); "
            "m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m); "
            "sys.argv=['worker','--probe-network-guard',sys.argv[1]]; raise SystemExit(m.main())"
        )
        for operation in ("create", "dns", "connect"):
            with self.subTest(operation=operation):
                proc = subprocess.run([sys.executable, "-c", script, operation],
                                      text=True, encoding="utf-8", capture_output=True, timeout=10)
                self.assertEqual(proc.returncode, 0, proc.stderr)
                self.assertEqual(proc.stdout.strip(), "NETWORK_BLOCKED")


if __name__ == "__main__":
    unittest.main()
