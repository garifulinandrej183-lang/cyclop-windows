"""Offline JSONL translator using Argos CTranslate2 model packages."""

from __future__ import annotations

import json
import gc
import os
import queue
import re
import sys
import threading
import time
from pathlib import Path
from typing import Any

IDLE_SECONDS = 90
MAX_LINE_BYTES = 2 * 1024 * 1024
MAX_TOKENS_PER_CHUNK = 120
SUPPORTED = {"en-ru": ("en", "ru"), "ru-en": ("ru", "en")}
SENTENCE_END = re.compile(r"(?<=[.!?…])\s+")


class TranslationOutputLimitError(Exception):
    """The model output reached its configured bound; never return a cut-off string."""


def install_network_guard() -> None:
    """Deny all CPython socket API use for this process lifetime."""
    def deny_socket(event: str, _args: tuple[object, ...]) -> None:
        if event.startswith("socket."):
            raise PermissionError("Network access is disabled in the offline translation worker.")

    # Audit hooks cannot be removed by Python code after installation.
    sys.addaudithook(deny_socket)


class OfflineTranslator:
    def __init__(self, models_root: Path) -> None:
        self.models_root = models_root
        self.loaded: tuple[str, Any, Any] | None = None

    def _unload(self) -> None:
        previous = self.loaded
        self.loaded = None
        if previous is not None:
            # Destroy the CTranslate2 object before loading another ~200 MB model.
            # CPython refcounts release its C++ model and worker pool synchronously;
            # collection handles any wrapper cycles before the next allocation.
            del previous
            gc.collect()

    def _load(self, direction: str) -> tuple[Any, Any]:
        if self.loaded is not None and self.loaded[0] == direction:
            return self.loaded[1], self.loaded[2]

        # CTranslate2's Windows wheel bundles Intel OpenMP. Set these before
        # importing/initializing it so idle worker threads sleep instead of spin.
        os.environ["OMP_WAIT_POLICY"] = "PASSIVE"
        os.environ["KMP_BLOCKTIME"] = "0"
        self._unload()

        package = self.models_root / direction
        model_dir = package / "model"
        tokenizer_path = package / "sentencepiece.model"
        if not (model_dir / "model.bin").is_file() or not tokenizer_path.is_file():
            raise FileNotFoundError(f"Model '{direction}' is not installed. Install it in the app first.")

        # These imports are local CPU runtimes. Neither library downloads models.
        import ctranslate2
        import sentencepiece as spm

        available = set(ctranslate2.get_supported_compute_types("cpu"))
        compute_type = "int8" if "int8" in available else "int8_float32"
        translator = ctranslate2.Translator(
            str(model_dir), device="cpu", compute_type=compute_type,
            inter_threads=1, intra_threads=2,
        )
        try:
            tokenizer = spm.SentencePieceProcessor(model_file=str(tokenizer_path))
        except Exception:
            del translator
            gc.collect()
            raise
        self.loaded = (direction, translator, tokenizer)
        return translator, tokenizer

    @staticmethod
    def _sentence_chunks(paragraph: str, tokenizer: Any) -> list[list[str]]:
        """Split on sentence whitespace first, then hard-split long sentences by SP tokens."""
        chunks: list[list[str]] = []
        for sentence in SENTENCE_END.split(paragraph.strip()):
            if not sentence:
                continue
            tokens = tokenizer.encode(sentence, out_type=str)
            if not tokens:
                continue
            for start in range(0, len(tokens), MAX_TOKENS_PER_CHUNK):
                chunks.append(tokens[start:start + MAX_TOKENS_PER_CHUNK])
        return chunks

    def translate(self, text: str, direction: str) -> str:
        if direction not in SUPPORTED:
            raise ValueError("Unsupported translation direction.")
        if len(text.encode("utf-8")) > MAX_LINE_BYTES:
            raise ValueError("Text exceeds the 2 MiB request limit.")
        if not text.strip():
            return text

        translator, tokenizer = self._load(direction)
        # Keep blank lines/paragraph boundaries exactly. Non-empty paragraphs are
        # independently tokenized so boundaries never consume the model budget.
        paragraphs = text.split("\n")
        translated: list[str] = []
        for paragraph in paragraphs:
            if not paragraph.strip():
                translated.append(paragraph)
                continue
            chunks = self._sentence_chunks(paragraph, tokenizer)
            if not chunks:
                translated.append("")
                continue
            batch = translator.translate_batch(
                chunks, beam_size=4, max_batch_size=8,
                batch_type="tokens", length_penalty=0.2,
                max_decoding_length=1024,
                replace_unknowns=True,
            )
            if any(len(result.hypotheses[0]) >= 1024 for result in batch):
                raise TranslationOutputLimitError("Translation output reached the 1024-token safety limit.")
            pieces = [tokenizer.decode_pieces(result.hypotheses[0]) for result in batch]
            translated.append(" ".join(piece.strip() for piece in pieces if piece.strip()))
        return "\n".join(translated)


