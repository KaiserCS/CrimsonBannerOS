# SPDX-License-Identifier: GPL-3.0-or-later
# Joins a Crimson Banner OS installer ISO that was published in parts (GitHub takes files up to 2 GiB), and checks it,
# on Windows. Download every part (NAME.iso.part01, .part02...), NAME.iso.parts.sha256 and NAME.iso.sha256 into one
# folder with this script. Then right-click the script, "Run with PowerShell", or in that folder:
#
#   powershell -ExecutionPolicy Bypass -File merge-iso.ps1              joins every ISO whose parts are here
#   powershell -ExecutionPolicy Bypass -File merge-iso.ps1 NAME.iso     joins that one
#
# Each part is checked first, so a broken or missing download is named before anything is joined; the joined ISO is
# then checked against NAME.iso.sha256. The parts are left as they are: delete them once the ISO checks out.
# On Linux and macOS: merge-iso.sh.
param([string[]]$Iso)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

function Read-Sums([string]$File) {
    # "hash  name" lines (sha256sum's format) as name -> hash
    $sums = @{}
    foreach ($line in Get-Content -LiteralPath $File) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') { $sums[$Matches[2].Trim()] = $Matches[1].ToLower() }
    }
    return $sums
}
function Hash-Of([string]$File) { (Get-FileHash -Algorithm SHA256 -LiteralPath $File).Hash.ToLower() }

function Merge-Iso([string]$Name) {
    $list = "$Name.parts.sha256"
    if (-not (Test-Path -LiteralPath $list)) { Write-Host "$list is missing: download it with the parts." -ForegroundColor Red; return $false }
    Write-Host "==> $Name"
    $sums = Read-Sums $list
    $parts = $sums.Keys | Sort-Object
    $bad = $false
    foreach ($p in $parts) {
        if (-not (Test-Path -LiteralPath $p)) { Write-Host "  missing: $p (download it again)" -ForegroundColor Red; $bad = $true; continue }
        if ((Hash-Of $p) -ne $sums[$p]) { Write-Host "  broken: $p (download it again)" -ForegroundColor Red; $bad = $true; continue }
        Write-Host "  ok: $p"
    }
    if ($bad) { return $false }
    $joining = "$Name.joining"
    $out = [System.IO.File]::Create((Join-Path $PWD $joining))
    try {
        foreach ($p in $parts) {
            $in = [System.IO.File]::OpenRead((Join-Path $PWD $p))
            try { $in.CopyTo($out, 4MB) } finally { $in.Close() }
        }
    } finally { $out.Close() }
    Move-Item -LiteralPath $joining -Destination $Name -Force
    if (Test-Path -LiteralPath "$Name.sha256") {
        $want = (Read-Sums "$Name.sha256")[$Name]
        if ((Hash-Of $Name) -eq $want) {
            Write-Host "  $Name is whole and matches $Name.sha256. The parts can go." -ForegroundColor Green
        } else {
            Write-Host "  $Name doesn't match $Name.sha256: something is wrong. Download it again." -ForegroundColor Red
            return $false
        }
    } else {
        Write-Host "  $Name joined (no $Name.sha256 here to check it against)."
    }
    return $true
}

$ok = $true
if ($Iso) {
    foreach ($i in $Iso) { if (-not (Merge-Iso $i)) { $ok = $false } }
} else {
    $lists = Get-ChildItem -Filter '*.iso.parts.sha256'
    if (-not $lists) { Write-Host "No ISO parts here (NAME.iso.parts.sha256 and NAME.iso.part01...): put this script in the folder you downloaded them to." -ForegroundColor Red; $ok = $false }
    foreach ($l in $lists) { if (-not (Merge-Iso ($l.Name -replace '\.parts\.sha256$', ''))) { $ok = $false } }
}
# started with "Run with PowerShell": keep the window open to read what happened
if (-not $Iso -and $Host.Name -eq 'ConsoleHost') { Read-Host 'Press Enter to close' | Out-Null }
if (-not $ok) { exit 1 }
