# Releases

```powershell
.\scripts/Build.ps1 -OutputDirectory dist/app -Version 0.10.0
.\scripts/Build-Installer.ps1 -Version 0.10.0 -DownloadTools
```

Outputs are the x64 MSI, portable ZIP and SHA256SUMS.txt in `dist/releases`. Logs are in `work/logs`. WiX is downloaded to `work/tools` and checked against a pinned SHA-256; MSI validation remains enabled.

The MSI installs to Program Files and registers the application in Windows. It does not launch the app or enable startup. User settings survive uninstall. Keep the UpgradeCode unchanged and increase the numeric version for every release, including preview-to-stable upgrades (for example, 0.10.0-preview to 0.10.1).

## GitHub

PRs and pushes to main run the Windows build. Manually running the release workflow produces artifacts without publishing. Pushing a version tag such as `v0.10.0` builds and publishes MSI, ZIP and checksums to [Releases](https://github.com/e-khurmamatov/r-helper-compact/releases). Existing releases are not overwritten.

Before tagging, test installation, upgrade and uninstall on a clean Windows system. Test hardware changes on the relevant laptop; compilation and UI smoke tests do not verify hardware behavior.

The in-app updater selects the highest compatible version from this repository's releases. Each release must contain the exact MSI/ZIP filenames produced by the build and SHA256SUMS.txt. Stable builds exclude prereleases; preview builds accept both. SHA-256 verifies the download against the published release, not publisher identity; protect release access. Never replace published assets.

Test in-app upgrades from an installed copy, including UAC cancellation, installer failure and automatic restart. The updater waits for both the UI and controller to exit, runs MSI without automatic Windows restart, then reopens the app. Portable copies only download a ZIP. No background service or scheduled checks are installed.

Run `./scripts/Test-Updates.ps1 -AppDirectory dist/app` for offline updater tests. CI runs these with simulated HTTP and installer outcomes; they do not install anything or access hardware.

New release builds attest the MSI, portable ZIP and checksums. Verify a downloaded file with `gh attestation verify <file> --repo e-khurmamatov/r-helper-compact`. Attestations establish build provenance; they do not replace Windows code signing.