def _write(response: dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(response, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def _read_stdin(lines: queue.Queue[str | None]) -> None:
    try:
        for line in sys.stdin:
            if len(line.encode("utf-8", errors="replace")) > MAX_LINE_BYTES + 8192:
                lines.put("{}\n")
            else:
                lines.put(line)
    finally:
        lines.put(None)


def main() -> int:
    # Windows chooses the active ANSI code page for redirected streams unless
    # it is explicitly changed. The C# protocol always uses UTF-8.
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    probe_connect_socket = None
    if len(sys.argv) > 1 and sys.argv[1] == "--probe-network-guard" and len(sys.argv) > 2 and sys.argv[2] == "connect":
        # Create a socket before the hook solely to verify that connect itself
        # is blocked. The audit event is rejected before Winsock is invoked.
        import socket
        probe_connect_socket = socket.socket()
    install_network_guard()
    if len(sys.argv) > 1 and sys.argv[1] == "--probe-network-guard":
        try:
            operation = sys.argv[2] if len(sys.argv) > 2 else "create"
            if operation == "connect" and probe_connect_socket is not None:
                probe_connect_socket.connect(("127.0.0.1", 9))
            elif operation == "dns":
                import socket
                socket.getaddrinfo("network-probe.invalid", 80)
            else:
                import socket
                socket.socket()
        except PermissionError:
            sys.stdout.write("NETWORK_BLOCKED\n")
            sys.stdout.flush()
            return 0
        finally:
            if probe_connect_socket is not None:
                probe_connect_socket.close()
        return 3
    models_root = Path(sys.argv[1] if len(sys.argv) > 1 else os.environ.get("CYCLOP_MODELS_ROOT", "models"))
    lines: queue.Queue[str | None] = queue.Queue(maxsize=8)
    threading.Thread(target=_read_stdin, args=(lines,), daemon=True).start()
    translator = OfflineTranslator(models_root)
    last_activity = time.monotonic()
    while True:
        remaining = IDLE_SECONDS - (time.monotonic() - last_activity)
        if remaining <= 0:
            return 0
        try:
            line = lines.get(timeout=remaining)
        except queue.Empty:
            return 0
        if line is None:
            return 0
        last_activity = time.monotonic()
        request_id: Any = None
        try:
            request = json.loads(line)
            request_id = request.get("id")
            if not isinstance(request_id, (str, int)):
                raise ValueError("Request id is required.")
            if not isinstance(request.get("text"), str):
                raise ValueError("Text is required.")
            result = translator.translate(request["text"], request.get("direction", ""))
            _write({"id": request_id, "result": result})
        except Exception as exc:  # Never include request text in diagnostics or error output.
            error = (
                "The selected model is not installed."
                if isinstance(exc, FileNotFoundError)
                else "Translation output reached 1024 tokens; shorten the input and retry."
                if isinstance(exc, TranslationOutputLimitError)
                else "Unsupported direction or malformed translation request."
                if isinstance(exc, (ValueError, TypeError))
                else f"Translation failed ({type(exc).__name__})."
            )
            print(f"request failed: {type(exc).__name__}", file=sys.stderr, flush=True)
            _write({"id": request_id, "error": error})


if __name__ == "__main__":
    raise SystemExit(main())
