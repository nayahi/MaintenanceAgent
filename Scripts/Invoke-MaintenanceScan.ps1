#Requires -Version 7.0
<#
.SYNOPSIS
    Weekly system maintenance scanner and cleaner for Windows 11.

.DESCRIPTION
    Scan mode (default): reports sizes of all cleanup targets, no files deleted.
    Clean mode (-Clean): prompts per category, then deletes confirmed items.

.PARAMETER Clean
    Enable deletion. Without this flag the script is fully read-only.

.PARAMETER IncludeConditional
    Also evaluate TechSmith old installers and WSL disk compact.

.PARAMETER ReportDir
    Where to save timestamped report files. Default: C:\Users\nayah\MaintenanceReports

.PARAMETER SkipDiskOptimize
    Skip Optimize-Volume (TRIM/retrim) at the end. Use this for unattended/scheduled runs.

.PARAMETER NoReport
    Do not save a report file.

.EXAMPLE
    # Scan only (safe, no changes)
    pwsh -File "C:\Users\nayah\Scripts\Invoke-MaintenanceScan.ps1"

.EXAMPLE
    # Full interactive clean including conditional items
    pwsh -File "C:\Users\nayah\Scripts\Invoke-MaintenanceScan.ps1" -Clean -IncludeConditional

.EXAMPLE
    # Scheduled/unattended clean (no GUI prompts)
    pwsh -File "C:\Users\nayah\Scripts\Invoke-MaintenanceScan.ps1" -Clean -SkipDiskOptimize

.EXAMPLE
    # Dry run: preview exactly what -Clean would do (per category) without deleting/running anything
    pwsh -File "C:\Users\nayah\Scripts\Invoke-MaintenanceScan.ps1" -Clean -WhatIf
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Clean,
    [switch]$IncludeConditional,
    [string]$ReportDir = 'C:\Users\nayah\MaintenanceReports',
    [switch]$SkipDiskOptimize,
    [switch]$NoReport
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

# ── Admin check (informational only — user-AppData cleanup works without admin) ──
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[INFO] Running without admin rights. ProgramData\WER and WSL compact may be skipped — all AppData targets will clean fine." -ForegroundColor Yellow
}

# ── Report buffer ────────────────────────────────────────────────────────────
$script:ReportLines = [System.Collections.Generic.List[string]]::new()

function Write-Report {
    param([string]$Message, [string]$Level = 'INFO')
    $line = "[$(Get-Date -Format 'HH:mm:ss')] [$Level] $Message"
    $script:ReportLines.Add($line)
    switch ($Level) {
        'WARN'    { Write-Host $line -ForegroundColor Yellow }
        'ERROR'   { Write-Host $line -ForegroundColor Red }
        'OK'      { Write-Host $line -ForegroundColor Green }
        'SECTION' { Write-Host "`n$line" -ForegroundColor Cyan }
        default   { Write-Host $line }
    }
}

# ── Helpers ──────────────────────────────────────────────────────────────────
function Get-FolderSize {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return 0L }
    $files = @(Get-ChildItem $Path -Recurse -Force -File -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { return 0L }
    [long](($files | Measure-Object -Property Length -Sum -ErrorAction SilentlyContinue).Sum ?? 0)
}

function Get-DriveInfo {
    param([string]$Drive = 'C')
    $d = Get-PSDrive $Drive -ErrorAction SilentlyContinue
    if (-not $d) { return $null }
    [PSCustomObject]@{
        FreeGB  = [math]::Round($d.Free  / 1GB, 2)
        UsedGB  = [math]::Round($d.Used  / 1GB, 2)
        TotalGB = [math]::Round(($d.Free + $d.Used) / 1GB, 2)
        FreePct = [math]::Round($d.Free / ($d.Free + $d.Used) * 100, 1)
    }
}

