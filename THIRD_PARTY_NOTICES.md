# Third-party notices

R-Helper Compact is an independent WinUI 3 application with a Rust hardware controller.

- **Ivan Romanchuk / Fatalution** — [R-Helper](https://github.com/Fatalution/r-helper), copyright 2025 Ivan Romanchuk, MIT. Original license: [LICENSE](LICENSE).
- **Robak08 and contributors** — [R-Helper 0.8.5](https://github.com/Robak08/r-helper/tree/8f4ac7b2bc5b2cd077d73254dddb6873d1a5cab6), forked at commit `8f4ac7b2bc5b2cd077d73254dddb6873d1a5cab6`. Earlier fork history: [Fatalution/r-helper PR #10](https://github.com/Fatalution/r-helper/pull/10).
- **Tarek Dakhran (tdakhran), blauzim and contributors** — [razer-ctl / librazer](https://github.com/blauzim/razer-ctl), copyright 2024 Tarek Dakhran, MIT. License text: [THIRD_PARTY_LICENSES.md](licenses/THIRD_PARTY_LICENSES.md). Upstream modifications include the Windows GUI, resource embedding, polling and device-state handling.

Compact adds the WinUI interface, tray behavior, device registry and build packaging. Existing hardware commands and upstream author credits are retained.

## Runtime components

Licenses and notices for bundled Microsoft runtime components are listed in [licenses/winui](licenses/winui/README.md). Identical texts are stored once and mapped to their packages.

## Acknowledgements and build tools

Thanks to [OpenRazer](https://github.com/openrazer/openrazer) contributors for documenting how Razer hardware communicates. This information informed the keyboard lighting implementation; OpenRazer source code is not included.

WiX Toolset 3.14.1 builds the installer and is licensed separately under the Microsoft Reciprocal License. Its verified archive is downloaded during packaging and is not stored in this repository.
