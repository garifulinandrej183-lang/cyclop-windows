# Building Cyclop Windows

The application targets Windows 11 x64. To build and package it, use PowerShell 7, .NET SDK **10.0.403** (`global.json` disables roll-forward), Python **3.12.14 x64**, and Inno Setup **7.1.0**; compatible Inno Setup 6.7.3 is also supported. These tools are not needed to run the self-contained release.

The repository contains NuGet lock files and a hash-pinned worker dependency lock. With an appropriate .NET SDK on PATH, prepare the pinned build-only Python and Inno Setup for the current user, then build and test from the repository root:

```powershell
.\tools\install-python.ps1
.\tools\install-inno.ps1
.\build.ps1
.\test.ps1
.\tools\build-worker.ps1
.\test.ps1 -PythonTests
.\publish.ps1 -SkipWorkerBuild
```

The Python setup script sets `CYCLOP_PYTHON` for the current PowerShell session. Alternatively, point that variable to a Python 3.12.14 executable. Both setup scripts download their pinned tools and validate their SHA-256 hashes. `build-worker.ps1` creates `worker/.venv`, installs hash-locked dependencies and freezes the translator worker. `publish.ps1` runs the .NET tests again, stages a self-contained x64 app and worker, verifies the ZIP and creates a per-user Setup and checksums in `artifacts`. Without `-SkipWorkerBuild`, it builds the worker itself. Pass `-IsccPath` to `publish.ps1` when Inno Setup is installed outside its searched locations.

To run a development build after building the app and worker:

```powershell
.\tools\run.ps1
```

Use `-Collapsed` to start with the panel collapsed. Translation models are separate downloads; see [TRANSLATION.md](TRANSLATION.md). Release executables and installer are unsigned.

The [Windows workflow](https://github.com/garifulinandrej183-lang/cyclop-windows/blob/main/.github/workflows/windows.yml) records the automated build, test and packaging sequence. Tests and a successful package build do not establish that every native integration works on every machine. Use [MANUAL-TESTS.md](MANUAL-TESTS.md) for real-device acceptance, including live Spotify, clipboard, display and clean-install scenarios. Use synthetic data and obtain permission before a test changes a live clipboard or audio session.
