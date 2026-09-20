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

Support varies by model and firmware. Other Razer laptops may use experimental control after host and Blade controller identification. See [the device registry](devices/README.md) to request or contribute support.

## Laptop compatibility

The app checks the computer manufacturer in BIOS/SMBIOS and a chassis SKU beginning with `RZ09-`. Razer USB VID `1532` alone is insufficient: external mice and keyboards also use it.

| Model | PID | Chassis SKU | Profile and evidence |
|---|---|---|---|
| Razer Blade 15 Advanced (2021) | 026D | RZ09-0367, RZ09-0409 | Inherited profile enabled; experimental, no confirmed Compact hardware test |
| Razer Blade Pro 17 (Early 2021) | 026E | RZ09-0368 | Candidate: generic experimental control; command profile unverified |
| Razer Blade 15 Base (Early 2021) | 026F | RZ09-0369 | Candidate: generic experimental control; command profile unverified |
| Razer Blade 14 (2021) | 0270 | RZ09-0370 | Candidate: generic experimental control; command profile unverified |
| Razer Blade 15 Advanced (Mid 2021) | 0276 | RZ09-0409 | Candidate: generic experimental control; command profile unverified |
| Razer Blade 17 (2021) | 0279 | RZ09-0406 | Inherited profile enabled; experimental, no confirmed Compact hardware test |
| Razer Blade 15 (2022) | 028A | RZ09-0421 | Inherited profile enabled; experimental, no confirmed Compact hardware test |
| Razer Blade 17 (2022) | 028B | RZ09-0423 | Inherited profile enabled; experimental, no confirmed Compact hardware test |
| Razer Blade 14 (2022) | 028C | RZ09-0427 | User-confirmed hardware |
| Razer Blade 15 (2023) | 029C | RZ09-0485 | Profile disabled: PID 029C/029E discrepancy |
| Razer Blade 14 (2023) | 029D | RZ09-0482 | Inherited profile enabled; experimental, no confirmed Compact hardware test |
| Razer Blade 15 (2023) | 029E | RZ09-0485 | Candidate: generic experimental control; command profile unverified |
| Razer Blade 16 (2023) | 029F | RZ09-0483 | Inherited profile enabled; experimental, no confirmed Compact hardware test |
| Razer Blade 18 (2023) | 02A0 | RZ09-0484 | Candidate: generic experimental control; command profile unverified |
| Razer Blade 14 (2024) | 02B6 | RZ09-0508 | Candidate: generic experimental control; command profile unverified |
| Razer Blade 16 (2024) | 02B7 | RZ09-0510 | Candidate: generic experimental control; command profile unverified |
| Razer Blade 18 (2024) | 02B8 | RZ09-0509 | Candidate: generic experimental control; command profile unverified |
| Razer Blade 14 (2025) | 02C5 | RZ09-0530 | Inherited profile enabled; experimental, no confirmed Compact hardware test |
| Razer Blade 16 (2025) | 02C6 | RZ09-0528 | Inherited profile enabled; experimental, no confirmed Compact hardware test |
| Razer Blade 18 (2025) | 02C7 | RZ09-0529 | Candidate: generic experimental control; command profile unverified |

Only Blade 14 (2022) is marked `user_confirmed` in the registry. This does not establish that every feature or firmware revision was tested.

- An exact PID/SKU match to an enabled profile uses that protocol. Unverified models show **Experimental support**.
- Other Razer laptops, including models absent from the table, can use generic experimental control when USB product metadata identifies one Blade controller. Available features depend on device responses. Operation is not guaranteed; no other model's initialization sequence is applied.
- Candidate records supply identity references only. `enabled: false` means no authorized model-specific profile; it does not disable the separate generic experimental policy. This also applies to the disabled 029C profile: its model commands are not inherited.
- A non-Razer host, unavailable system identity, missing Blade controller or ambiguous controllers blocks hardware control. Application settings, diagnostics and explicit Quit remain available.

Blade 16 (2024) is an identity candidate based on external `02B7 / RZ09-0510` evidence. This does not identify the review author's actual laptop or confirm compatibility. Sources and limitations are recorded in [its device record](devices/laptops/02b7.json).

Open **Diagnostics and support → Save diagnostic report…** in the status panel. Review the report, save the text file and attach it to a GitHub issue yourself. Export includes a model recognized in a safe format, chassis SKU, USB VID:PID, interface/usage, app/Windows/BIOS/EC versions and support-check events from the current UI session. Raw logs, serial numbers, device paths, settings and user-entered fields are excluded. Opening GitHub does not include these details even in the URL. Model and BIOS/EC fields are filled automatically when available. Model identification can use the chassis catalogue without a HID connection; a Windows-reported family does not imply an exact year. Missing values are optional and are not guessed. Describe the symptoms and optionally correct the fields; corrections survive refreshes. Review free text before copying a request.

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
