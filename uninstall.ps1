# Removes FnCtrl: stops it, drops the logon task and/or Startup shortcut,
# and deletes the installed copies. Re-launches elevated if needed.
param([switch]$Relaunched)

$ErrorActionPreference = 'Stop'
$TaskName = 'FnCtrl'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

$taskInstalled = [bool](Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)
$inProgramFiles = Test-Path "$env:ProgramFiles\FnCtrl"

if (($taskInstalled -or $inProgramFiles) -and -not (Test-Admin)) {
    if ($Relaunched) { throw 'Elevation did not take effect.' }
    Write-Host 'Administrator rights are needed to remove the task. Approve the UAC prompt...'
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Relaunched')
    return
}

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Get-Process FnCtrl -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'FnCtrl.lnk'
if (Test-Path $lnk) { Remove-Item $lnk -Force }

foreach ($dir in @("$env:ProgramFiles\FnCtrl", "$env:LOCALAPPDATA\FnCtrl")) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host 'FnCtrl removed.' -ForegroundColor Green
