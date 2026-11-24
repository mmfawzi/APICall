<#
.SYNOPSIS
  Removes the ApiScheduler service and program files. Configuration/logs in %ProgramData%\ApiScheduler
  are kept unless -RemoveData is given.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ApiScheduler'),
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
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force; Write-Host "Removed $InstallDir" }

if ($RemoveData) {
    Remove-Item (Join-Path $env:ProgramData 'ApiScheduler') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Configuration, history and logs removed.'
} else {
    Write-Host "Configuration kept in $env:ProgramData\ApiScheduler (use -RemoveData to delete it)."
}
