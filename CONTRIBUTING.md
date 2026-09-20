# Contributing

R-Helper Compact is an independently maintained fork with its own releases and roadmap. Upstream changes are reviewed and adopted selectively. Preserve original attribution.

Build with `scripts/Build.ps1` and run `python -m unittest discover -s tests -v` plus `scripts/Test-Updates.ps1`.

Edit sources directly in `winui/`, `controller/` or `librazer/`. Cargo embeds `devices/laptops/*.json` at build time; do not edit generated registry output. Keep hardware protocol changes separate from UI refactors and preserve upstream licenses. Do not commit binaries, build logs, personal settings or credentials.

For new laptops, follow [devices/README.md](devices/README.md). For UI translations, see [winui/Locales/README.md](winui/Locales/README.md).

PRs should describe the change and tests performed. Hardware changes should list the model, identifiers, sources and functions actually tested. Never bypass device identification or enable experimental commands just to demonstrate the UI.

Documentation-only PRs automatically skip the Windows build, CodeQL and dependency review after a short changed-file check. This covers the READMEs, contributor/release/agent guidelines, Markdown under `docs/`, and screenshots. Source code, device definitions, workflows, dependencies, license notices and packaging assets still require full CI. Mixed changes also run full CI. Do not add `[skip ci]` to commits: it prevents required checks from reporting a result. Scheduled/manual checks and tagged release builds always run normally.

Dependency updates run through pull requests and CI; do not auto-merge them. For Windows App SDK or .NET runtime updates, review the bundled texts and package mapping in `licenses/winui/` before merging.
