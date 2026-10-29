# Cyclop translation worker

This directory contains the Python JSONL worker source and the pinned Windows build dependencies. Build on Windows with Python 3.12:

```powershell
./tools/build-worker.ps1
```

This creates `artifacts/worker/cyclop-translator/` as an onedir package. The application runs `cyclop-translator.exe` and passes the installed models directory as its only normal argument. It does not require a system Python install.

The lock pins CTranslate2 4.8.4, SentencePiece 0.2.2, PyInstaller 6.22.3 and transitive dependencies with hashes. PyInstaller collects `python312.dll`, the CTranslate2 and SentencePiece native modules, Intel OpenMP, and MSVC runtime files needed by the tested package. The build recursively removes `cudnn64_9.dll` from the final onedir because this worker uses CPU only. The CTranslate2 PE imports do not reference cuDNN, and the frozen CPU worker was tested against real models after removal. No PyTorch or CUDA runtime is needed.

Only the most recently used direction remains loaded in memory. When the user changes direction, the worker drops the prior CTranslate2 translator and tokenizer before creating the next one; installed files are left in place. CPU runtime settings are applied before importing CTranslate2: `OMP_WAIT_POLICY=PASSIVE`, `KMP_BLOCKTIME=0`, `inter_threads=1`, `intra_threads=2`. OpenMP defines passive wait as mostly sleeping/yielding, and Intel defines blocktime as how long workers spin before sleeping. These settings reduce idle thread use and stack reservation for this single-request interactive worker.

Each decoding chunk has an explicit 1,024-token output ceiling. If CTranslate2 returns a hypothesis at that ceiling, the worker returns a clear limit error instead of treating a possibly truncated result as complete.

At startup the worker installs a CPython audit hook that rejects all `socket.*` API events. No network connection or DNS request can be made through the Python socket API from the worker process. The audit hook is tested in the frozen executable for socket creation, DNS resolution, and connect rejection before Winsock is called. The test does not alter firewall settings or test Windows network-adapter isolation.

For worker protocol tests, run:

```powershell
python -m unittest discover -s worker/tests -v
```

For actual model translation tests using the downloaded pinned Argos model archives, run `worker/test_worker.py` without an executable to exercise the Python module or pass the frozen executable path to exercise the packaged worker. Model sources, versions, hashes, tokenizer details, and redistribution caveats are in [TRANSLATION.md](../docs/TRANSLATION.md).
