<#
.SYNOPSIS
  Builds a self-contained deployment package (no .NET runtime needed on the server).
  Output: .\publish\ApiScheduler\  (copy this whole folder to the server, then run install.ps1 there)
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $root 'publish\ApiScheduler'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet test (Join-Path $root 'tests\ApiScheduler.Tests') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Tests failed - not publishing.' }

$common = @('-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none')

dotnet publish (Join-Path $root 'src\ApiScheduler.Service') @common -o (Join-Path $out 'Service')
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }
dotnet publish (Join-Path $root 'src\ApiScheduler.Desktop') @common -o (Join-Path $out 'Desktop')
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

Copy-Item (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1') $out
Copy-Item (Join-Path $root 'README.md') $out -ErrorAction SilentlyContinue

Write-Host "`nPackage ready: $out" -ForegroundColor Green
