# Contributing

Thanks for helping improve this independent Windows adaptation of [Cyclop](https://github.com/akalikbergenov/cyclop). Its current scope is the Spotify panel, file shelf, opt-in clipboard history and offline Russian/English translation. Please describe the problem and expected behaviour in an issue or pull request; avoid promising unsupported features or compatibility.

Use Windows 11 x64 and the pinned tools described in [building](docs/BUILDING.md). Run the checks relevant to your change and report the commands, results and any checks you could not perform. The [manual checklist](docs/MANUAL-TESTS.md) covers native integration scenarios that a build or screenshot alone cannot prove.

Use your own synthetic text, images and files for testing. Do not alter the live clipboard or audio playback/volume by default; perform such checks only with explicit permission in a suitable test environment. Do not include personal data, secrets, model archives or generated build output in a contribution.

For UI changes, identify the screens and states actually inspected. For platform integrations, distinguish automated checks from manual checks and state the Windows and application versions used. Keep changes focused and follow the existing project conventions.
