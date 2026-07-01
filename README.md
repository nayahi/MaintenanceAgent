# MaintenanceAgent

A .NET 8 console app that orchestrates a weekly Windows maintenance routine:

1. Runs a PowerShell 7 disk-cleanup scan script.
2. Sends the scan output to a Hugging Face model for AI-generated maintenance advice.
3. Saves a combined Markdown report.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [PowerShell 7](https://github.com/PowerShell/PowerShell/releases) (`pwsh.exe`)
- A free [Hugging Face](https://huggingface.co/settings/tokens) API token
- The companion PowerShell script `Invoke-MaintenanceScan.ps1` (see [PowerShell script setup](#powershell-script-setup) below)

## Setup

### 1. Configure environment variables

```powershell
# Required
$env:HF_API_KEY = 'hf_YOUR_TOKEN_HERE'

# Optional — overrides the default model (Qwen/Qwen2.5-7B-Instruct)
$env:HF_MODEL = 'Qwen/Qwen2.5-7B-Instruct'
```

To persist these across sessions, set them as user or system environment variables via `sysdm.cpl` > Advanced > Environment Variables.

### 2. PowerShell script setup

By default, `PowerShellRunner` expects:

- `pwsh.exe` at `D:\Program Files\PowerShell\7\pwsh.exe` (falls back to the PATH-resolved `pwsh` if not found there)
- The scan script at `C:\Users\nayah\Scripts\Invoke-MaintenanceScan.ps1`

If your paths differ, update the constructor defaults in `MaintenanceAgent/Services/PowerShellRunner.cs`.

The scan script supports these switches:

| Switch | Effect |
|---|---|
| *(none)* | Scan only — reports reclaimable space, no files deleted |
| `-Clean` | Deletes files in confirmed cleanup categories |
| `-IncludeConditional` | Also evaluates riskier categories (old installers, WSL disk compact) |
| `-SkipDiskOptimize` | Skips the SSD TRIM/retrim step (used for unattended runs) |
| `-NoReport` | Don't save a `.txt` report file |

### 3. Reports directory

Both the PowerShell script's own report and the app's combined Markdown report are saved to `C:\Users\nayah\MaintenanceReports\`. Update the `ReportDir` const in `Program.cs` if you want a different location.

### 4. (Optional) Schedule a weekly run

A companion script, `ScheduleMaintenanceTask.ps1`, registers a Windows Scheduled Task that runs the PS7 scan script directly (in clean mode) every Monday at 09:00 as SYSTEM. Run it once, as Administrator:

```powershell
pwsh -ExecutionPolicy Bypass -File "C:\Users\nayah\Scripts\ScheduleMaintenanceTask.ps1"
```

This schedules the raw disk cleanup only. The C# MaintenanceAgent (AI summarization layer) is not itself scheduled — run it manually or wire up your own scheduled task pointing at `dotnet run` if you want the AI report generated automatically too. If you do, set `HF_API_KEY` as a **system-level** environment variable so it's visible to the SYSTEM account.

## Usage

```powershell
# Build
dotnet build

# Scan only (read-only, default)
dotnet run --project MaintenanceAgent

# Clean mode (deletes cache/temp files found by the scan)
dotnet run --project MaintenanceAgent -- --clean
```

Output includes the raw scan results, AI-generated recommendations, and the path to the saved Markdown report.
