<#
.SYNOPSIS
  Uploads the latest MSI + latest.json to the release folder that SmartDock checks for updates.
.DESCRIPTION
  -Target accepts:
    user@host:/srv/smartdock     (Linux over SSH; uses Windows' OpenSSH scp and your normal SSH key)
    \\server\share\smartdock     (network share)
    D:\releases\smartdock        (folder)
  The MSI is copied first and latest.json last, so clients never see a manifest pointing to a missing file.
  Build the installer first (Rider: Installer configuration, or build\installer.ps1); both write latest.json.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build\release.ps1 -Target deploy@10.0.0.15:/srv/smartdock
#>
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [int]$Port = 22
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root 'artifacts\installer'
$manifestPath = Join-Path $dir 'latest.json'
if (-not (Test-Path $manifestPath)) {
    throw "No $manifestPath. Build the installer first (Rider: Installer configuration > Build, or build\installer.ps1)."
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$msi = Join-Path $dir $manifest.msi
if (-not (Test-Path $msi)) { throw "$msi not found - rebuild the installer." }
$actual = (Get-FileHash $msi -Algorithm SHA256).Hash
if ($actual -ne $manifest.sha256) { throw 'The MSI does not match latest.json - rebuild the installer.' }

Write-Host "Releasing SmartDock v$($manifest.version)"
Write-Host "  MSI      : $msi"
Write-Host "  Manifest : $manifestPath"
Write-Host "  Target   : $Target"

if ($Target -match '^(?<login>[^\\/:@\s]+@[^\\/:\s]+):(?<path>.+)$') {
    $login = $Matches['login']
    $remote = $Matches['path'].TrimEnd('/')
    foreach ($tool in 'ssh', 'scp') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "$tool.exe not found. Install it: Settings > System > Optional features > OpenSSH Client."
        }
    }

    Write-Host "1/3 Creating $remote on $login (if needed)..."
    ssh -p $Port $login "mkdir -p '$remote'"
    if ($LASTEXITCODE -ne 0) { throw "ssh to $login failed (exit $LASTEXITCODE). Check that 'ssh -p $Port $login' works in this terminal." }

    Write-Host "2/3 Uploading $($manifest.msi)..."
    scp -P $Port $msi "${login}:$remote/"
    if ($LASTEXITCODE -ne 0) { throw "Uploading the MSI failed (exit $LASTEXITCODE). Does $login have write access to $remote?" }

    Write-Host "3/3 Uploading latest.json..."
    scp -P $Port $manifestPath "${login}:$remote/"
    if ($LASTEXITCODE -ne 0) { throw "Uploading latest.json failed (exit $LASTEXITCODE)." }

    Write-Host ""
    Write-Host "Done. Check on the server:  ssh -p $Port $login ls -l '$remote'"
    Write-Host "SmartDock release location: ssh://$login$(if ($Port -ne 22) { ":$Port" })$remote"
}
else {
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    Copy-Item $msi $Target -Force
    Copy-Item $manifestPath $Target -Force
    Write-Host "Done. SmartDock release location: $Target"
}
