<#
.SYNOPSIS
  Builds a self-contained deployment package (no .NET runtime needed on the server).
  Output: .\publish\ApiScheduler\  (copy this whole folder to the server, then run install.ps1 there)
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $root 'publish\ApiScheduler'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET is not installed. Install the .NET 10 SDK (x64) from https://dotnet.microsoft.com/download/dotnet/10.0'
}
$sdks = @(dotnet --list-sdks 2>$null) | Where-Object { $_ -match '^10\.' }
if (-not $sdks) {
    $found = (@(dotnet --list-sdks 2>$null) -join ', ')
    if (-not $found) { $found = 'none (only the runtime is installed)' }
    throw ".NET 10 SDK not found. Installed SDKs: $found. Install the '.NET 10 SDK - Windows x64' (not the Runtime / Hosting Bundle) from https://dotnet.microsoft.com/download/dotnet/10.0, then open a NEW PowerShell window."
}

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
