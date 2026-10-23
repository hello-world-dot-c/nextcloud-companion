<#
.SYNOPSIS
  Publishes Nextcloud Companion and builds the per-user Windows installer (no admin rights needed).
.DESCRIPTION
  1. dotnet publish (self-contained win-x64, so the user does not need to install .NET)
  2. Inno Setup (ISCC) compiles installer\NextcloudCompanion.iss
  Output: artifacts\installer\NextcloudCompanion-Setup-<version>.exe
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$csproj  = Join-Path $root 'NextcloudCompanion\NextcloudCompanion.csproj'
$publish = Join-Path $root 'artifacts\publish'

$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# Locate ISCC (per-user or machine-wide install, or on PATH).
$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php' }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "Publishing $version ($Configuration, $Runtime)..."
dotnet publish $csproj -c $Configuration -r $Runtime --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host 'Building installer...'
& $iscc "/DAppVersion=$version" "/DSourceDir=$publish" (Join-Path $root 'installer\NextcloudCompanion.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

Write-Host "Done: $(Join-Path $root "artifacts\installer\NextcloudCompanion-Setup-$version.exe")"
