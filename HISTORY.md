# UbaTrayTool — history

## 2026-10-09 — first version (0.1.0)

- Pete asked for his own UbaVisualizer as a tray popup. The UE 5.8 launcher install only had UBA headers,
  so Pete cloned the UBA source (sparse, branch `5.8`) to `C:\source\Claude\UE-UbaSource`.
- Ported the trace reader to C#: channel + named mapping for live builds, `.uba` files for saved ones.
  Checked against `Log.uba` (a v43 trace from UE 5.6): 5534/5534 bytes, 7 actions, timings right.
- Pete's requirements during the build: a new product (not TrayTools), named UbaTrayTool; prettier;
  match dark/light; one task card per processor; grow in **height only** (fixed width); debounce
  shrinking; slide off-screen 3 s after the build; icon on the taskbar, not the overflow.
- Pete created the GitHub repo; local repo initialised in `C:\source\Claude\UbaTrayTool`. First push by Pete.

## 2026-10-09 — installer

- Inno Setup installer like TrayTools': Program Files, Start menu, "Start with Windows" (on),
  "Stop UBA from opening UbaVisualizer" (off), kills a running copy before upgrade.
  Built from VS (Release, `Installer` project). Copied to `Z:\Software\Claude\UbaTrayTool`.

## 2026-10-09 — code review (0.1.1)

Pete: "works very well". Review fixes:
- Cancelled builds showed false errors (exit -1): now "Build stopped" (amber).
- Idle reused process hosts no longer counted as running work.
- Host exit detected by pid when the process can't be opened (elevated UBT).
- Parser waits for a complete header; unexpected exceptions stop parsing with a message.
- Popup re-targets size when errors/machines sections appear; shrinks at once while hidden.
- BuildStarted always before BuildEnded; UbaVisualizer path searched once per build.
- Tray context disposed on exit (icon + listener mutex cleaned up).
- `--dump-dir` added. All 12 traces in `%LOCALAPPDATA%\UnrealBuildTool` (11 × v53 from UE 5.8, 1 × v43) parse fully.

## 2026-10-11 — 25% smaller (0.1.2)

- Popup too big on many-core PCs. Added `UiScale = 0.75`; column count still computed at full size so
  the popup shrinks in both directions (64 lanes: 1210×1154 → 844×852).
- Git now done through Pete's git MCP server; pushed all pending commits.
