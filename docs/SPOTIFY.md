# Spotify media integration

Cyclop reads Spotify through Windows' [`GlobalSystemMediaTransportControlsSessionManager`](https://learn.microsoft.com/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager) and each session's [`SourceAppUserModelId`](https://learn.microsoft.com/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession.sourceappusermodelid). It never uses Spotify Web API/OAuth, browser playback, media-key simulation, process polling, or Spotify process memory. It only accepts the Spotify desktop AUMIDs `Spotify.exe` / `Spotify` and the Store package AUMID `SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify` (including casing variations of that exact ID). Browser sessions and other applications are ignored.

The service listens for session-list/current-session changes, metadata, playback-state, and timeline changes. It detaches every event handler when replacing the session or disposing the service. Every asynchronous metadata/artwork read is checked against the active session generation so a stale result cannot replace the new track. Artwork payload reads are capped at 8 MiB. Transport and seek actions check the session's current capability flags and return the actual GSMTC `Try*` result; seek is constrained to the session's reported seek range. These operations follow Microsoft's [session API](https://learn.microsoft.com/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession), [playback control capabilities](https://learn.microsoft.com/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionplaybackcontrols), and [timeline properties](https://learn.microsoft.com/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessiontimelineproperties).

## Read-only Windows probe

On a Windows machine with the pinned .NET SDK available, run:

```powershell
pwsh -File .\tools\probe-spotify.ps1
```

The script compiles a temporary console probe against the Windows project, reads the current supported session, prints available metadata and capability flags, then exits. It never starts Spotify or sends playback commands. If no Spotify desktop session exists, it reports that condition. For an end-to-end manual check, start Spotify and play audio yourself, run the probe, then pause or close Spotify yourself and run it again; the probe does not alter the playback state.

The read-only probe was run successfully on the development Windows machine and found a live `Spotify.exe` session while playback was paused. It returned a title, artist, timeline, seek support, and 146,674 artwork bytes. The listening metadata is intentionally not recorded here. No playback command was sent.

GSMTC session registration depends on Spotify's own Windows media integration and may lag or be absent for some player versions or playback modes. In that case the service reports an empty snapshot; it does not fall back to a browser or an unrelated current session. Actual playback control was not exercised by the read-only probe.

## Per-application volume

GSMTC has no per-session volume setter. `SpotifyVolumeService` uses Windows Core Audio [ISimpleAudioVolume](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-isimpleaudiovolume), matches sessions to `Spotify.exe` process IDs, and enumerates all active render endpoints so routed output is included. It sets each currently found Spotify session, reads the active session first, and ignores browsers and other processes. It never calls an endpoint/master-system-volume setter. Inactive paused sessions remain usable; expired sessions are ignored. A missing session disables the slider instead of showing an invented percentage.

The slider coalesces changes at 60ms intervals, keeps the current thumb position during a drag, and applies native readback after the latest write. A positive adjustment sets the requested level before clearing mute, avoiding a burst at a previous louder level. Simple-volume/session/device notifications refresh the snapshot; there is no idle polling timer. Refresh/write/dispose share one gate, callback unregistration occurs outside the state lock, and a [lifetime MTA lease](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-coincrementmtausage) keeps notification support alive between ThreadPool operations.

`tools/probe-volume.ps1` defaults to ABI-contract validation without native activation. `tools/probe-volume.ps1 -ReadOnly` explicitly reads the live session. The ABI guard checks all 11 COM method orders before activation, including the setter/getter order from the [Windows SDK header](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/Audioclient.h#L1529-L1549). It is also part of `test.ps1`. A deliberate write is only available through the standalone smoke program's explicit `--set-volume` argument; it is not part of normal tests or packaging.


## Compact player, mute and client window

The Spotify pane is 520 × 180 DIP with a 96px cover, a single transport/volume row and full-width progress below. Elapsed and remaining time are separate. Shelf, history and translation share these dimensions. Settings expands downward to 520 × 420 DIP and returns smoothly to the main-pane height. The player stays anchored while the native height changes, and an active music outline can continue alongside the height animation. Sections switch by activation rather than hover.

Clicking the speaker calls `SetMutedAsync` on the same verified `ISimpleAudioVolume.SetMute` interface. It does not change volume scalars, so unmute restores each session's retained level. The pure `SpotifyMuteState` remembers observed nonzero levels for the separate slider-zero case; it never invents a level for an initially silent unknown session. The button is disabled during pending slider writes/drag and while a mute request runs, avoiding a delayed slider write undoing a later mute.

Clicking the cover calls `SpotifyLauncher.TryOpenAsync`: find a visible/minimized, ownerless, titled, non-tool Spotify.exe window; restore/show it and request foreground activation, allowing up to 150ms for the asynchronous restore. With no visible client (including closed-to-tray state), Windows Shell receives the locally registered `spotify:` protocol instead of showing arbitrary hidden helper windows. The local registry registration was inspected; no executable path or browser fallback is hardcoded. The launcher sends no playback commands. Windows may refuse foreground activation; URI acceptance does not itself prove that a Store window became foreground. Native launch/mute gestures remain manual acceptance checks; automated checks do not launch Spotify or change live audio.
