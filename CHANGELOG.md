# Changelog

## 0.10.1 (unreleased)

- Replace the unmaintained bincode dependency with explicit encoding of the existing 90-byte HID packet format.
- Update Windows App SDK and runtime notices, Rust dependencies and GitHub Actions.
- Preserve hardware command bytes and settings compatibility.

## 0.10.0

- Add a saved application language preference with system, English and Russian options; changes apply after restart.
- Add real English and Russian screenshots to the README.
- Preserve existing settings and hardware behavior; no breaking changes.

## 0.9.0

First independent R-Helper Compact release, forked from R-Helper 0.8.5.

- Native WinUI 3 tray panel with reorderable sections and English/Russian localization.
- Laptop performance, fan, charging and lighting controls, plus Cooling Pad support.
- Compiled laptop registry with unsupported-device protection and support requests.
- Two-minute CPU/GPU temperature charts and adaptive polling.
- AC/battery profiles and optional external keyboard lighting ownership.
- GitHub update checks, verified downloads, MSI installation and portable packages.
- Self-contained source workspace; unused peripheral battery and headset code removed.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for the fork point and author credits.
