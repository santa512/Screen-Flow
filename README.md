# ScreenFlow

Native Windows workflow automation using mouse input, keyboard shortcuts, and computer vision.

**Version 0.1** · [Windows x64 download](https://github.com/santa512/Screen-Flow/releases/download/v0.1/ScreenFlow-v0.1-win-x64.zip) · [Usage guide](docs/USAGE.md) · [Changelog](CHANGELOG.md)

## Requirements

- Windows x64; interactive, unlocked desktop.
- Portable download: .NET included; entire extracted folder required.
- Source builds: .NET 10-compatible SDK; internet access for package restore.
- Target applications: same privilege level as ScreenFlow.

## Quick start

1. Extract the ZIP → run `ScreenFlow/ScreenFlow.exe`.
2. Add actions → edit settings → **Apply changes**.
3. **Pick point on screen** or **Capture visual target**.
4. Arrange and enable actions → set repetition and start delay.
5. **Run workflow** → focus the target during the countdown.

**Emergency stop: F8** · Activity log: progress and errors.

## Actions

| Category | Available actions |
| --- | --- |
| Mouse | Click, double click, right click, move, scroll |
| Keyboard | Unicode text, shortcuts, Enter, Tab |
| Vision | Find image and click; wait for image |
| Browser | Focus window; new tab via Ctrl+T |
| Data | Copy page URL, read clipboard, set variable |
| Timing | Delays, pause after action, repeated workflows |

## Workflow reference

- Files: `.screenflow.json`; embedded target images; **Open / Save / Save as**.
- Editing: reorder, duplicate, remove, enable/disable; selection-dependent controls.
- Variables: `{{name}}`; letters/digits/underscores; leading letter or underscore.
- Values: shared across steps and repeat cycles; reset on each run.
- URL handoff: copy URL into `url` → switch tab → click field → type `{{url}}`.
- Built-in examples: new browser tab, URL handoff; local practice window.
- Execution errors and image timeouts: workflow stops; details in the activity log.

## Browser targeting

- Octo/Octium, Chrome, Edge, Firefox, Brave, Vivaldi, Opera, Chromium.
- Priority: foreground browser → last-used browser → frontmost available browser.
- Minimized windows: restored before input; foreground focus checked before Ctrl+T.
- Octo: open the intended profile first; profile manager excluded from targeting.
- Visible website interaction through native input; no DOM access or extensions.

## Visual targets

| Setting | Reference |
| --- | --- |
| Coordinates | Physical desktop pixels; multiple displays supported |
| Capture | Distinctive text, icon, or button edge; minimum 8 × 8 pixels |
| Confidence | Default 0.92; range 0.50–1.00 |
| Timeout | Default 10 seconds |
| Scale search | Optional 80%, 90%, 100%, 110%, 125%, 150% |

- Image clicks: center of the best qualifying match.
- Consistent zoom, theme, and display scaling; visible targets required.
- Repeated controls: capture surrounding context for distinction.

## Appearance

- Windows Fluent controls, Segoe fonts, mouse-circle icon.
- **System / Light / Dark** selector; immediate switching; saved preference.
- Settings: `%LocalAppData%/ScreenFlow/settings.json`.

## Build and diagnostics

```powershell
./build.ps1 -Test -Publish
```

Output: `dist/ScreenFlow-Production/ScreenFlow.exe`.

- `--self-test`: isolated automated checks.
- `--ui-self-test`: practice-window integration check; click **Start local check**.
- `--browser-self-test`: practice-window focus and shortcut check.

## Current scope

- Sequential workflows with full-workflow repetition.
- No recording, OCR, conditional branches, nested loops, or scheduling.
- No locked or secure-desktop automation.
