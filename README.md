# Diogenes ImGui

An ImGui front end for [CaesarSuite](https://github.com/jglim/CaesarSuite)'s Diogenes II, a tool for talking to Mercedes-Benz control units (ECUs) through a J2534 adapter using CBF files.

It shows everything from the CBF file in plain English.

## Download

Get `DiogenesImGui-win-x86.zip` from the [Releases](../../releases) page, unzip it, and run `DiogenesImGui.exe`. No .NET install is needed. Keep `glfw3.dll` and `cimgui.dll` next to the exe.

To publish a new release, push a version tag: `git tag v1.0.1 && git push origin v1.0.1`.

## What's different from Diogenes II

- **ImGui interface** (Silk.NET + ImGui.NET) instead of WinForms, scaled to the screen so it stays readable on 4K.
- **Automatic German to English translation** of command names, descriptions, inputs, results, coding options and fault codes:
  - uses the CBF's own English text table if it has one
  - otherwise translates offline with a built-in automotive dictionary that also splits German compound words
  - and refines the result with Google Translate in the background (cached, can be turned off under View)
- **Simpler to use**: a three-step setup bar (open CBF, pick adapter, connect), commands grouped into plain categories and color-coded by risk, a confirmation before anything that changes the car, decoded results, and ECU error codes explained.
- **New tabs**: read-only coding view that can read the current values from the car, and a searchable fault code list.
- Advanced mode (View menu) brings back raw bytes, bit positions, connection parameters and a raw hex console.

## Layout

| Folder | What |
| --- | --- |
| `DiogenesImGui/` | The new ImGui application |
| `Diogenes/` | The original WinForms Diogenes II, unchanged |
| `Caesar/`, `CaesarConnection/`, `Interpreter/`, `J2534-Sharp/` | Libraries from CaesarSuite (`v2` branch) |

## Building

Requires the .NET 8 SDK. The app is 32-bit because J2534 adapter drivers usually are.

```
dotnet publish DiogenesImGui -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -o publish
```

Run `publish/DiogenesImGui.exe` and keep `glfw3.dll` and `cimgui.dll` next to it. `DiogenesImGui.exe --demo` opens a small made-up ECU to try the interface without a CBF file.

CBF and CFF files are Mercedes-Benz data and are not included.

## Status

The CBF browsing and translation are tested with a real CBF file. Connecting, running commands, reading coding and memory, and flashing are ported from Diogenes II but have not yet been tested on a car. Flashing is experimental, as in the original.

## License

MIT. Based on [CaesarSuite](https://github.com/jglim/CaesarSuite) by JinGen Lim, see `LICENSE`.
