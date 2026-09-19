[CmdletBinding()]
param([string]$AppDirectory = 'dist/app')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Exe = Join-Path $Root "$AppDirectory/rhelper-compact.exe"
$LogDir = Join-Path $Root 'work/logs'
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
$Report = Join-Path $LogDir ('updates-' + [Guid]::NewGuid().ToString('N') + '.txt')
# Offline test mode exits before creating any window, controller or installer.
$Process = Start-Process -FilePath $Exe -ArgumentList '--update-smoke-test', ('"' + $Report + '"') -WindowStyle Hidden -PassThru
if (-not $Process.WaitForExit(60000)) { $Process.Kill(); throw 'Offline updater tests timed out.' }
if (-not (Test-Path -LiteralPath $Report)) { throw 'Updater test report was not produced.' }
Get-Content -LiteralPath $Report
if ($Process.ExitCode -ne 0) { throw "Offline updater tests failed: $($Process.ExitCode)" }
