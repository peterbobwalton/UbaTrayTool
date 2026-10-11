# UbaTrayTool — handover

_Last updated 2026-10-11._

## State

- Version **0.1.2**. Builds clean in VS 2026 (0 errors, 0 warnings). `main` pushed to
  https://github.com/peterbobwalton/UbaTrayTool.
- Installer `UbaTrayToolSetup-0.1.2.exe` in `Z:\Software\Claude\UbaTrayTool` (and `Installer\output`).
  Pete is running an installed copy; reinstall to pick up new builds.
- Pete has used it on live builds and says it works very well. The 0.1.1 review fixes and the 0.1.2
  size change were checked with `--dump-dir` and `--render-demo` but not yet on a cancelled build or with elevated UBT.

## How to pick it up

1. Read CLAUDE.md (layout, how UBA data is read, build/test/release steps, Pete's design rules).
2. `vs_session open C:\source\Claude\UbaTrayTool\UbaTrayTool.slnx`, build `UbaTrayTool`.
3. Parser regression: run with `--dump-dir "C:\Users\peter\AppData\Local\UnrealBuildTool" <out>`.
4. Release: bump `<Version>`, Release build of `Installer`, back to Debug, commit + push via git MCP.

## Open items / ideas (not started; ask Pete before doing)

- Verify on a live build: cancelled build shows "Build stopped"; elevated UBT ends correctly;
  remote helpers (none available yet) show in Machines with network rates.
- Trace format will change with future UE versions. Watch `uba::TraceVersion` in `UbaTrace.h`
  (53 for 5.8). A newer trace shows "Can't read this trace" with the version number.
- Possible: user-adjustable size (UiScale in the menu), per-lane mini timeline, click a card or error
  to copy the log line, sound on failure, history of recent builds.
- `Properties/launchSettings.json` has a second "Render demo" profile; the first profile must stay
  argument-free so F5 runs the tray app.
- Repo files are LF; git warns they'll become CRLF on checkout (`.gitattributes` has `* text=auto`). Harmless.
