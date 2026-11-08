<#
.SYNOPSIS
  Builds the SmartDock MSI installer (publishes a fresh single-file SmartDock.exe first).
.DESCRIPTION
  Output: artifacts/installer/SmartDock-Setup-v<version>-x64.msi (+ .sha256)
          artifacts/installer/latest.json   (update manifest for in-app updates)
  Run:    powershell -ExecutionPolicy Bypass -File .\build\installer.ps1 -Notes "What changed"
  Then publish both files to the release folder, e.g. .\build\release.ps1 -Target deploy@build01:/srv/smartdock
#>
param(
    [string]$Notes = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build installer/SmartDock.Installer.wixproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed ($LASTEXITCODE)." }

    $msi = Get-ChildItem (Join-Path $root 'artifacts/installer') -Filter 'SmartDock-Setup-*.msi' -Recurse |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $msi) { throw 'No MSI was produced.' }
    $hash = (Get-FileHash $msi.FullName -Algorithm SHA256).Hash
    Set-Content -Path "$($msi.FullName).sha256" -Value "$hash  $($msi.Name)"

    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $Notes) {
        # Default notes: the first lines of this version's CHANGELOG section.
        $changelog = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw -Encoding UTF8
        $match = [regex]::Match($changelog, "(?ms)^## v$([regex]::Escape($version))[^\n]*\n(.*?)(?=^## v|\z)")
        if ($match.Success) { $Notes = ($match.Groups[1].Value -replace '(?m)^#+\s*', '' -replace '\*\*', '').Trim() }
        if ($Notes.Length -gt 600) { $Notes = $Notes.Substring(0, 600) + '...' }
    }
    $manifest = [ordered]@{ version = $version; msi = $msi.Name; sha256 = $hash; notes = $Notes } | ConvertTo-Json
    $manifestPath = Join-Path $msi.DirectoryName 'latest.json'
    [System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ""
    Write-Host "Installer : $($msi.FullName) ($([math]::Round($msi.Length / 1MB, 1)) MB)"
    Write-Host "SHA-256   : $hash"
    Write-Host "Manifest  : $manifestPath"
}
finally {
    Pop-Location
}
