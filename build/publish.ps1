<#
.SYNOPSIS
  Builds a release SmartDock.exe and a zip ready to hand out.
.DESCRIPTION
  Default: ONE self-contained SmartDock.exe (no .NET install needed on the target PC), using the
  publish profile src/SmartDock.App/Properties/PublishProfiles/SingleFile-<runtime>.pubxml.
  The version is read from <Version> in Directory.Build.props, so nothing has to be kept in sync.

  Output:
    artifacts/publish/single-file-<rid>/SmartDock.exe
    artifacts/SmartDock-v<version>-<rid>.zip   (+ .sha256)

  Run it from PowerShell (unsigned scripts need a process-scoped bypass):
    powershell -ExecutionPolicy Bypass -File .\build\publish.ps1
.EXAMPLE
  ./build/publish.ps1                  # win-x64, single self-contained exe
  ./build/publish.ps1 -Runtime win-arm64
  ./build/publish.ps1 -Small           # small exe that needs the .NET 10 Desktop Runtime installed
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$Small
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'Could not read <Version> from Directory.Build.props.' }

if ($Small) {
    if ($Runtime -ne 'win-x64') { throw 'The small (framework-dependent) profile exists for win-x64 only.' }
    $publishProfile = 'Small-FrameworkDependent-win-x64'
    $out = Join-Path $root 'artifacts/publish/small-win-x64'
    $zip = Join-Path $root "artifacts/SmartDock-v$version-$Runtime-small.zip"
}
else {
    $publishProfile = "SingleFile-$Runtime"
    $out = Join-Path $root "artifacts/publish/single-file-$Runtime"
    $zip = Join-Path $root "artifacts/SmartDock-v$version-$Runtime.zip"
}

Push-Location $root
try {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Write-Host "Publishing SmartDock v$version ($publishProfile)..."
    dotnet publish src/SmartDock.App/SmartDock.App.csproj -p:PublishProfile=$publishProfile
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

    $exe = Join-Path $out 'SmartDock.exe'
    if (-not (Test-Path $exe)) { throw "Expected $exe was not produced." }

    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    Set-Content -Path "$zip.sha256" -Value "$hash  $(Split-Path $zip -Leaf)"

    $sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ""
    Write-Host "SmartDock.exe : $exe ($sizeMb MB)"
    Write-Host "Zip           : $zip"
    Write-Host "SHA-256       : $hash"
}
finally {
    Pop-Location
}
