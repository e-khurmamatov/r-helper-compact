[CmdletBinding()]
param(
    [string]$OutputDirectory = 'dist/app',
    [string]$Version = '0.9.0'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Source = $Root
$TargetDir = Join-Path $Root 'work/target'
$Dist = Join-Path $Root $OutputDirectory
$Log = Join-Path $Root 'work/logs/build.log'
New-Item -ItemType Directory -Path (Split-Path $Log -Parent) -Force | Out-Null
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw 'Rust stable (MSVC) is not installed or is not on PATH. See README.md. Nothing was installed automatically.'
}
if ($env:OS -ne 'Windows_NT') { throw 'This build script targets Windows x64 with the MSVC toolchain.' }
$Target = 'x86_64-pc-windows-msvc'
$env:COMPACT_UI_VERSION = $Version
# Link the Microsoft C runtime into the binary so clean Windows 11 systems
# do not need a separate Visual C++ Redistributable installation.
$CrtConfig = 'target.x86_64-pc-windows-msvc.rustflags=["-C","target-feature=+crt-static"]'
$RustBin = Split-Path -Parent (Get-Command cargo -ErrorAction Stop).Source
# Import MSVC / Windows SDK environment when not already in a developer shell.
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $VsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $VsWhere)) { throw 'Visual Studio Build Tools with Desktop development with C++ and a Windows SDK are required.' }
    $Install = (& $VsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
    if (-not $Install) { throw 'MSVC x64 build tools not found. Install the C++ desktop build workload.' }
    $Module = Join-Path $Install 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
    if (-not (Test-Path -LiteralPath $Module)) { throw 'Visual Studio developer shell module not found; run from Developer PowerShell for VS.' }
    Import-Module $Module
    Enter-VsDevShell -VsInstallPath $Install -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64'
}
$env:PATH = "$RustBin;$env:PATH"
Start-Transcript -Path $Log -Force | Out-Null
try {
    Push-Location $Source
    try {
        & rustc --version 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'rustc could not run.' }
        & cargo test --locked --config $CrtConfig --target-dir $TargetDir --target $Target --workspace
        if ($LASTEXITCODE -ne 0) { throw 'Controller / hardware library tests failed.' }
        & cargo build --release --locked --config $CrtConfig --target-dir $TargetDir --target $Target -p rhelper-controller
        if ($LASTEXITCODE -ne 0) { throw 'Controller build failed. See work/logs/build.log.' }
    } finally { Pop-Location }
    New-Item -ItemType Directory -Path $Dist -Force | Out-Null
    $Exe = Join-Path $Dist 'rhelper-compact.exe'
    Copy-Item -LiteralPath (Join-Path $TargetDir "$Target/release/rhelper-controller.exe") -Destination (Join-Path $Dist 'rhelper-controller.exe') -Force
    foreach ($Name in @('LICENSE','THIRD_PARTY_NOTICES.md','README.md')) {
        Copy-Item -LiteralPath (Join-Path $Root $Name) -Destination $Dist -Force
    }
    Copy-Item -LiteralPath (Join-Path $Root 'README.ru.md') -Destination $Dist -Force
    New-Item -ItemType Directory -Path (Join-Path $Dist 'assets') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $Root 'assets/rhelper.svg') -Destination (Join-Path $Dist 'assets/rhelper.svg') -Force
    Copy-Item -LiteralPath (Join-Path $Root 'licenses') -Destination $Dist -Recurse -Force
    $Publish = Join-Path $Root ('work/winui-publish-' + [Guid]::NewGuid().ToString('N'))
    & dotnet publish (Join-Path $Root 'winui/RHelper.Compact.csproj') -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o $Publish "-p:Version=$Version" "-p:ApplicationIcon=$Root/assets/rhelper.ico"
    if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed. No WinUI release is ready.' }
    foreach ($File in Get-ChildItem -LiteralPath $Publish -Force) {
        # Ship Russian and English satellite resources; other languages fall back to English.
        if ($File.PSIsContainer -and $File.Name -match '^[a-z]{2,3}(-[A-Za-z]{2,8}){0,2}$' -and $File.Name -notmatch '^(ru|en)(-|$)') { continue }
        if ($File.Extension -ne '.pdb') { Copy-Item -LiteralPath $File.FullName -Destination $Dist -Recurse -Force }
    }
    $Hash = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $Dist 'SHA256SUMS.txt'), "$Hash  rhelper-compact.exe`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host "Build succeeded: $Exe"
    Write-Host 'Unsigned local build. Hardware behavior has not been validated by this script.'
} finally { Stop-Transcript | Out-Null }
