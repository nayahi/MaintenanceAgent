#Requires -Version 7.0
<#
.SYNOPSIS
    Quick Windows health commands — run individual sections or paste the dashboard block.

.DESCRIPTION
    This file is a REFERENCE. Dot-source it or copy individual sections into your terminal.
    Most commands are read-only and safe without admin rights.
    Commands marked [ADMIN] need an elevated terminal.
#>

# ══════════════════════════════════════════════════════════════════════════════
# ONE-SHOT DASHBOARD  (paste this whole block for a 5-second health check)
# ══════════════════════════════════════════════════════════════════════════════
function Show-HealthDashboard {
    $disk   = Get-PSDrive C | Select-Object `
        @{N='FreeGB';  E={[math]::Round($_.Free  / 1GB, 2)}},
        @{N='UsedPct'; E={[math]::Round($_.Used  / ($_.Used + $_.Free) * 100, 1)}}
    $os     = Get-CimInstance Win32_OperatingSystem
    $memPct = [math]::Round($os.FreePhysicalMemory / $os.TotalVisibleMemorySize * 100, 1)
    $cpu    = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
    $uptime = (Get-Date) - $os.LastBootUpTime
    $errors = (Get-WinEvent -FilterHashtable @{
        LogName   = 'System', 'Application'
        Level     = 2
        StartTime = (Get-Date).AddHours(-24)
    } -ErrorAction SilentlyContinue).Count

    Write-Host "═══ QUICK HEALTH  $(Get-Date -Format 'yyyy-MM-dd HH:mm') ═══" -ForegroundColor Cyan
    $diskColor = if ($disk.UsedPct -gt 90) { 'Red' } elseif ($disk.UsedPct -gt 80) { 'Yellow' } else { 'Green' }
    Write-Host "  Disk C:   $($disk.FreeGB) GB free  ($($disk.UsedPct)% used)" -ForegroundColor $diskColor
    Write-Host "  Memory:   $memPct% free"
    Write-Host "  CPU Load: $cpu%"
    Write-Host "  Uptime:   $([int]$uptime.TotalDays)d $($uptime.Hours)h $($uptime.Minutes)m"
    $errColor = if ($errors -gt 20) { 'Red' } elseif ($errors -gt 5) { 'Yellow' } else { 'Green' }
    Write-Host "  Errors (24h): $errors event-log errors" -ForegroundColor $errColor
    Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
}

# Run it immediately when dot-sourced:
# . "C:\Users\nayah\Scripts\QuickHealthCommands.ps1"; Show-HealthDashboard

# ══════════════════════════════════════════════════════════════════════════════
# DISK SPACE
# ══════════════════════════════════════════════════════════════════════════════
function Get-DiskSpace {
    Get-PSDrive -PSProvider FileSystem | Select-Object Name,
        @{N='FreeGB';  E={[math]::Round($_.Free  / 1GB, 2)}},
        @{N='UsedGB';  E={[math]::Round($_.Used  / 1GB, 2)}},
        @{N='TotalGB'; E={[math]::Round(($_.Free + $_.Used) / 1GB, 2)}},
        @{N='FreePct'; E={[math]::Round($_.Free / ($_.Free + $_.Used) * 100, 1)}} |
    Format-Table -AutoSize
}

# Top 20 largest files on C: — [ADMIN] for full access, takes ~60s
function Get-LargestFiles {
    param([int]$Top = 20, [string]$Root = 'C:\')
    Get-ChildItem $Root -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { -not $_.PSIsContainer } |
        Sort-Object Length -Descending |
        Select-Object -First $Top FullName, @{N='SizeMB'; E={[math]::Round($_.Length / 1MB, 1)}} |
        Format-Table -AutoSize
}

# Top 10 largest folders under a directory
function Get-LargestFolders {
    param([string]$Root = "$env:LOCALAPPDATA", [int]$Top = 10)
    Get-ChildItem $Root -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $size = (Get-ChildItem $_.FullName -Recurse -Force -ErrorAction SilentlyContinue |
                     Measure-Object Length -Sum).Sum ?? 0
            [PSCustomObject]@{ Folder = $_.Name; SizeGB = [math]::Round($size / 1GB, 2) }
        } |
        Sort-Object SizeGB -Descending |
        Select-Object -First $Top |
        Format-Table -AutoSize
}

# ══════════════════════════════════════════════════════════════════════════════
# EVENT LOG
# ══════════════════════════════════════════════════════════════════════════════
function Get-RecentErrors {
    param([int]$Hours = 24, [int]$Top = 30)
    Get-WinEvent -FilterHashtable @{
        LogName   = 'System', 'Application'
        Level     = 2   # Error
        StartTime = (Get-Date).AddHours(-$Hours)
    } -ErrorAction SilentlyContinue |
        Select-Object TimeCreated, ProviderName, Id,
            @{N='Message'; E={$_.Message -replace '\s+', ' ' | ForEach-Object { $_[..200] }}} |
        Select-Object -First $Top |
        Format-Table -AutoSize -Wrap
}

function Get-RecentCritical {
    param([int]$Days = 7)
    Get-WinEvent -FilterHashtable @{
        LogName   = 'System'
        Level     = 1   # Critical
        StartTime = (Get-Date).AddDays(-$Days)
    } -ErrorAction SilentlyContinue |
        Select-Object TimeCreated, ProviderName, Message |
        Format-Table -AutoSize -Wrap
}

# ══════════════════════════════════════════════════════════════════════════════
# STARTUP & SERVICES
# ══════════════════════════════════════════════════════════════════════════════
function Get-StartupPrograms {
    Get-CimInstance Win32_StartupCommand |
        Select-Object Name, Command, Location, User |
        Format-Table -AutoSize -Wrap
}

function Get-StoppedAutoServices {
    # Auto-start services that are currently stopped (may indicate issues)
    Get-Service |
        Where-Object { $_.StartType -eq 'Automatic' -and $_.Status -ne 'Running' } |
        Select-Object Name, DisplayName, Status |
        Format-Table -AutoSize
}

function Get-ThirdPartyServices {
    # Running services with paths outside System32/Windows (non-Microsoft)
    Get-CimInstance Win32_Service |
        Where-Object { $_.State -eq 'Running' -and
                       $_.PathName -notmatch 'System32|SysWOW64|\\Windows\\' } |
        Select-Object Name, DisplayName,
            @{N='Path'; E={$_.PathName -replace '"', ''}} |
        Format-Table -AutoSize -Wrap
}

# ══════════════════════════════════════════════════════════════════════════════
# MEMORY & PROCESSES
# ══════════════════════════════════════════════════════════════════════════════
function Get-MemorySummary {
    $os = Get-CimInstance Win32_OperatingSystem
    [PSCustomObject]@{
        TotalGB  = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
        FreeGB   = [math]::Round($os.FreePhysicalMemory     / 1MB, 1)
        UsedGB   = [math]::Round(($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / 1MB, 1)
        UsedPct  = [math]::Round(($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize * 100, 1)
    }
}

function Get-TopProcesses {
    param([int]$Top = 15)
    Get-Process |
        Sort-Object WorkingSet64 -Descending |
        Select-Object -First $Top Name, Id,
            @{N='MemMB';  E={[math]::Round($_.WorkingSet64 / 1MB, 1)}},
            @{N='CPU_sec'; E={[math]::Round($_.TotalProcessorTime.TotalSeconds, 1)}} |
        Format-Table -AutoSize
}

# ══════════════════════════════════════════════════════════════════════════════
# WINDOWS UPDATE
# ══════════════════════════════════════════════════════════════════════════════
function Get-RecentUpdates {
    param([int]$Top = 10)
    Get-WmiObject -Class Win32_QuickFixEngineering |
        Sort-Object InstalledOn -Descending |
        Select-Object -First $Top HotFixID, Description, InstalledOn |
        Format-Table -AutoSize
}

function Test-PendingReboot {
    $checks = [ordered]@{
        WindowsUpdate = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
        CBS           = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
        FileRename    = $null -ne (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' `
                            -Name PendingFileRenameOperations -ErrorAction SilentlyContinue)?.PendingFileRenameOperations
    }
    $reboot = $checks.Values -contains $true
    Write-Host "Pending reboot: $(if ($reboot) { 'YES' } else { 'No' })" -ForegroundColor $(if ($reboot) { 'Yellow' } else { 'Green' })
    $checks | Format-Table -AutoSize
}

