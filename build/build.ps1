<#
.SYNOPSIS
  Restore, build and test SmartDock. Run from any directory on Windows with the .NET 10 SDK installed.
.EXAMPLE
  ./build/build.ps1 -Configuration Release
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet --info | Select-String 'Version' | Select-Object -First 1
    dotnet restore SmartDock.sln
    dotnet build SmartDock.sln -c $Configuration --no-restore
    dotnet test SmartDock.sln -c $Configuration --no-build --logger "trx;LogFileName=test-results.trx"
}
finally {
    Pop-Location
}
