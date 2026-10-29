# Clipboard smoke probe

`tools/probe-clipboard.ps1` defaults to **safe mode**. It checks DPAPI persistence and the watched-folder/image shelf using only isolated temporary files; it does not read or modify the system clipboard. Use `-ShelfOnly` or `-DataOnly` to run one safe section.

The live Windows clipboard probe is opt-in: `tools/probe-clipboard.ps1 -LiveClipboard`. It saves a deep WPF IDataObject snapshot in process memory, does not print or write clipboard contents to disk, and compares the clipboard sequence before restoring so it can leave newer clipboard content untouched. It can still alter the clipboard if restoration fails; use the live mode only when that risk is acceptable.

**Live probe status:** restoration has not been verified end to end. Use only disposable synthetic clipboard content; safe mode is the default and does not access the system clipboard.
