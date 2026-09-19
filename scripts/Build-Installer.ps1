[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.9.0',
    [string]$AppDirectory = 'dist/app',
    [switch]$DownloadTools
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$AppDir = Join-Path $Root $AppDirectory
$ToolDir = Join-Path $Root 'work/tools/wix3141'
$ToolZip = Join-Path $Root 'work/tools/wix314-binaries.zip'
$ToolHash = '6ac824e1642d6f7277d0ed7ea09411a508f6116ba6fae0aa5f2c7daa2ff43d31'
$Output = Join-Path $Root 'dist/releases'
$BuildDir = Join-Path $Root 'work/installer'
foreach ($Name in @('rhelper-compact.exe','rhelper-controller.exe','rhelper-compact.dll','rhelper-compact.pri','App.xbf','LICENSE','licenses/THIRD_PARTY_LICENSES.md','THIRD_PARTY_NOTICES.md','README.md','SHA256SUMS.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $AppDir $Name))) { throw "Missing payload: $Name. Run scripts/Build.ps1 -OutputDirectory '$AppDirectory' first." }
}
$ExpectedExeHash = ((Get-Content -LiteralPath (Join-Path $AppDir 'SHA256SUMS.txt') -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash -LiteralPath (Join-Path $AppDir 'rhelper-compact.exe')).Hash.ToLowerInvariant() -cne $ExpectedExeHash) { throw 'Executable checksum mismatch.' }
if (-not (Test-Path -LiteralPath $ToolZip)) {
    if (-not $DownloadTools) { throw 'Portable WiX is missing. Pass -DownloadTools to download the pinned official archive into work/tools.' }
    New-Item -ItemType Directory -Path (Split-Path $ToolZip -Parent) -Force | Out-Null
    Invoke-WebRequest -Uri 'https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314-binaries.zip' -OutFile $ToolZip
}
if ((Get-FileHash -LiteralPath $ToolZip).Hash.ToLowerInvariant() -cne $ToolHash) { throw 'WiX archive checksum mismatch.' }
# Refresh the portable compiler from the verified archive, never install it globally.
Expand-Archive -LiteralPath $ToolZip -DestinationPath $ToolDir -Force
New-Item -ItemType Directory -Path $Output,$BuildDir -Force | Out-Null
$NumericVersion = ($Version -split '-', 2)[0]
$LicenseRtf = Join-Path $BuildDir 'license.rtf'
$License = [IO.File]::ReadAllText((Join-Path $Root 'LICENSE'))
$License = $License.Replace('\','\\').Replace('{','\{').Replace('}','\}').Replace("`r",'').Replace("`n",'\par ')
[IO.File]::WriteAllText($LicenseRtf, '{\rtf1\ansi\deff0 {\fonttbl {\f0 Segoe UI;}}\f0\fs20 ' + $License + '}', [Text.Encoding]::ASCII)
$Object = Join-Path $BuildDir 'Product.wixobj'
$RuntimeWxs = Join-Path $BuildDir 'Runtime.wxs'
& (Join-Path $Root 'scripts/installer-runtime.ps1') -AppDirectory $AppDir -OutputFile $RuntimeWxs
$RuntimeObject = Join-Path $BuildDir 'Runtime.wixobj'
$Msi = Join-Path $Output "R-Helper-Compact-$Version-x64.msi"
New-Item -ItemType Directory -Path (Join-Path $Root 'work/logs') -Force | Out-Null
Start-Transcript -Path (Join-Path $Root 'work/logs/installer-build.log') -Force | Out-Null
try {
    & (Join-Path $ToolDir 'candle.exe') -nologo -arch x64 "-dNumericVersion=$NumericVersion" "-dReleaseVersion=$Version" "-dAppDir=$AppDir" "-dLicenseRtf=$LicenseRtf" -out $Object (Join-Path $Root 'installer/Product.wxs') 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "WiX candle failed: $LASTEXITCODE" }
    & (Join-Path $ToolDir 'candle.exe') -nologo -arch x64 "-dAppDir=$AppDir" -out $RuntimeObject $RuntimeWxs 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Runtime manifest compilation failed.' }
    & (Join-Path $ToolDir 'light.exe') -nologo -ext (Join-Path $ToolDir 'WixUIExtension.dll') -cc (Join-Path $BuildDir 'cab-cache') -reusecab -out $Msi $Object $RuntimeObject 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "WiX light / MSI validation failed: $LASTEXITCODE" }
    $Zip = Join-Path $Output "R-Helper-Compact-$Version-x64-portable.zip"
    Compress-Archive -Path (Join-Path $AppDir '*') -DestinationPath $Zip -Force
    $Lines = foreach ($File in @($Msi, $Zip)) {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $File).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($File)
    }
    [IO.File]::WriteAllText((Join-Path $Output 'SHA256SUMS.txt'), ($Lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Built and validated: $Msi"
    Write-Host 'The installer was not installed; application and hardware controller were not launched.'
} finally { Stop-Transcript | Out-Null }
