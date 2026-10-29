# Runtime dependency notices

License texts and package `.nuspec` metadata here accompany the shipped .NET/WPF application. Package metadata is copied from the exact restored versions in packages.lock.json (the Windows SDK projection package is selected by the pinned SDK).

- .NET runtime 10.0.14: MIT and bundled third-party notices from the official runtime package.
- WPF/Windows Desktop 10.0.14: license from its runtime package.
- CommunityToolkit.Mvvm 8.4.2: MIT License.md and ThirdPartyNotices.txt from the package.
- Microsoft.Data.Sqlite/Core 10.0.14: MIT; full text is Microsoft's `dotnet/efcore` v10.0.14 LICENSE.txt.
- SQLitePCLRaw 2.1.13: Apache-2.0 as declared by the package metadata. Copyright 2014-2024 SourceGear, LLC. Full terms: Apache-2.0.txt. The underlying SQLite engine is public domain (https://sqlite.org/copyright.html).
- ProtectedData 10.0.14: .NET MIT and its supplied notices.
- Microsoft Windows SDK .NET projections / WinRT.Runtime: MIT as declared in the SDK package; Microsoft-WinRT-MIT.txt contains the upstream Microsoft/CsWinRT license.

Python worker licenses are in worker/licenses/ in a published distribution and are collected separately by tools/build-worker.ps1. Translation models are not redistributed in the app installer; their unresolved license status is documented in docs/TRANSLATION.md.
