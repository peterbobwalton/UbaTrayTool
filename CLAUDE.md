# UbaTrayTool — working context

Windows tray app (C# .NET 10 WinForms) that shows Unreal Build Accelerator (UBA) builds live:
a compact, prettier alternative to Epic's UbaVisualizer. A standalone product. It is **not** part of
the TrayTools suite and doesn't join TrayTools' popup row. Owner: Pete.
README.md is for users; HISTORY.md says how things got this way; HANDOVER.md has the current state
and next steps. Read HANDOVER.md first when picking the work up.

## Layout

Repo: `C:\source\Claude\UbaTrayTool` → https://github.com/peterbobwalton/UbaTrayTool (branch `main`).

```
UbaTrayTool.slnx
UbaTrayTool/                 the app
  Program.cs                 entry, single-instance mutex, test/asset switches (--dump, --render…)
  TrayContext.cs             NotifyIcon, menu, hover/pin/auto-show/auto-hide, notifications, commands
  BuildPopup.cs              the popup: layout, stable task lanes, debounce, animation, drawing
  Theme.cs                   light/dark + accent colour (registry), lane colours, DWM rounded corners
  IconFactory.cs             tray icon (timeline tile, progress strip, result badge), app.ico export
  Settings.cs                HKCU\Software\UbaTrayTool (AutoShow, NotifyOnFinish, SuppressVisualizer, VisualizerPath)
  StartupRegistration.cs     HKCU Run key "UbaTrayTool"
  TrayIconPromotion.cs       Win11: show the icon on the taskbar, not in the ^ overflow
  DemoData.cs                made-up busy build for --render-demo
  Trace/
    TraceByteReader.cs       uba::BinaryReader port (7-bit ints, UTF-16-count strings, long strings)
    TraceStats.cs            walks the stats blobs (Process/Session/Storage/Kernel/Cache stats)
    TraceParser.cs           incremental port of UbaTraceReader::ReadTrace (trace versions 40–53)
    TraceModel.cs            parsed state (sessions, processes, progress, status…)
    LiveTrace.cs             TraceChannel (named mapping "Default" + mutexes) and NamedTraceMapping
    TraceMonitor.cs          500 ms poller → immutable BuildSnapshot; file open/save; --dump text
    BuildSnapshot.cs         what the UI draws (records)
    VisualizerLocator.cs     finds UbaVisualizer.exe (from the UBT host path, or installed engines)
    Native.cs                P/Invoke
Installer/
  UbaTrayTool.iss            Inno Setup 6 script
  build.ps1                  icon → publish (self-contained win-x64) → ISCC → copy to Z:
  Installer.csproj           NoTargets project: Release build runs build.ps1 (Debug does nothing)
```

## How UBA data is read (the important part)

Reference source: `C:\source\Claude\UE-UbaSource` (sparse clone of EpicGames/UnrealEngine `5.8`,
`Engine/Source/Programs/UnrealBuildAccelerator`). Key files: `Common/Private/UbaTrace.cpp` (writer),
`Visualizer/Private/UbaTraceReader.cpp` (reader), `Common/Private/UbaTraceChannel.cpp`,
`Common/Private/UbaStats.cpp`, `Core/Public/UbaBinaryReaderWriter.h`. The launcher install
(`C:\Program Files\Epic Games\UE_5.8`) has only UBA headers, no .cpp.

- **Channel**: a 256-byte named file mapping called `Default` (session namespace) holds the current trace
  name as UTF-16, guarded by mutex `UbaDefaultChannelDefaultMutex`. Empty means no build running.
- **Listener**: while mutex `UbaDefaultListener` exists, UBA won't launch UbaVisualizer itself
  (menu option "Stop UBA launching UbaVisualizer" holds it).
- **Trace**: a named page-file mapping (SEC_RESERVE). u32 at offset 0 is the *published size*; every byte
  below it is complete entries. Header: u32 size, u32 version, u32 host pid, 7-bit systemStartUs (Unix µs),
  7-bit frequency, 7-bit start ticks. Then entries: `u8 type` + 7-bit time (except String/DriveUpdate) + payload.
  Entries have **no length prefix**, so every payload (incl. stats blobs) must be walked exactly.
- Build ends when the host pid exits (or the pid disappears, when we can't open it), a Summary entry
  arrives, or parsing fails.
- Supported trace versions 40–53 (`TraceParser.MinVersion/MaxVersion`). UE 5.8 writes **53**.
  When Epic bumps `TraceVersion`, diff `UbaTrace.cpp`/`UbaTraceReader.cpp`, update the parser, raise MaxVersion.
- Long strings over 20 chars (breadcrumbs) are Oodle-compressed; we skip them.

## Build, run, test

- Build/run/debug through Visual Studio 2026 via the VsMcp bridge (`vs_session` opens
  `UbaTrayTool.slnx`; don't touch Pete's other VS windows). Debug F5 runs the tray app.
- Parser check: `UbaTrayTool.exe --dump-dir "%LOCALAPPDATA%\UnrealBuildTool" out.txt`. UBT keeps
  `Trace.uba` + backups there. Every file should say `parsed N/N bytes  summary True` with no ERROR.
- Layout check: `--render-demo <dir>` (made-up 64/12/0-lane builds, dark+light) or
  `--render trace.uba out.png [light|dark] [fraction]`. Set args in `Properties/launchSettings.json`
  temporarily; VS uses the first profile. Put it back afterwards.
- Installer: switch to **Release**, build project `Installer` → `Installer\output\UbaTrayToolSetup-<ver>.exe`
  and a copy in `Z:\Software\Claude\UbaTrayTool`. Version = `<Version>` in `UbaTrayTool.csproj`.
  Switch back to Debug afterwards.
- Git: use Pete's git MCP tools (`git_status`, `git_commit`, `git_push`…) on `C:\source\Claude\UbaTrayTool`.

## Pete's design decisions (keep these)

- Standalone product, not TrayTools. Same visual family (Segoe UI, flat cards, rounded corners).
- Follows Windows light/dark and accent colour live.
- One card per running task, up to the logical processor count; cards keep their slot (lane).
- **Width never changes** (columns fixed from the processor count). Height grows at once as jobs
  start and shrinks only after 4 s (debounced) so it doesn't jump about.
- Whole UI at 75% scale (`BuildPopup.UiScale`); column count is still worked out at full size.
- Pops up when a build starts; slides down off-screen 3 s after it ends (not if pinned or hovered).
- Tray icon promoted to the taskbar (not behind the ^ overflow).
- Click on popup closes it; left-click tray pins; double-click opens UbaVisualizer.