# ══════════════════════════════════════════════════════════════════════════════
# NETWORK (bonus)
# ══════════════════════════════════════════════════════════════════════════════
function Get-ActiveConnections {
    # Active TCP connections — useful for spotting unexpected outbound traffic
    Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
        Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort,
            @{N='Process'; E={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue)?.Name}} |
        Sort-Object RemoteAddress |
        Format-Table -AutoSize
}

Write-Host "QuickHealthCommands loaded. Available functions:" -ForegroundColor Cyan
@(
    'Show-HealthDashboard   — 5-second health snapshot'
    'Get-DiskSpace          — all drives, free/used'
    'Get-LargestFiles       — top N largest files on C: (slow, needs admin)'
    'Get-LargestFolders     — top N largest folders under a path'
    'Get-RecentErrors       — event log errors (last 24h by default)'
    'Get-RecentCritical     — critical events (last 7 days)'
    'Get-StartupPrograms    — all startup entries'
    'Get-StoppedAutoServices— auto-start services that are stopped'
    'Get-ThirdPartyServices — non-Microsoft running services'
    'Get-MemorySummary      — RAM total/free/used'
    'Get-TopProcesses       — top N memory-using processes'
    'Get-RecentUpdates      — last N Windows updates installed'
    'Test-PendingReboot     — check if a reboot is pending'
    'Get-ActiveConnections  — active TCP connections by process'
) | ForEach-Object { Write-Host "  $_" }
