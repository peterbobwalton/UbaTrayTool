# UbaTrayTool

A Windows tray app that shows Unreal Build Accelerator (UBA) builds as they happen. It's a compact alternative to UbaVisualizer.

- Follows UBA's live trace channel (`Default`), so any UBT/UBA build on the machine shows up on its own.
- Popup shows one card per running task, up to the number of logical processors. It grows in height as jobs start and shrinks (after a short delay) as they finish.
- Shows progress, elapsed time and time left, a chart of parallel tasks, errors with their first message, and the local machine plus helpers.
- Follows the Windows light/dark theme and accent colour.
- Opens or saves `.uba` traces, and can hand the current build over to UbaVisualizer.

The trace reader is a C# port of `UbaTraceReader.cpp` from Unreal Engine 5.8 (trace versions 40-53).

## Build

Open `UbaTrayTool.slnx` in Visual Studio 2026 (.NET 10, WinForms).

## Installer

Switch Visual Studio to **Release** and build the `Installer` project, or run `Installer\build.ps1`. Either way it publishes the app self-contained (win-x64) and compiles `Installer\UbaTrayTool.iss` with Inno Setup 6 into `Installer\output\UbaTrayToolSetup-<version>.exe`, then copies the installer to `Z:\Software\Claude\UbaTrayTool`. The version comes from `<Version>` in `UbaTrayTool.csproj`.

The installer puts the app in `Program Files\UbaTrayTool`. It offers "Start with Windows" (ticked by default) and "Stop UBA from opening UbaVisualizer" (unticked by default), and closes a running copy before upgrading.

## Command line

| Arguments | What it does |
|---|---|
| *(none)* | Run in the tray |
| `trace.uba` | Run and open a saved trace |
| `--dump trace.uba out.txt` | Write a text summary of a trace |
| `--render trace.uba out.png [light\|dark] [fraction]` | Render the popup for a trace |
| `--render-demo outDir` | Render the popup with made-up data |
| `--export-icon app.ico` | Write the app icon |
