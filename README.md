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

## Command line

| Arguments | What it does |
|---|---|
| *(none)* | Run in the tray |
| `trace.uba` | Run and open a saved trace |
| `--dump trace.uba out.txt` | Write a text summary of a trace |
| `--render trace.uba out.png [light\|dark] [fraction]` | Render the popup for a trace |
| `--render-demo outDir` | Render the popup with made-up data |
| `--export-icon app.ico` | Write the app icon |
