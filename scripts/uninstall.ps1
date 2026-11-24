<#
.SYNOPSIS
  Removes the ApiScheduler service and its program folders. The Data folder (config, history, logs)
  is kept unless -RemoveData is given.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'D:\APICall',
    [switch]$RemoveData
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'ApiScheduler'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script as Administrator.' }

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Write-Host 'Service removed.'
}
Get-Process ApiScheduler.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\API Scheduler.lnk') -ErrorAction SilentlyContinue

foreach ($d in 'ApiService', 'ApiDesktop') {
    $p = Join-Path $InstallDir $d
    if (Test-Path $p) { Remove-Item $p -Recurse -Force; Write-Host "Removed $p" }
}
Remove-Item (Join-Path $InstallDir 'install.ps1'), (Join-Path $InstallDir 'README.md') -ErrorAction SilentlyContinue

if ($RemoveData) {
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed $InstallDir including Data (config, history, logs)."
} else {
    Write-Host "Kept $InstallDir\Data (use -RemoveData to delete it)."
}
