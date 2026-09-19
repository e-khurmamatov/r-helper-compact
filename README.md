# R-Helper Compact

<img src="assets/rhelper.svg" alt="R-Helper Compact" width="96" height="96">

Control performance modes, fan speeds, keyboard lighting and battery charge limits on Razer Blade laptops from the Windows system tray.

[Releases](https://github.com/e-khurmamatov/r-helper-compact/releases) · [Report an issue](https://github.com/e-khurmamatov/r-helper-compact/issues/new/choose) · [Русский](README.ru.md)

## Features

- Performance modes, fan control and battery charge limits.
- Two-minute CPU/GPU temperature charts and adaptive background polling.
- Keyboard lighting effects and an option to leave lighting to OpenRGB.
- AC/battery profiles and Cooling Pad controls.
- Reorderable sections, startup settings and automatic UI language selection.

Support varies by model and firmware. Unknown laptops are blocked from hardware control. See [the device registry](devices/README.md) to request or contribute support.

## Screenshots

<p>
  <img src="assets/screenshots/overview.png" alt="Temperature charts and fan controls" width="360">
  &nbsp;&nbsp;&nbsp;&nbsp;
  <img src="assets/screenshots/lighting.png" alt="Keyboard lighting settings" width="360">
</p>

Razer Blade 14 (2022). Available controls depend on the laptop model.

## Install

Download the x64 MSI or portable ZIP from Releases. The MSI installs to Program Files; extract the entire portable ZIP before running `rhelper-compact.exe`. Left-click the tray icon to open settings; right-click for quick controls and Quit. Close the app before upgrading.

Settings are stored in `%APPDATA%/r-helper-compact/`. English and Russian are included; the app follows the Windows display language and falls back to English. Override it under **Application → Preferences → Language**, then quit from the tray menu and reopen the app. Builds are currently unsigned.

Open **Application → Check for updates** to check GitHub Releases. Installed copies can download a verified MSI and restart after updating; Windows may request administrator permission. Portable copies download a ZIP to extract manually. Checks are manual; preview builds also receive prereleases. Update downloads and installer logs are stored in `%LOCALAPPDATA%/r-helper-compact/updates/`.

## Build

Requires Windows x64, PowerShell 7, Python 3.11+, Visual Studio C++ Build Tools with the Windows SDK, Rust 1.98.1 (MSVC) and .NET SDK 10.0.401.

```powershell
.\scripts/Build.ps1
python -m unittest discover -s tests -v
```

The repository contains all project sources. `controller/` owns hardware orchestration and the private JSON protocol, `librazer/` owns device protocols, and `winui/` owns the interface. `devices/` contains laptop definitions. Cargo compiles the registry automatically. `rust-toolchain.toml`, `global.json` and the Cargo/NuGet lock files pin the toolchains and dependencies. Fork provenance is recorded in `THIRD_PARTY_NOTICES.md`. No upstream checkout or patch application is needed.

[Contributing](CONTRIBUTING.md) · [Translations](winui/Locales/README.md) · [Release builds](RELEASING.md)

## License

Forked from [R-Helper 0.8.5](https://github.com/Robak08/r-helper/tree/8f4ac7b2bc5b2cd077d73254dddb6873d1a5cab6), under MIT. Original author notices are preserved in [LICENSE](LICENSE), [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and [THIRD_PARTY_LICENSES.md](licenses/THIRD_PARTY_LICENSES.md). Independent project; not affiliated with Razer.
