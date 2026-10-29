# Cyclop Windows

*English · [Русский](README.ru.md)*

A compact panel at the top of your screen for Spotify, files, clipboard history and offline translation. A native Windows 11 application in C# and WPF.

This is an **unofficial, independent Windows adaptation** of [Cyclop](https://github.com/akalikbergenov/cyclop) by [@akalikbergenov](https://github.com/akalikbergenov). The original project inspired the idea and design; this port is not an official release of the original project.

[![Windows build and tests](https://github.com/garifulinandrej183-lang/cyclop-windows/actions/workflows/windows.yml/badge.svg)](https://github.com/garifulinandrej183-lang/cyclop-windows/actions/workflows/windows.yml)
[![Support the original author](https://img.shields.io/badge/support%20the%20original%20author-Buy%20Me%20a%20Coffee-FFDD00?style=flat-square&labelColor=000000)](https://buymeacoffee.com/akalikbergenov)

![Cyclop Windows panel](docs/panel.png)

*Windows UI preview with sample content. The original idea and application artwork are by @akalikbergenov.*

**[Download the latest release](https://github.com/garifulinandrej183-lang/cyclop-windows/releases/latest)** — Windows 11 x64.

## What it does

| Tab | What it does |
|---|---|
| **Spotify** | Shows artwork, track and timeline from supported Spotify desktop or Store sessions; controls playback and Spotify's own volume when Windows reports the relevant capabilities. Browser playback is not a source. |
| **Shelf** | Keep file references close at hand, drag them out, and optionally save copied images or watch a chosen screenshot folder. Removing cards leaves the original files in place. |
| **Clipboard** | Opt in to text and link history, then search, pin, restore, pause or clear entries. History is protected with Windows DPAPI for the current user. |
| **Translate** | Translate Russian and English locally on the CPU after explicitly downloading or importing both pinned Argos models. The models are not included in the app download. |

![Offline translation preview](docs/translate.png)

Hover at the top centre of the selected display or press **Ctrl+Alt+Space** to open the panel. **Escape** closes it; **Ctrl+1–4** select tabs. The shortcut and monitor can be changed in Settings. Autostart is off until enabled.

## Installation

Download the portable ZIP or per-user Setup from the release page. For the ZIP, extract the entire archive and run `Cyclop.exe`; keep its accompanying files together. Setup installs for the current user. The packages are self-contained: users do not need a .NET SDK, Python installation or administrator rights.

The application and installer are **unsigned**, so Windows may show a security warning. Translation requires an explicit model download or import in Settings; after installation, translation uses local files. The other tabs work without the models. See [translation details](docs/TRANSLATION.md) for model sources, checksums and licensing information.

## Data and privacy

Clipboard recording is off by default. It stores text and links only when enabled and respects supported clipboard exclusion markers; it does not automatically identify passwords. Image autosave is a separate opt-in: saved PNGs are ordinary, unencrypted files. Shelf references and settings are not encrypted clipboard history. Clearing history does not delete saved PNGs or the original files referenced by the shelf.

User data is stored in `%LOCALAPPDATA%\CyclopWindows`. See [clipboard and shelf](docs/CLIPBOARD.md) for capture, retention and cleanup details, and [Spotify integration](docs/SPOTIFY.md) for its Windows media-session limits.

## Building

Build on Windows 11 x64 using PowerShell 7, the pinned .NET SDK **10.0.403**, Python **3.12.14 x64** and Inno Setup **7.1.0** (compatible 6.7.3 is also supported). These are build tools, not requirements for running a release.

```powershell
.\build.ps1
.\test.ps1
.\tools\build-worker.ps1
.\test.ps1 -PythonTests
.\publish.ps1 -SkipWorkerBuild
```

See [building](docs/BUILDING.md) for setup and package details. Automated checks do not replace [manual tests](docs/MANUAL-TESTS.md), including live Spotify actions, clipboard and screenshot workflows, multiple physical displays and a clean Windows installation. See also [porting notes](docs/PORTING.md).

## Thanks

Thank you to [@akalikbergenov](https://github.com/akalikbergenov) for creating the original [Cyclop](https://github.com/akalikbergenov/cyclop), its idea, design and open-source work. This adaptation references upstream commit `125eb02fbd28b443fe922334ae09823d4ad38df6`. You can [support the original author](https://buymeacoffee.com/akalikbergenov).

Licensed under [MIT](LICENSE); see [third-party notices](THIRD-PARTY-NOTICES.md).
