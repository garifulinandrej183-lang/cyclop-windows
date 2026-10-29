# Third-party components

Cyclop Windows is an **unofficial Windows port** of [Cyclop](https://github.com/akalikbergenov/cyclop), upstream commit `125eb02fbd28b443fe922334ae09823d4ad38df6`. Copyright (c) 2026 akalikbergenov. MIT license, reproduced in LICENSE. Panel geometry, state model, compact layout and interactions are adapted from that source. Swift/AppKit and macOS system integrations are not included in the Windows executable.

| Component | Version | License / upstream |
|---|---|---|
| .NET SDK / runtime / WPF | SDK 10.0.403; runtime selected by locked framework restore | [MIT, .NET](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT); distribution includes its own notices |
| CommunityToolkit.Mvvm | 8.4.2 | [MIT](https://github.com/CommunityToolkit/dotnet/blob/main/License.md) |
| Microsoft.Windows.CsWin32 (build only) | 0.3.367 | [MIT](https://github.com/microsoft/CsWin32/blob/main/LICENSE) |
| Microsoft.Data.Sqlite / SQLitePCLRaw | 10.0.14 / see packages.lock.json | [MIT](https://github.com/dotnet/efcore/blob/v10.0.14/LICENSE.txt), [Apache-2.0](https://www.apache.org/licenses/LICENSE-2.0.txt) |
| SQLite native engine | see lock file | [Public domain](https://www.sqlite.org/copyright.html) |
| ProtectedData | 10.0.14 | .NET MIT |
| xUnit (test only) | 2.9.3 | [Apache-2.0](https://github.com/xunit/xunit/blob/main/license.txt) |
| Microsoft.NET.Test.Sdk (test only) | 18.11.0 | [MIT](https://github.com/microsoft/vstest/blob/main/LICENSE) |

Python worker versions, bundled native dependencies and model provenance are recorded in worker/ and docs/TRANSLATION.md. Those records are shipped beside the worker. Models are separate downloads/imports and are not bundled into the application installer. Do not infer a model's license from the engine or package index license.

Full application runtime license texts and exact package notices/metadata are included in `licenses/`. CsWin32 0.3.367 is stable; its required build-only inputs are vendor-labelled `Microsoft.Windows.SDK.Win32Metadata 71.0.37-preview` and `Win32Docs 0.1.42-alpha`. These are the specific exception to avoiding prerelease packages: they are necessary metadata/documentation inputs of the requested stable generator, locked by hash, PrivateAssets, and not shipped as runtime integrations.

Control icons in the Windows UI are original geometric drawings. Windows system fonts are supplied by the operating system and are not redistributed. The application/window/tray/installer icon reuses Cyclop's original Resources/AppIcon.icns from upstream commit 125eb02fbd28b443fe922334ae09823d4ad38df6, converted to a multi-resolution ICO. Copyright (c) 2026 akalikbergenov, MIT license reproduced in LICENSE. No upstream screenshots are bundled. Icon provenance and hashes are recorded in docs/PORTING.md.

The installer is produced with [Inno Setup 7.1.0](https://jrsoftware.org/isdl.php). Inno Setup is a build tool; its licensing terms are separate from this MIT application. Generated builds are unsigned.
