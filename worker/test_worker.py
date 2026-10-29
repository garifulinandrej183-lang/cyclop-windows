"""Smoke the Python module or frozen worker against official Argos model archives."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / "artifacts" / "model-cache"
EXPECTED = {
    "en-ru": ("en-ru.argosmodel", "591d743ae103752b88ffc38785c50421320f4eff93c8967e0d3d2e14d4e27811"),
    "ru-en": ("ru-en.argosmodel", "e9ba8bf722d10a4a4c39f74289d5938fd47eac08dbe4ed0afd22d89445a5c3ac"),
}


def install_archives(models_root: Path) -> None:
    for direction, (filename, expected_hash) in EXPECTED.items():
        archive = CACHE / filename
        hasher = hashlib.sha256()
        with archive.open("rb") as source:
            for block in iter(lambda: source.read(1024 * 1024), b""):
                hasher.update(block)
        digest = hasher.hexdigest()
        assert digest == expected_hash, f"Unexpected SHA-256 for {filename}: {digest}"
        target = models_root / direction
        target.mkdir(parents=True)
        with zipfile.ZipFile(archive) as package:
            root = package.namelist()[0].split("/", 1)[0] + "/"
            for entry in package.infolist():
                relative = entry.filename.removeprefix(root)
                if not relative or entry.is_dir():
                    continue
                # Keep exactly the CTranslate2 + SentencePiece runtime inputs.
                if relative == "sentencepiece.model" or relative == "metadata.json" or relative.startswith("model/"):
                    output = target / relative
                    output.parent.mkdir(parents=True, exist_ok=True)
                    with package.open(entry) as source, output.open("wb") as destination:
                        shutil.copyfileobj(source, destination, 1024 * 1024)


def requests(exe: Path, models: Path, values: list[tuple[str, str, str]]) -> list[str]:
    proc = subprocess.run(
        [str(exe), str(models)],
        input="".join(json.dumps({"id": request_id, "text": text, "direction": direction}, ensure_ascii=False) + "\n"
                     for request_id, text, direction in values),
        text=True, encoding="utf-8", capture_output=True, timeout=240,
    )
    if proc.returncode != 0:
        raise AssertionError(f"Frozen worker exited {proc.returncode}: {proc.stderr[-2000:]}")
    responses = [json.loads(line) for line in proc.stdout.splitlines()]
    assert [response.get("id") for response in responses] == [item[0] for item in values]
    assert all("error" not in response for response in responses), responses
    return [response["result"] for response in responses]


def check_frozen_network_guard(exe: Path) -> None:
    for operation in ("create", "dns", "connect"):
        probe = subprocess.run([str(exe), "--probe-network-guard", operation],
                               capture_output=True, text=True, encoding="utf-8", timeout=15)
        assert probe.returncode == 0, (operation, probe.returncode, probe.stderr[-1000:])
        assert probe.stdout.strip() == "NETWORK_BLOCKED", (operation, probe.stdout)


def main() -> None:
    executable = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else None
    if executable is not None:
        check_frozen_network_guard(executable)
    long_text = " ".join([
        "The text continues with useful context and a few details that help explain the main idea clearly"
    ] * 8)
    with tempfile.TemporaryDirectory(prefix="cyclop-model-check-") as temp:
        models = Path(temp) / "models"
        models.mkdir()
        install_archives(models)
        import sentencepiece as spm
        source_tokens = len(spm.SentencePieceProcessor(
            model_file=str(models / "en-ru" / "sentencepiece.model")
        ).encode(long_text, out_type=str))
        assert source_tokens > 120, f"Long-text fixture must exceed the 120-token chunk cap: {source_tokens}"
        if executable is None:
            module_spec = importlib.util.spec_from_file_location("cyclop_worker", ROOT / "worker" / "worker.py")
            assert module_spec is not None and module_spec.loader is not None
            module = importlib.util.module_from_spec(module_spec)
            module_spec.loader.exec_module(module)
            translator = module.OfflineTranslator(models)
            en_ru = translator.translate("Hello, how are you?\n\nHave a nice day.", "en-ru")
            ru_en = translator.translate("Привет, как дела?\n\nХорошего дня.", "ru-en")
            long_result = translator.translate(long_text, "en-ru")
        else:
            en_ru, ru_en, long_result = requests(executable, models, [
                ("e2r", "Hello, how are you?\n\nHave a nice day.", "en-ru"),
                ("r2e", "Привет, как дела?\n\nХорошего дня.", "ru-en"),
                ("long", long_text, "en-ru"),
            ])

        assert en_ru.strip() and en_ru != "Hello, how are you?\n\nHave a nice day."
        assert ru_en.strip() and ru_en != "Привет, как дела?\n\nХорошего дня."
        assert "\n\n" in en_ru and "\n\n" in ru_en
        assert long_result.strip()
        offline = "socket creation, DNS and connect denied; " if executable is not None else ""
        print(f"PASS: {offline}en-ru, ru-en, paragraph boundaries, {source_tokens}-token input")


if __name__ == "__main__":
    main()
