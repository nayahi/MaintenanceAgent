# MaintenanceAgent

A .NET 8 console app that orchestrates a weekly Windows maintenance routine:

1. Runs a PowerShell 7 disk-cleanup scan script.
2. Sends the scan output to a Hugging Face model for AI-generated maintenance advice.
3. Saves a combined Markdown report.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [PowerShell 7](https://github.com/PowerShell/PowerShell/releases) (`pwsh.exe`)
- A [Hugging Face](https://huggingface.co/settings/tokens) API token with **"Inference Providers"** permission (generate a fine-grained token and check that scope — older read-only tokens may not have it)
- The companion PowerShell script `Invoke-MaintenanceScan.ps1` (see [PowerShell script setup](#powershell-script-setup) below)

## Setup

### 1. Configure environment variables

```powershell
# Required
$env:HF_API_KEY = 'hf_YOUR_TOKEN_HERE'

# Optional — overrides the default model (openai/gpt-oss-120b)
# Must be a model ID available on Hugging Face's Inference Providers router:
# https://huggingface.co/docs/inference-providers
$env:HF_MODEL = 'openai/gpt-oss-120b'
```

These only last for the current session. To persist `HF_API_KEY` across sessions (for your user account), run:

```powershell
[System.Environment]::SetEnvironmentVariable('HF_API_KEY', 'hf_YOUR_TOKEN_HERE', 'User')
```

Alternatively, set it as a user or system environment variable via `sysdm.cpl` > Advanced > Environment Variables. Use the `Machine` scope (or the SYSTEM-level GUI setting) instead of `User` if you're running the agent from a scheduled task under the SYSTEM account (see [Schedule a weekly run](#4-optional-schedule-a-weekly-run)).

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

## Troubleshooting

**`Hugging Face API error: The requested name is valid, but no data of the requested type was found.`**
This is a DNS-level failure, not an HF API error — it means the app is trying to reach a hostname that no longer resolves. Make sure you're on a build that posts to `https://router.huggingface.co/v1/chat/completions` (HF retired the old per-model `api-inference.huggingface.co/models/{model}/...` endpoint).

**401/403 from the HF API**
Your token likely lacks the "Inference Providers" permission. Generate a new fine-grained token at [huggingface.co/settings/tokens](https://huggingface.co/settings/tokens) with that scope explicitly checked.

**400 `model_not_supported`: "not supported by any provider you have enabled"**
The model exists but none of the providers hosting it are enabled on your HF account. Check which providers actually serve a model with:
```
curl "https://huggingface.co/api/models/<org>/<model>?expand[]=inferenceProviderMapping"
```
Models hosted by only one niche provider (e.g. `featherless-ai`) are the most likely to fail this way. Prefer models with broad coverage — `openai/gpt-oss-120b` (the default) is live on ~10 providers (Groq, Together, Cerebras, Novita, Fireworks, DeepInfra, etc.), so it's very likely at least one is enabled. You can also review/enable providers directly at [huggingface.co/settings/inference-providers](https://huggingface.co/settings/inference-providers).

**404 or "model not found" from the HF API**
The model in `HF_MODEL` (or the default) isn't available on the Inference Providers router at all. Check [supported models](https://huggingface.co/docs/inference-providers) and switch to one that's listed.
