# Installs FnCtrl so it starts automatically at logon.
#
#   .\install.ps1                 elevated logon task (works in admin windows too)
#   .\install.ps1 -Mode Startup   Startup-folder shortcut, no admin required
#   .\install.ps1 -ExtraArgs '--map','Fn:11:1:10:A2:1D'    custom mappings
param(
    [ValidateSet('Task', 'Startup')] [string]$Mode = 'Task',
    [string[]]$ExtraArgs = @(),
    [string]$ForUser,          # set automatically when the script re-launches elevated
    [switch]$Relaunched
)

$ErrorActionPreference = 'Stop'
$TaskName = 'FnCtrl'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not $ForUser) { $ForUser = "$env:USERDOMAIN\$env:USERNAME" }

# -File passes an array as one comma-joined string, so split it back apart.
if ($ExtraArgs.Count -eq 1 -and $ExtraArgs[0] -match ',') { $ExtraArgs = $ExtraArgs[0] -split ',' }

# The elevated task must live somewhere non-admins cannot write, otherwise any
# process running as the user could swap the exe and inherit admin rights.
# Task Scheduler also refuses to launch executables under %LOCALAPPDATA% on some
# systems (the action fails with 0x80070002), so Program Files it is.
$dest = if ($Mode -eq 'Task') { "$env:ProgramFiles\FnCtrl" } else { "$env:LOCALAPPDATA\FnCtrl" }

if ($Mode -eq 'Task' -and -not (Test-Admin)) {
    if ($Relaunched) { throw 'Elevation did not take effect.' }
    Write-Host 'Administrator rights are needed to register the logon task. Approve the UAC prompt...'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
                 '-Mode', 'Task', '-ForUser', $ForUser, '-Relaunched')
    if ($ExtraArgs.Count) { $argList += @('-ExtraArgs', ($ExtraArgs -join ',')) }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait
    return
}

# --- build -----------------------------------------------------------------
$exeSrc = Join-Path $PSScriptRoot 'build\FnCtrl.exe'
if (-not (Test-Path $exeSrc)) { & (Join-Path $PSScriptRoot 'build.ps1') }
if (-not (Test-Path $exeSrc)) { throw "Build produced no executable at $exeSrc" }

# --- stop anything already running -----------------------------------------
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Get-Process FnCtrl -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# --- copy ------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $exeSrc (Join-Path $dest 'FnCtrl.exe') -Force
Copy-Item (Join-Path $PSScriptRoot 'src\FnCtrl.cs') (Join-Path $dest 'FnCtrl.cs') -Force
$exe = Join-Path $dest 'FnCtrl.exe'

# --- register --------------------------------------------------------------
$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'FnCtrl.lnk'

if ($Mode -eq 'Task') {
    if (Test-Path $lnk) { Remove-Item $lnk -Force }   # avoid a second, non-elevated copy

    $action = if ($ExtraArgs.Count) {
        New-ScheduledTaskAction -Execute $exe -Argument ($ExtraArgs -join ' ') -WorkingDirectory $dest
    } else {
        New-ScheduledTaskAction -Execute $exe -WorkingDirectory $dest
    }
    $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $ForUser
    $principal = New-ScheduledTaskPrincipal -UserId $ForUser -LogonType Interactive -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -StartWhenAvailable -MultipleInstances IgnoreNew `
                    -ExecutionTimeLimit ([TimeSpan]::Zero) -Compatibility Win8
    $settings.IdleSettings.StopOnIdleEnd = $false

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings `
        -Description 'Maps Apple Magic Keyboard Fn and Eject keys to ordinary Windows keys.' | Out-Null

    Start-ScheduledTask -TaskName $TaskName
    Start-Sleep -Seconds 2
    $result = (Get-ScheduledTaskInfo -TaskName $TaskName).LastTaskResult
    if ($result -ne 0 -and $result -ne 0x00041301) {
        throw ("Task registered but failed to start (0x{0:X8}). Check the path: {1}" -f $result, $exe)
    }
    Write-Host "Installed to $dest and registered the '$TaskName' logon task (elevated)." -ForegroundColor Green
} else {
    $w = New-Object -ComObject WScript.Shell
    $sc = $w.CreateShortcut($lnk)
    $sc.TargetPath = $exe
    $sc.Arguments = ($ExtraArgs -join ' ')
    $sc.WorkingDirectory = $dest
    $sc.Description = 'Maps Apple Magic Keyboard Fn and Eject keys to ordinary Windows keys.'
    $sc.Save()
    Start-Process $exe -ArgumentList $ExtraArgs
    Write-Host "Installed to $dest and added a Startup shortcut." -ForegroundColor Green
    Write-Host "Note: without admin, the remapped keys will not reach elevated windows." -ForegroundColor Yellow
}

Write-Host 'Running now - look for the tray icon.'
