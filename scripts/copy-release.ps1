param()

$ErrorActionPreference = 'Stop'

function Write-CopyLog {
    param([string]$Message)

    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $line = "[$timestamp] $Message"
    Write-Host $line
    if ($logPath) {
        Add-Content -LiteralPath $logPath -Value $line -Encoding utf8
    }
}

function Wait-ForStableFile {
    param(
        [string]$SourcePath,
        [int]$TimeoutSeconds = 120
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $SourcePath) {
            $item = Get-Item -LiteralPath $SourcePath
            if ($item.Length -gt 0) {
                Start-Sleep -Milliseconds 400
                $stable = Get-Item -LiteralPath $SourcePath
                if ($stable.Length -eq $item.Length) {
                    return $stable
                }
            }
        }
        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for release binary: $SourcePath"
}

function Copy-ReleaseBinary {
    param(
        [string]$SourcePath,
        [string]$DestPath,
        [int]$Retries = 10
    )

    for ($attempt = 1; $attempt -le $Retries; $attempt++) {
        try {
            if (Test-Path -LiteralPath $DestPath) {
                Remove-Item -LiteralPath $DestPath -Force -ErrorAction Stop
            }
            Copy-Item -LiteralPath $SourcePath -Destination $DestPath -Force -ErrorAction Stop
            return
        } catch {
            if ($attempt -eq $Retries) {
                throw "Failed to copy '$SourcePath' to '$DestPath'. Close any running rhelper.exe launched from dist/ and retry. $($_.Exception.Message)"
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$distDir = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$logPath = Join-Path $distDir 'copy-release.log'
Write-CopyLog "copy-release.ps1 started"

$metadata = cargo metadata --no-deps --format-version 1 | ConvertFrom-Json
$pkg = $metadata.packages | Where-Object { $_.name -eq 'r-helper' } | Select-Object -First 1
if (-not $pkg) {
    throw "Could not find r-helper package in cargo metadata"
}

$versionSlug = $pkg.version -replace '\.', '_'
$destName = "rhelper-$versionSlug.exe"
$source = Join-Path $repoRoot 'target\release\rhelper.exe'
$dest = Join-Path $distDir $destName

try {
    Write-CopyLog "Copying release binary to dist/"
    $built = Wait-ForStableFile -SourcePath $source
    Copy-ReleaseBinary -SourcePath $built.FullName -DestPath $dest

    $packaged = Get-Item -LiteralPath $dest
    if ($packaged.Length -ne $built.Length) {
        throw "Packaged binary size mismatch (source=$($built.Length), dest=$($packaged.Length))"
    }

    Write-CopyLog "Packaged: $dest ($($packaged.Length) bytes, $($packaged.LastWriteTime))"
} catch {
    Write-CopyLog "ERROR: $($_.Exception.Message)"
    throw
}