function Remove-SafeFolder {
    # Deletes folder contents, skips locked files, never throws
    param([string]$Path, [string]$Label)
    $freed = 0L
    if (-not (Test-Path $Path)) { return $freed }
    Get-ChildItem $Path -Recurse -Force -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        ForEach-Object {
            try {
                $freed += ($_.Length ?? 0L)
                Remove-Item $_.FullName -Force -Recurse -ErrorAction Stop
            } catch { <# silently skip locked files #> }
        }
    return $freed
}

function Format-MB { param([long]$Bytes) [math]::Round($Bytes / 1MB, 1) }
function Format-GB { param([long]$Bytes) [math]::Round($Bytes / 1GB, 2) }

# ── Category definitions ─────────────────────────────────────────────────────
# Mode values:
#   Folder        — delete contents of each path in Paths[]
#   Command       — run Command scriptblock (npm/dotnet)
#   DynamicAzFunc — detect old Azure Functions version tags at runtime

$SafeCategories = @(
    [ordered]@{
        Label   = 'JetBrains IntelliJ caches / index / log / temp'
        Note    = 'Plugins folder is intentionally excluded (slow to rebuild)'
        Paths   = @(
            "$env:LOCALAPPDATA\JetBrains\IntelliJIdea2026.1\caches",
            "$env:LOCALAPPDATA\JetBrains\IntelliJIdea2026.1\index",
            "$env:LOCALAPPDATA\JetBrains\IntelliJIdea2026.1\log",
            "$env:LOCALAPPDATA\JetBrains\IntelliJIdea2026.1\_temp_",
            "$env:LOCALAPPDATA\JetBrains\IntelliJIdea2026.1\jcef_cache"
        )
        Mode    = 'Folder'
    },
    [ordered]@{
        Label   = 'Chrome for Testing browser cache'
        Note    = 'Close Chrome before cleaning'
        Paths   = @(
            "$env:LOCALAPPDATA\Google\Chrome for Testing\User Data\Default\Cache",
            "$env:LOCALAPPDATA\Google\Chrome for Testing\User Data\Default\Code Cache"
        )
        Mode    = 'Folder'
    },
    [ordered]@{
        Label   = 'Postman cache'
        Note    = 'Close Postman before cleaning'
        Paths   = @(
            "$env:APPDATA\Postman\Cache",
            "$env:APPDATA\Postman\Code Cache"
        )
        Mode    = 'Folder'
    },
    [ordered]@{
        Label   = 'Microsoft Edge browser cache'
        Note    = 'Close Edge before cleaning'
        Paths   = @(
            "$env:LOCALAPPDATA\Microsoft\Edge\User Data\Default\Cache",
            "$env:LOCALAPPDATA\Microsoft\Edge\User Data\Default\Code Cache"
        )
        Mode    = 'Folder'
    },
    [ordered]@{
        Label   = 'npm cache'
        Note    = 'Runs: npm cache clean --force'
        Paths   = @("$env:LOCALAPPDATA\npm-cache")
        Mode    = 'Command'
        Command = {
            $npm = 'D:\Program Files\nodejs\npm.cmd'
            if (-not (Test-Path $npm)) { $npm = (Get-Command npm -ErrorAction SilentlyContinue)?.Source }
            if ($npm) { & $npm cache clean --force 2>&1 | ForEach-Object { Write-Report "  npm: $_" } }
            else { Write-Report "  npm not found, deleting cache folder directly" 'WARN' }
        }
    },
    [ordered]@{
        Label   = 'NuGet HTTP cache + global packages (~/.nuget/packages)'
        Note    = 'Runs: dotnet nuget locals all --clear. Packages auto-restore on next build.'
        Paths   = @(
            "$env:LOCALAPPDATA\NuGet",
            "$env:USERPROFILE\.nuget\packages"
        )
        Mode    = 'Command'
        Command = {
            dotnet nuget locals all --clear 2>&1 | ForEach-Object { Write-Report "  dotnet nuget: $_" }
        }
    },
    [ordered]@{
        Label   = 'Azure Functions Core Tools (old version tags only, keeps latest)'
        Note    = 'Latest version tag is kept; older ones are removed'
        Paths   = @()   # populated dynamically below
        Mode    = 'DynamicAzFunc'
    },
    [ordered]@{
        Label   = 'User Temp folder'
        Note    = 'Locked files are skipped automatically'
        Paths   = @("$env:LOCALAPPDATA\Temp")
        Mode    = 'Folder'
    },
    [ordered]@{
        Label   = 'Windows Error Reporting (WER) archives'
        Paths   = @(
            "$env:ProgramData\Microsoft\Windows\WER\ReportArchive",
            "$env:ProgramData\Microsoft\Windows\WER\ReportQueue",
            "$env:LOCALAPPDATA\Microsoft\Windows\WER\ReportArchive",
            "$env:LOCALAPPDATA\Microsoft\Windows\WER\ReportQueue"
        )
        Mode    = 'Folder'
    },
    [ordered]@{
        Label   = 'Crash dumps (system + Snagit)'
        Paths   = @(
            "$env:LOCALAPPDATA\CrashDumps",
            "$env:LOCALAPPDATA\TechSmith\Snagit\CrashDumps"
        )
        Mode    = 'Folder'
    },
    [ordered]@{
        Label   = 'Docker unused data (build cache, dangling images/containers)'
        Note    = 'Safe subset only: stopped containers, dangling images/networks, unused build cache. Volumes and in-use tagged images are never touched. Size not measured in scan mode -- run "docker system df" to preview. Skipped automatically if Docker is not installed or not running.'
        Paths   = @()
        Mode    = 'Command'
        Command = {
            $docker = (Get-Command docker -ErrorAction SilentlyContinue)?.Source
            if (-not $docker) { Write-Report "  docker CLI not found, skipping" 'WARN'; return }
            & $docker info *> $null
            if ($LASTEXITCODE -ne 0) { Write-Report "  Docker daemon not running, skipping" 'WARN'; return }
            & $docker system prune -f 2>&1  | ForEach-Object { Write-Report "  docker: $_" }
            & $docker builder prune -f 2>&1 | ForEach-Object { Write-Report "  docker: $_" }
        }
    },
    [ordered]@{
        Label   = 'Windows Update component store (WinSxS superseded versions)'
        Note    = 'Runs DISM /StartComponentCleanup -- conservative, keeps rollback ability for recent updates. Requires admin (skipped otherwise). Size not measured in scan mode: the WinSxS folder size overstates real reclaimable space due to hard links. Run "DISM /Online /Cleanup-Image /AnalyzeComponentStore" for an accurate estimate.'
        Paths   = @()
        Mode    = 'Command'
        Command = {
            if (-not $isAdmin) { Write-Report "  Requires admin, skipping" 'WARN'; return }
            Dism.exe /Online /Cleanup-Image /StartComponentCleanup /Quiet /NoRestart 2>&1 |
                ForEach-Object { Write-Report "  dism: $_" }
        }
    },
    [ordered]@{
        Label   = 'Recycle Bin'
        Note    = 'Permanently empties the Recycle Bin (Clear-RecycleBin) -- files are not recoverable after this.'
        Paths   = @("$env:SystemDrive\`$Recycle.Bin")
        Mode    = 'Command'
        Command = {
            Clear-RecycleBin -Force -ErrorAction SilentlyContinue
        }
    },
    [ordered]@{
        Label   = 'Windows Update download cache'
        Note    = 'Stops the Windows Update service, clears cached update files, restarts the service. Windows re-downloads as needed. Requires admin (skipped otherwise).'
        Paths   = @("$env:windir\SoftwareDistribution\Download")
        Mode    = 'Command'
        Command = {
            if (-not $isAdmin) { Write-Report "  Requires admin, skipping" 'WARN'; return }
            Stop-Service wuauserv -Force -ErrorAction SilentlyContinue
            Get-ChildItem "$env:windir\SoftwareDistribution\Download" -Force -ErrorAction SilentlyContinue |
                Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
            Start-Service wuauserv -ErrorAction SilentlyContinue
        }
    },
    [ordered]@{
        Label   = 'Explorer thumbnail/icon cache'
        Note    = 'Rebuilds automatically; thumbnails regenerate as you browse folders.'
        Paths   = @("$env:LOCALAPPDATA\Microsoft\Windows\Explorer")
        Mode    = 'Folder'
    },
    [ordered]@{
        Label   = 'pip cache'
        Note    = 'Runs: pip cache purge'
        Paths   = @("$env:LOCALAPPDATA\pip\Cache")
        Mode    = 'Command'
        Command = {
            $pip = (Get-Command pip -ErrorAction SilentlyContinue)?.Source
            if ($pip) { & $pip cache purge 2>&1 | ForEach-Object { Write-Report "  pip: $_" } }
            else { Write-Report "  pip not found, deleting cache folder directly" 'WARN' }
        }
    },
    [ordered]@{
        Label   = 'Yarn cache'
        Note    = 'Runs: yarn cache clean'
        Paths   = @(
            "$env:LOCALAPPDATA\Yarn\Cache",
            "$env:LOCALAPPDATA\Yarn\Berry\cache"
        )
        Mode    = 'Command'
        Command = {
            $yarn = (Get-Command yarn -ErrorAction SilentlyContinue)?.Source
            if ($yarn) { & $yarn cache clean 2>&1 | ForEach-Object { Write-Report "  yarn: $_" } }
            else { Write-Report "  yarn not found, deleting cache folder directly" 'WARN' }
        }
    },
    [ordered]@{
        Label   = 'VS Code cache'
        Note    = 'Close VS Code before cleaning'
        Paths   = @(
            "$env:APPDATA\Code\Cache",
            "$env:APPDATA\Code\CachedData",
            "$env:APPDATA\Code\Code Cache",
            "$env:APPDATA\Code\GPUCache"
        )
        Mode    = 'Folder'
    }
)

$ConditionalCategories = @(
    [ordered]@{
        Label   = 'TechSmith Updater old installer packages'
        Warning = 'These are cached installer packages for old Snagit/Camtasia versions. Snagit stays installed. Re-download needed if you reinstall that specific version.'
        Paths   = @("$env:LOCALAPPDATA\TechSmith\Updater\Installers")
        Mode    = 'Folder'
    },
    [ordered]@{
        Label   = 'WSL Ubuntu virtual disk compact (reclaims ~4 GB)'
        Warning = 'Will run: wsl --manage Ubuntu --set-sparse true. WSL must be Stopped. Briefly unavailable during compact.'
        Paths   = @()
        Mode    = 'Command'
        Command = {
            $state = wsl --list --verbose 2>&1 | Select-String 'Ubuntu'
            if ($state -match 'Stopped') {
                Write-Report "  WSL Ubuntu is Stopped — compacting..." 'INFO'
                wsl --manage Ubuntu --set-sparse true 2>&1 | ForEach-Object { Write-Report "  wsl: $_" }
            } else {
                Write-Report "  WSL Ubuntu is not Stopped (state: $state) — skipping compact" 'WARN'
            }
        }
    },
    [ordered]@{
        Label   = 'Windows.old (previous Windows installation)'
        Warning = 'Removes your ability to roll back to the previous Windows version. Windows normally auto-deletes this ~10 days after upgrading anyway.'
        Paths   = @("$env:SystemDrive\Windows.old")
        Mode    = 'Folder'
    },
    [ordered]@{
        Label   = 'Windows component store aggressive cleanup (DISM /ResetBase)'
        Warning = 'Permanently removes the ability to uninstall any currently-installed updates -- no rollback. Only run this if the system has been stable for a while. Requires admin.'
        Paths   = @()
        Mode    = 'Command'
        Command = {
            if (-not $isAdmin) { Write-Report "  Requires admin, skipping" 'WARN'; return }
            Dism.exe /Online /Cleanup-Image /StartComponentCleanup /ResetBase /Quiet /NoRestart 2>&1 |
                ForEach-Object { Write-Report "  dism: $_" }
        }
    },
    [ordered]@{
        Label   = 'Docker aggressive prune (all unused images + volumes)'
        Warning = 'Removes ALL unused images (even tagged ones like postgres:16 you might reuse) AND unused volumes, which may hold database/app data you still need. Re-pull/re-create needed afterward.'
        Paths   = @()
        Mode    = 'Command'
        Command = {
            $docker = (Get-Command docker -ErrorAction SilentlyContinue)?.Source
            if (-not $docker) { Write-Report "  docker CLI not found, skipping" 'WARN'; return }
            & $docker system prune -a -f --volumes 2>&1 | ForEach-Object { Write-Report "  docker: $_" }
        }
    }
)

# ── Resolve DynamicAzFunc paths at runtime ───────────────────────────────────
$azTagsRoot = "$env:LOCALAPPDATA\AzureFunctionsTools\Tags"
if (Test-Path $azTagsRoot) {
    $azTags = Get-ChildItem $azTagsRoot -Directory -ErrorAction SilentlyContinue |
              Sort-Object Name -Descending
    if ($azTags.Count -gt 1) {
        $oldTags = $azTags | Select-Object -Skip 1
        $azCat   = $SafeCategories | Where-Object { $_.Mode -eq 'DynamicAzFunc' }
        foreach ($t in $oldTags) { $azCat.Paths += $t.FullName }
        $azCat.Mode = 'Folder'
        Write-Report "Azure Functions: keeping '$($azTags[0].Name)', marking $($oldTags.Count) older tag(s) for removal" 'INFO'
    } else {
        # Only one version — nothing to remove
        $azCat = $SafeCategories | Where-Object { $_.Mode -eq 'DynamicAzFunc' }
        $azCat.Mode = 'Folder'
    }
}

# ── Resolve real DISM component-store analysis (replaces the static placeholder note) ─
# DISM doesn't report a clean reclaimable-MB figure (WinSxS hard links make folder size
# unreliable), so we surface its own package-count/recommendation instead of a size.
function Get-ComponentStoreAnalysis {
    if (-not $isAdmin) { return $null }
    try {
        $output      = Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore 2>&1
        $reclaimable = ($output | Select-String 'Number of Reclaimable Packages\s*:\s*(\d+)').Matches.Groups[1].Value
        $recommended = ($output | Select-String 'Component Store Cleanup Recommended\s*:\s*(\w+)').Matches.Groups[1].Value
        if ($reclaimable -and $recommended) {
            return "DISM /StartComponentCleanup -- conservative, keeps rollback ability for recent updates. " +
                   "Live analysis: $reclaimable reclaimable package(s), cleanup recommended: $recommended. " +
                   "(DISM doesn't report an exact MB figure -- these are packages superseding what's kept for update rollback.)"
        }
    } catch { <# fall back to the static note below #> }
    return $null
}

$dismCat = $SafeCategories | Where-Object { $_.Label -eq 'Windows Update component store (WinSxS superseded versions)' }
$dismLiveNote = Get-ComponentStoreAnalysis
if ($dismLiveNote) { $dismCat.Note = $dismLiveNote }

# ══════════════════════════════════════════════════════════════════════════════
# SCAN PHASE
# ══════════════════════════════════════════════════════════════════════════════
Write-Report "══ MAINTENANCE SCAN ══ $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" 'SECTION'
$before = Get-DriveInfo 'C'
Write-Report "C: Drive  →  $($before.FreeGB) GB free of $($before.TotalGB) GB  ($($before.FreePct)% free)" 'INFO'
Write-Report "" 'INFO'

$totalReclaimable = 0L

function Measure-Category {
    param($cat)
    $bytes = 0L
    foreach ($p in $cat.Paths) { $bytes += Get-FolderSize $p }
    $cat['ScannedBytes'] = $bytes
    return $bytes
}

Write-Report "── SAFE TO CLEAN ──" 'SECTION'
foreach ($cat in $SafeCategories) {
    $bytes = Measure-Category $cat
    $totalReclaimable += $bytes
    $mb = Format-MB $bytes
    Write-Report "  [SAFE]  $($cat.Label)  →  $mb MB" 'INFO'
    if ($cat.Contains('Note') -and $cat.Note) { Write-Report "          NOTE: $($cat.Note)" 'INFO' }
}

if ($IncludeConditional) {
    Write-Report "── CONDITIONAL (review before cleaning) ──" 'SECTION'
    foreach ($cat in $ConditionalCategories) {
        $bytes = Measure-Category $cat
        $totalReclaimable += $bytes
        $mb = Format-MB $bytes
        Write-Report "  [COND]  $($cat.Label)  →  $mb MB" 'WARN'
        if ($cat.Contains('Warning') -and $cat.Warning) { Write-Report "          WARNING: $($cat.Warning)" 'WARN' }
    }
}

$totalMB = Format-MB $totalReclaimable
$totalGB = Format-GB $totalReclaimable
Write-Report "" 'INFO'
Write-Report "TOTAL RECLAIMABLE: $totalMB MB  ($totalGB GB)" 'OK'

# ══════════════════════════════════════════════════════════════════════════════
# CLEAN PHASE
# ══════════════════════════════════════════════════════════════════════════════
if ($Clean) {
    Write-Report "══ CLEAN MODE ACTIVE ══" 'SECTION'
    $allCategories = $SafeCategories
    if ($IncludeConditional) { $allCategories = $allCategories + $ConditionalCategories }

    foreach ($cat in $allCategories) {
        $mb = Format-MB ($cat['ScannedBytes'] ?? 0L)

        if ($cat.Contains('Warning') -and $cat.Warning) {
            Write-Host "`n  WARNING: $($cat.Warning)" -ForegroundColor Yellow
        }

        # -WhatIf (requires -Clean too) previews every category -- Folder deletions and
        # Command invocations alike -- without touching anything, including external
        # tools (docker/dism/etc.) that don't understand PowerShell's own -WhatIf.
        if (-not $PSCmdlet.ShouldProcess($cat.Label, 'Clean')) { continue }

        Write-Report "  CLEANING: $($cat.Label)  (~$mb MB)" 'INFO'
        $freed = 0L
        switch ($cat.Mode) {
            'Folder' {
                foreach ($p in $cat.Paths) {
                    if (Test-Path $p) {
                        $freed += Remove-SafeFolder $p $cat.Label
                    }
                }
                Write-Report "  CLEANED: $($cat.Label)  →  $(Format-MB $freed) MB freed" 'OK'
            }
            'Command' {
                & $cat.Command
                $after = 0L
                foreach ($p in $cat.Paths) { $after += Get-FolderSize $p }
                $freed = ($cat['ScannedBytes'] ?? 0L) - $after
                Write-Report "  CLEANED: $($cat.Label)  →  $(Format-MB $freed) MB freed" 'OK'
            }
        }
        $cat['FreedBytes'] = $freed
    }

    # ── Disk optimization (SSD TRIM) ──────────────────────────────────────────
    if (-not $SkipDiskOptimize -and $PSCmdlet.ShouldProcess('C: Volume', 'Optimize-Volume (TRIM/retrim)')) {
        Write-Report "── DISK OPTIMIZATION ──" 'SECTION'
        Write-Report "Running Optimize-Volume (TRIM/retrim on SSD)..." 'INFO'
        try {
            Optimize-Volume -DriveLetter C -ReTrim -ErrorAction Stop
            Write-Report "Optimize-Volume completed." 'OK'
        } catch {
            Write-Report "Optimize-Volume failed (may need admin or not an SSD): $_" 'WARN'
        }
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# FINAL SUMMARY
# ══════════════════════════════════════════════════════════════════════════════
Write-Report "── SUMMARY ──" 'SECTION'
$after = Get-DriveInfo 'C'
$freedGB = [math]::Round($after.FreeGB - $before.FreeGB, 2)
Write-Report "C: Drive  →  $($after.FreeGB) GB free of $($after.TotalGB) GB  ($($after.FreePct)% free)" 'OK'
if ($Clean) {
    Write-Report "Net space recovered this run: $freedGB GB" 'OK'
} else {
    Write-Report "Scan complete. Re-run with -Clean to delete. Estimated savings: $totalGB GB" 'OK'
}

# ── Structured run summary (for the C# agent's history/insights tracking) ────
function Get-CategorySummary {
    param($Categories, [string]$Type)
    $Categories | ForEach-Object {
        [ordered]@{
            Label     = $_.Label
            Type      = $Type
            ScannedMB = Format-MB ($_['ScannedBytes'] ?? 0L)
            FreedMB   = if ($_.Contains('FreedBytes')) { Format-MB $_['FreedBytes'] } else { $null }
        }
    }
}

$categorySummary = @(Get-CategorySummary $SafeCategories 'Safe')
if ($IncludeConditional) { $categorySummary += @(Get-CategorySummary $ConditionalCategories 'Conditional') }

$runSummary = [ordered]@{
    Timestamp          = (Get-Date -Format 'o')
    CleanMode          = [bool]$Clean
    DriveFreeGBBefore  = $before.FreeGB
    DriveFreeGBAfter   = $after.FreeGB
    TotalReclaimableMB = $totalMB
    Categories         = $categorySummary
}
# Sentinel line for C# agent to parse (single-line compact JSON)
Write-Output "RUN_SUMMARY_JSON:$($runSummary | ConvertTo-Json -Compress -Depth 5)"

# ── Save report ───────────────────────────────────────────────────────────────
if (-not $NoReport) {
    $timestamp  = Get-Date -Format 'yyyyMMdd_HHmmss'
    $mode       = if ($Clean) { 'Clean' } else { 'Scan' }
    $reportPath = Join-Path $ReportDir "Maintenance${mode}_${timestamp}.txt"
    New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
    $script:ReportLines | Set-Content $reportPath -Encoding UTF8
    Write-Host "`nReport saved: $reportPath" -ForegroundColor Cyan
    # Sentinel line for C# agent to parse
    Write-Output "REPORT_FILE:$reportPath"
}
