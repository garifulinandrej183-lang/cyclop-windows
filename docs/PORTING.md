# Porting Cyclop to Windows

Base: [akalikbergenov/cyclop](https://github.com/akalikbergenov/cyclop/tree/125eb02fbd28b443fe922334ae09823d4ad38df6), commit `125eb02fbd28b443fe922334ae09823d4ad38df6`. Sources were cloned into the ignored `upstream/cyclop` directory for inspection. Re-fetch: `git clone https://github.com/akalikbergenov/cyclop.git upstream/cyclop`, then `git -C upstream/cyclop checkout 125eb02fbd28b443fe922334ae09823d4ad38df6`.

| Original inspected component | Windows implementation / decision |
|---|---|
| README, LICENSE, architecture.md | MIT attribution in LICENSE, notices and settings/About; unofficial port explicitly identified |
| Notch/NotchGeometry.swift | 520 × 180 DIP main panes and 520 × 420 DIP Settings, 12 DIP concave shoulders, 22 DIP bottom radius; collapsed 192 × 12 DIP strip including shoulders; selected display uses work area and actual DPI |
| UI/NotchShape.swift, Theme.swift | StreamGeometry reproduces quadratic shoulder/lower-corner construction, black fill, native Segoe UI, quiet icon rail |
| Notch/PanelState.swift | Core/Panel/PanelState.cs owns collapsed, expanded, text input, receiving files and hidden; pointer leave cannot interrupt editing/drop/menu |
| Notch/NotchPanel.swift | Borderless WPF tool window. No taskbar or Alt+Tab entry; ShowActivated=false. A passive NOACTIVATE style is removed on WM_MOUSEACTIVATE / explicit open before normal focus/input; it returns only on collapse. The interactive UI is never permanently marked non-activating |
| Notch/NotchRootView.swift | WPF AllowDrop / FileDrop and alpha-transparent layered window; actual window bounds shrink to collapsed strip rather than retaining a large invisible hit target |
| Notch/PointerWatcher.swift | Mouse enter/leave + cancellable dwell tasks. macOS pointer polling workaround intentionally omitted; no global cursor timer |
| Model/NotchViewModel.swift, UI/NotchContentView.swift | CommunityToolkit.Mvvm + MainViewModel. Four permanent module tabs. Hover opens without stealing focus; tabs select on click or Ctrl+1…4, with a short content fade for clicks |
| UI/ShelfPane.swift, ShelfDragSource.swift | Explorer FileDrop, extended selection, threshold drag, Copy-only outbound operation; double click opens, context menu reveals/copies path/removes card |
| Services/ShelfStore.swift, ScreenshotVault.swift | Local reference index plus separate managed PNG vault; card removal leaves originals and explicitly saved images intact |
| Services/ScreenshotFolderWatcher.swift | Opt-in user-selected folder, watcher events debounced, stable-size retry, bounded retry and overflow reconciliation; no Lightshot API or assumed folder |
| Services/ClipboardStore.swift | AddClipboardFormatListener rather than NSPasteboard polling; single shared observer; DPAPI CurrentUser ciphertext in SQLite, in-memory search |
| Services/MediaController.swift, UI/MediaPane.swift | Public WinRT GSMTC restricted to Spotify desktop/Store; capability gated Try-commands. No private frameworks, AppleScript, simulated media keys or Web API |
| Services/Translator.swift, UI/TranslatePane.swift | Alphabet heuristic retained; explicit direction and swap. CTranslate2 + exact SentencePiece tokenizer in lazy, frozen CPU worker, not Apple Translation.framework |
| Tests/CyclopTests/SnippetStoreTests.swift | Studied persistence/dedup/order coverage. Windows tests cover panel, media selection/timeline, encrypted history, privacy rules, shelf references and worker protocol instead of adding excluded snippets |

Differences required by the brief: one panel on a chosen monitor; work-area top instead of physically covering the taskbar; fullscreen suppression; no keyboard focus on hover; history capture disabled by default; managed-image storage limit; four requested modules only. No calendar, snippets, currencies, notes, teleprompter, account or sync was brought over.

The port uses OS foreground/location events and WM display/DPI/power changes. It does not bypass exclusive fullscreen, protected desktops, UAC, or applications running at higher integrity. The progress timer runs only while a playing Spotify pane is visible. Opening from keyboard is immediate; hover reveal uses a short content fade/scale and respects Windows reduced-animation preferences.

The user additionally requested a music capsule based on their image: collapsed playing Spotify uses a 144 × 38 DIP rounded pill with circular artwork and five animated bars, halved from the initial design at the user's request. WPF transform clocks run only while this state is visible and animations are enabled. GSMTC supplies playback state and artwork, not audio amplitude; the bars are a playback indicator. Pause or session loss returns the original strip.

The subsequent video reference supplies the reveal character: a centered shape expands downward/outward and its content appears later. The WPF music capsule uses a 360ms geometry morph with the movement cubic curve (0.77, 0, 0.175, 1), clipped content scale, smoothstep crossfades and reversible current-progress transitions. The user requested this softer timing after the initial 280ms drawer reveal. It adapts that motion to the existing Windows notch rather than copying the phone's widget layout. Native bounds change once at opening and once after closing; PanelOutline redraws without invalidating measure. Keyboard actions and reduced motion remain immediate.

Application artwork is the original Resources/AppIcon.icns from the pinned upstream, converted without redesign into Assets/Cyclop.ico (16/20/24/32/40/48/64/128/256px). It is embedded as the EXE application icon and WPF resource, copied for owned native tray loading, and used by Inno Setup. The icon converter is tools/convert-app-icon.py (optional Python/Pillow maintenance tool; the committed ICO is used directly by builds). Source ICNS SHA256: CBEAE3221C4E8D575EA981D9CAD04C8DE83E2CA6AB6350EB7A6F2C035454A4F1; ICO SHA256: 5FA7C0B0C777ADF9065D09A2468601C91E43D72CDB24441DCA48766DD439C5AB. Context menus use explicit dark templates to remove the light Windows icon gutter left behind by a background-only theme.
