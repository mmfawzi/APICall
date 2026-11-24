<#
.SYNOPSIS
  Installs (or upgrades) API Scheduler into ONE folder (default D:\APICall):
      D:\APICall\ApiService\   Windows Service
      D:\APICall\ApiDesktop\   Admin desktop app
      D:\APICall\Data\         config.json, state, history, logs
  Run from an elevated PowerShell. Re-running it upgrades in place and keeps Data\.

.PARAMETER InstallDir
  Target folder. Default D:\APICall.

.PARAMETER ServiceAccount
  Account the service runs as (also the identity used for SQL Windows authentication).
    'NT SERVICE\ApiScheduler'  (default) virtual account; on the network it appears as DOMAIN\SERVERNAME$
    'DOMAIN\gmsaName$'         group Managed Service Account
    'DOMAIN\svc-user'          normal domain account (you'll be prompted for its password)

.EXAMPLE
  .\install.ps1
  .\install.ps1 -InstallDir 'E:\Apps\APICall'
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'D:\APICall',
    [string]$ServiceAccount = 'NT SERVICE\ApiScheduler'
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'ApiScheduler'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script as Administrator.' }

$src = $PSScriptRoot
if (-not (Test-Path (Join-Path $src 'ApiService\ApiScheduler.Service.exe'))) { throw "ApiService\ApiScheduler.Service.exe not found next to this script." }

$drive = [IO.Path]::GetPathRoot($InstallDir)
if (-not (Test-Path $drive)) { throw "Drive $drive does not exist on this server. Use -InstallDir to choose another location." }

$ServiceDir = Join-Path $InstallDir 'ApiService'
$DesktopDir = Join-Path $InstallDir 'ApiDesktop'
$DataDir    = Join-Path $InstallDir 'Data'
$exe        = Join-Path $ServiceDir 'ApiScheduler.Service.exe'

# Locations used by older versions (moved into $InstallDir by this script)
$OldProgramDir = Join-Path $env:ProgramFiles 'ApiScheduler'
$OldDataDir    = Join-Path $env:ProgramData 'ApiScheduler'

# 1. Stop what is running
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host 'Stopping service...'
    Stop-Service $ServiceName -Force
    (Get-Service $ServiceName).WaitForStatus('Stopped', '00:00:30')
}
Get-Process ApiScheduler.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force

# 2. Copy program files
Write-Host "Installing to $InstallDir ..."
New-Item -ItemType Directory -Force $ServiceDir, $DesktopDir, $DataDir, (Join-Path $DataDir 'logs'), (Join-Path $DataDir 'requests') | Out-Null
Copy-Item (Join-Path $src 'ApiService\*') $ServiceDir -Recurse -Force
Copy-Item (Join-Path $src 'ApiDesktop\*') $DesktopDir -Recurse -Force
Copy-Item (Join-Path $src 'install.ps1'), (Join-Path $src 'uninstall.ps1') $InstallDir -Force
Copy-Item (Join-Path $src 'README.md') $InstallDir -Force -ErrorAction SilentlyContinue

# 3. Move settings from an older install (ProgramData) - only if Data\ has no config yet
if ((Test-Path (Join-Path $OldDataDir 'config.json')) -and -not (Test-Path (Join-Path $DataDir 'config.json'))) {
    Write-Host "Moving existing settings from $OldDataDir ..."
    foreach ($f in 'config.json', 'state.json', 'history.jsonl') {
        $p = Join-Path $OldDataDir $f
        if (Test-Path $p) { Copy-Item $p $DataDir -Force }
    }
    Copy-Item (Join-Path $OldDataDir 'logs\*') (Join-Path $DataDir 'logs') -Force -ErrorAction SilentlyContinue
    Remove-Item $OldDataDir -Recurse -Force
}

# 4. Register / update the service
$isVirtual = $ServiceAccount -like 'NT SERVICE\*'
$isGmsa    = $ServiceAccount.EndsWith('$')
if (-not $existing) {
    Write-Host 'Registering service...'
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'API Scheduler' `
        -Description 'Runs scheduled API calls (SQL SELECT -> JSON POST). Managed by ApiScheduler.Desktop.' -StartupType Automatic | Out-Null
}
sc.exe config $ServiceName binPath= "`"$exe`"" start= delayed-auto | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed ($LASTEXITCODE)." }
# Own service SID (NT SERVICE\ApiScheduler) so folder permissions can target exactly this service.
sc.exe sidtype $ServiceName unrestricted | Out-Null

if ($isVirtual -or $isGmsa) {
    sc.exe config $ServiceName obj= $ServiceAccount | Out-Null
} else {
    $cred = Get-Credential -UserName $ServiceAccount -Message "Password for service account $ServiceAccount"
    sc.exe config $ServiceName obj= $ServiceAccount password= $cred.GetNetworkCredential().Password | Out-Null
    Write-Host "Make sure $ServiceAccount has the 'Log on as a service' right." -ForegroundColor Yellow
}
if ($LASTEXITCODE -ne 0) { throw "Setting the service account failed ($LASTEXITCODE)." }

# Restart automatically on failure: after 1 min, 1 min, then 5 min; reset the counter daily.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

if (-not [Diagnostics.EventLog]::SourceExists($ServiceName)) { New-EventLog -LogName Application -Source $ServiceName }

# 5. Lock the folder down.
#    D:\ usually lets ordinary users create/modify files, so the install folder must NOT inherit from it:
#    otherwise a normal user could replace the service exe. Only SYSTEM + Administrators have full control;
#    the service account can read/run everything and write only to Data\.
Write-Host "Securing $InstallDir ..."
$svcAcct = if ($isVirtual) { "NT SERVICE\$ServiceName" } else { $ServiceAccount }
icacls $InstallDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${svcAcct}:(OI)(CI)RX" /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Setting folder permissions failed.' }
icacls "$InstallDir\*" /reset /T /C /Q | Out-Null
icacls $DataDir /grant "${svcAcct}:(OI)(CI)M" /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Setting Data folder permissions failed.' }

# 6. Remove program files of an older version (Program Files)
if (Test-Path $OldProgramDir) {
    Write-Host "Removing old program folder $OldProgramDir ..."
    Remove-Item $OldProgramDir -Recurse -Force
}

# 7. Start Menu shortcut for the desktop app (it asks for elevation itself)
$lnk = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\API Scheduler.lnk'
$ws = New-Object -ComObject WScript.Shell
$s = $ws.CreateShortcut($lnk)
$s.TargetPath = Join-Path $DesktopDir 'ApiScheduler.Desktop.exe'
$s.WorkingDirectory = $DesktopDir
$s.Save()

# 8. Start
Start-Service $ServiceName
(Get-Service $ServiceName).WaitForStatus('Running', '00:00:30')

Write-Host "`nInstalled in $InstallDir. Service '$ServiceName' is running as $ServiceAccount." -ForegroundColor Green
Write-Host "  App      : $DesktopDir\ApiScheduler.Desktop.exe  (also in the Start Menu)"
Write-Host "  Settings : $DataDir\config.json"
Write-Host "  Logs     : $DataDir\logs"
if ($isVirtual) {
    Write-Host "`nSQL (Windows auth): if SQL Server is on this server, create a login for [NT SERVICE\$ServiceName];" -ForegroundColor Yellow
    Write-Host "otherwise create one for [$env:USERDOMAIN\$env:COMPUTERNAME`$] on the SQL server." -ForegroundColor Yellow
}
Write-Host "Grant it only: GRANT SELECT, UPDATE ON dbo.NG_MC_SURVEY_SMS_LOG TO [login];" -ForegroundColor Yellow
