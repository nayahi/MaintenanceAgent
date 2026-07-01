#Requires -Version 7.0
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Registers a weekly Windows Task Scheduler task that runs Invoke-MaintenanceScan.ps1.

.DESCRIPTION
    Run this script ONCE as Administrator to set up the weekly task.
    It is idempotent: safe to re-run if you want to change settings.

    The task runs every Monday at 09:00 AM as SYSTEM (highest privilege).
    If the machine is off on Monday, it runs as soon as it next boots (StartWhenAvailable).

.EXAMPLE
    # Register the weekly task
    pwsh -ExecutionPolicy Bypass -File "C:\Users\nayah\Scripts\ScheduleMaintenanceTask.ps1"

    # Trigger it manually right now for testing
    Start-ScheduledTask -TaskName WeeklySystemMaintenance -TaskPath "\CustomMaintenance\"

    # Remove the task
    Unregister-ScheduledTask -TaskName WeeklySystemMaintenance -TaskPath "\CustomMaintenance\" -Confirm:$false
#>

$TaskName   = 'WeeklySystemMaintenance'
$TaskPath   = '\CustomMaintenance\'
$PwshExe    = 'D:\Program Files\PowerShell\7\pwsh.exe'
$ScriptFile = 'C:\Users\nayah\Scripts\Invoke-MaintenanceScan.ps1'

# Verify PS7 exists at the expected path
if (-not (Test-Path $PwshExe)) {
    $PwshExe = (Get-Command pwsh -ErrorAction SilentlyContinue)?.Source
    if (-not $PwshExe) {
        Write-Error "PowerShell 7 not found. Update the `$PwshExe path in this script."
        exit 1
    }
    Write-Warning "PS7 found at '$PwshExe' — update `$PwshExe in this script for future runs."
}

# Remove existing task (idempotent)
Unregister-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -Confirm:$false -ErrorAction SilentlyContinue

# Action: run PS7 scan script in clean mode (unattended, no GUI disk optimizer)
$action = New-ScheduledTaskAction `
    -Execute   $PwshExe `
    -Argument  "-ExecutionPolicy Bypass -NonInteractive -File `"$ScriptFile`" -Clean -SkipDiskOptimize" `
    -WorkingDirectory 'C:\Users\nayah\Scripts'

# Trigger: every Monday at 09:00, catches up if machine was off
$trigger = New-ScheduledTaskTrigger `
    -Weekly `
    -DaysOfWeek Monday `
    -At '09:00AM'

# Principal: SYSTEM account — no password expiry, works when user is logged off
$principal = New-ScheduledTaskPrincipal `
    -UserId    'SYSTEM' `
    -LogonType ServiceAccount `
    -RunLevel  Highest

# Settings
$settings = New-ScheduledTaskSettingsSet `
    -WakeToRun `
    -ExecutionTimeLimit   (New-TimeSpan -Hours 3) `
    -StartWhenAvailable `
    -MultipleInstances    IgnoreNew `
    -RunOnlyIfNetworkAvailable:$false `
    -RunOnlyIfIdle:$false

$task = Register-ScheduledTask `
    -TaskName   $TaskName `
    -TaskPath   $TaskPath `
    -Action     $action `
    -Trigger    $trigger `
    -Principal  $principal `
    -Settings   $settings `
    -Description 'Weekly disk cleanup and maintenance scan. Reports saved to C:\Users\nayah\MaintenanceReports\'

$info = Get-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath | Get-ScheduledTaskInfo
Write-Host "Task registered:  $($task.TaskPath)$($task.TaskName)" -ForegroundColor Green
Write-Host "Next scheduled run: $($info.NextRunTime)" -ForegroundColor Cyan
Write-Host ""
Write-Host "To run immediately (test):  Start-ScheduledTask -TaskName '$TaskName' -TaskPath '$TaskPath'"
Write-Host "To view last result:        (Get-ScheduledTask -TaskName '$TaskName' -TaskPath '$TaskPath' | Get-ScheduledTaskInfo).LastTaskResult"
Write-Host "Reports folder:             C:\Users\nayah\MaintenanceReports\"
Write-Host ""
Write-Host "NOTE: If you also want the C# MaintenanceAgent to run weekly, set HF_API_KEY" -ForegroundColor Yellow
Write-Host "      as a SYSTEM-level environment variable via:" -ForegroundColor Yellow
Write-Host "      sysdm.cpl > Advanced > Environment Variables > System Variables" -ForegroundColor Yellow
