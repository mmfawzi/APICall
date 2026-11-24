<#
.SYNOPSIS
  Installs (or upgrades) the ApiScheduler Windows Service and desktop app on this server.
  Run from an elevated PowerShell inside the published package folder.

.PARAMETER ServiceAccount
  Account the service runs as. It is also the identity used for SQL Windows authentication.
    'NT SERVICE\ApiScheduler'  (default) virtual account; on the network it appears as DOMAIN\SERVERNAME$
    'DOMAIN\gmsaName$'         group Managed Service Account (recommended in a domain - no password to manage)
    'DOMAIN\svc-user'          normal domain account (you'll be prompted for its password)

.EXAMPLE
  .\install.ps1
  .\install.ps1 -ServiceAccount 'CORP\gmsa-apisched$'
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ApiScheduler'),
    [string]$ServiceAccount = 'NT SERVICE\ApiScheduler'
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'ApiScheduler'
$DataDir = Join-Path $env:ProgramData 'ApiScheduler'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script as Administrator.' }

$src = $PSScriptRoot
if (-not (Test-Path (Join-Path $src 'Service\ApiScheduler.Service.exe'))) { throw "Service\ApiScheduler.Service.exe not found next to this script. Run publish.ps1 first." }

# 1. Stop an existing install (upgrade)
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host 'Stopping existing service...'
    if ($existing.Status -ne 'Stopped') { Stop-Service $ServiceName -Force; (Get-Service $ServiceName).WaitForStatus('Stopped', '00:00:30') }
}
Get-Process ApiScheduler.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force

# 2. Copy binaries
Write-Host "Copying files to $InstallDir ..."
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $src 'Service') $InstallDir -Recurse -Force
Copy-Item (Join-Path $src 'Desktop') $InstallDir -Recurse -Force
$exe = Join-Path $InstallDir 'Service\ApiScheduler.Service.exe'

# 3. Register the service
$isVirtual = $ServiceAccount -like 'NT SERVICE\*'
$isGmsa    = $ServiceAccount.EndsWith('$')
if (-not $existing) {
    Write-Host 'Registering service...'
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'API Scheduler' `
        -Description 'Runs scheduled API calls (SQL SELECT -> JSON POST). Managed by ApiScheduler.Desktop.' -StartupType Automatic | Out-Null
}
sc.exe config $ServiceName binPath= "`"$exe`"" start= delayed-auto | Out-Null
# Give the service its own SID (NT SERVICE\ApiScheduler) so folder ACLs can target exactly this service.
sc.exe sidtype $ServiceName unrestricted | Out-Null

if ($isVirtual -or $isGmsa) {
    # Virtual accounts and gMSAs have no password.
    sc.exe config $ServiceName obj= $ServiceAccount | Out-Null
} else {
    $cred = Get-Credential -UserName $ServiceAccount -Message "Password for service account $ServiceAccount"
    sc.exe config $ServiceName obj= $ServiceAccount password= $cred.GetNetworkCredential().Password | Out-Null
    Write-Host "Make sure $ServiceAccount has the 'Log on as a service' right." -ForegroundColor Yellow
}
if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed ($LASTEXITCODE)." }

# Restart automatically on failure: after 1 min, 1 min, then 5 min; reset the counter daily.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

# 4. Event log source
if (-not [Diagnostics.EventLog]::SourceExists($ServiceName)) { New-EventLog -LogName Application -Source $ServiceName }

# 5. Data folder (config with encrypted secrets, state, history, logs) - locked down
Write-Host "Securing $DataDir ..."
New-Item -ItemType Directory -Force $DataDir, (Join-Path $DataDir 'logs'), (Join-Path $DataDir 'requests') | Out-Null
$acctForAcl = if ($isVirtual) { "NT SERVICE\$ServiceName" } else { $ServiceAccount }
icacls $DataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${acctForAcl}:(OI)(CI)M" /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Setting folder permissions failed.' }

# 6. Start Menu shortcut for the desktop app (it requests elevation itself)
$lnk = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\API Scheduler.lnk'
$ws = New-Object -ComObject WScript.Shell
$s = $ws.CreateShortcut($lnk)
$s.TargetPath = Join-Path $InstallDir 'Desktop\ApiScheduler.Desktop.exe'
$s.WorkingDirectory = Join-Path $InstallDir 'Desktop'
$s.Save()

# 7. Start
Start-Service $ServiceName
(Get-Service $ServiceName).WaitForStatus('Running', '00:00:30')

Write-Host "`nInstalled. Service '$ServiceName' is running as $ServiceAccount." -ForegroundColor Green
Write-Host "Open 'API Scheduler' from the Start Menu to add connections and jobs."
if ($isVirtual) {
    Write-Host "SQL (Windows auth): create a login for DOMAIN\$env:COMPUTERNAME`$ (or 'NT SERVICE\$ServiceName' if SQL is on this server)." -ForegroundColor Yellow
}
Write-Host "Grant that login only: SELECT + UPDATE on the source table(s). Example:" -ForegroundColor Yellow
Write-Host "  GRANT SELECT, UPDATE ON dbo.NG_MC_SURVEY_SMS_LOG TO [login];"
