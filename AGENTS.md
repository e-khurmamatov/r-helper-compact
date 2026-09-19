# Agent guidelines

Use README.md for setup and RELEASING.md for packaging. Read devices/README.md before changing support and winui/Locales/README.md before changing translations. No private handoff files are required. Keep repository documentation and code comments in English, except README.ru.md and translations.

## Project boundaries

- This is an independently maintained fork. Edit the maintained sources directly; do not restore upstream downloads, overlays, patch manifests or the old egui frontend. Upstream references document attribution, not build dependencies.
- Scope is Razer laptops and the existing Cooling Pad support. Do not reintroduce headset or general peripheral support.
- Keep Compact's settings under `%APPDATA%/r-helper-compact/` and its startup/singleton identities separate from R-Helper. Never migrate or overwrite the original application's settings implicitly.

## Hardware and state

- Do not launch the real controller, install a build, enable startup, apply profiles or send hardware commands just to validate a change. Hardware testing requires an explicit request covering the action.
- Device support must fail closed in the controller as well as the UI. Preserve the exact PID/SKU gate; unknown, ambiguous and disabled records must not open HID for control. Do not enable a candidate from a model name or upstream listing alone.
- Fan Auto mode delegates laptop fan regulation to firmware. Polling optimizations must not change enforcement deadlines or stop measurements needed by active automatic features.
- OpenRGB ownership means leaving laptop lighting alone, not integrating its SDK. Check ownership and device availability when queued commands execute, not only when controls are rendered.
- Slider writes are coalesced: preserve the final edit during an in-flight command, and do not let polling overwrite a pending user value. Keep unavailable readings explicit; zero is not a substitute for missing telemetry.
- Hiding the tray popup must not terminate the controller or active automation. Preserve the explicit Quit action.

## Verification

- Use `scripts/Build.ps1` for native builds; it supplies the MSVC environment, static Rust CRT and self-contained WinUI resources. A raw `dotnet build` is not a distributable package.
- Prefer a fresh output folder under `dist/` when validating packaging. Reusing a populated folder can retain obsolete files. Never overwrite a running EXE; use another output folder.
- Run checks appropriate to the change. `python -m unittest discover -s tests -v` checks repository/package invariants; the build runs Rust workspace tests. Leave live WMI tests ignored unless hardware testing was requested.
- For UI checks, run the built EXE with `--ui-smoke-test --ui-culture=en-US` or `ru-RU`. This uses synthetic data without a controller. Screenshots and `smoke-checks.txt` are written beside the EXE, so use a disposable copy outside the release payload; run smoke instances sequentially.
- `scripts/Test-Updates.ps1 -AppDirectory <build-folder>` runs offline updater tests with simulated HTTP and installer outcomes. Do not invoke the internal `--apply-update` entry point as a test.
- Build success, synthetic UI checks, MSI validation, installation and hardware behavior are separate claims. Report exactly what was checked. UI smoke does not prove native drag gestures or hardware behavior.

## Packaging and attribution

- `assets/rhelper.svg` is the logo source; `assets/rhelper.ico` is embedded during the scripted build. Window/tray icons come from the EXE, and MSI uses that EXE's icon. Changing the ICO requires rebuilding, not merely copying it beside the app. README uses the SVG, which must also ship with the bundled README.
- Keep the MSI UpgradeCode stable. Increase the numeric version for published upgrades, synchronize Cargo/.NET/build defaults and release documentation, and never replace published release assets. Local unreleased rebuilds may retain their version.
- Keep WiX checksum verification and MSI validation enabled. After repackaging, verify MSI/ZIP contents and regenerate release checksums. Keep smoke reports and screenshots out of shipped payloads.
- Preserve inherited copyright and license conditions even though the project is independent. `LICENSE` also supplies the MSI license screen; changing its presentation must not erase upstream attribution or invent authorship.
- Third-party texts live under `licenses/`, with attribution and links in `THIRD_PARTY_NOTICES.md`. Byte-identical texts can share one copy with a package mapping. An `AS IS` disclaimer does not remove notice obligations; do not discard notices merely because a dependency has no visible UI.
- Keep generated files, logs and private validation notes under ignored `work/` or `dist/`. This AGENTS.md is public and tracked: do not put workstation paths, conversation history or transient test counts here.
- Do not commit, tag, push or publish without explicit authorization. Preparing files and local release artifacts is not authorization to publish.
