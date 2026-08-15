# MaintenanceAgent

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)

A .NET 8 console app that orchestrates a weekly Windows maintenance routine:

1. Runs a PowerShell 7 disk-cleanup scan script.
2. Sends the scan output — plus a summary of what's worked in past runs — to a Hugging Face model for AI-generated maintenance advice.
3. Records this run's results to a local history file so future runs can compare recommendations against actual outcomes.
4. Saves a combined Markdown report.

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

# Optional — overrides the preferred model (zai-org/GLM-5.2)
# Must be a model ID available on Hugging Face's Inference Providers router:
# https://huggingface.co/docs/inference-providers
$env:HF_MODEL = 'zai-org/GLM-5.2'

# Optional — paths. Defaults shown; set these only if your layout differs.
$env:MAINTENANCE_REPORT_DIR  = "$env:USERPROFILE\MaintenanceReports"
$env:MAINTENANCE_SCRIPT_PATH = "$env:USERPROFILE\Scripts\Invoke-MaintenanceScan.ps1"
$env:MAINTENANCE_PWSH_PATH   = 'C:\Program Files\PowerShell\7\pwsh.exe'  # else PATH-resolved `pwsh`
```

> **There is no hard-coded fallback token.** If `HF_API_KEY` is unset the app exits with
> a clear error rather than running. Never commit a key to this repository — a committed
> key is a leaked key, even in a private repo.

If the preferred model comes back `model_not_supported`, the app automatically retries the next model in `HuggingFaceClient.FallbackModels` (ordered by how many providers serve it) instead of failing — see [Troubleshooting](#troubleshooting) for the full list and how to check coverage for other models.

These only last for the current session. To persist `HF_API_KEY` across sessions (for your user account), run:

```powershell
[System.Environment]::SetEnvironmentVariable('HF_API_KEY', 'hf_YOUR_TOKEN_HERE', 'User')
```

Alternatively, set it as a user or system environment variable via `sysdm.cpl` > Advanced > Environment Variables. Use the `Machine` scope (or the SYSTEM-level GUI setting) instead of `User` if you're running the agent from a scheduled task under the SYSTEM account (see [Schedule a weekly run](#4-optional-schedule-a-weekly-run)).

### 2. PowerShell script setup

By default, `PowerShellRunner` resolves:

- `pwsh.exe` from `MAINTENANCE_PWSH_PATH`, falling back to the PATH-resolved `pwsh`
- The scan script from `MAINTENANCE_SCRIPT_PATH`, falling back to `%USERPROFILE%\Scripts\Invoke-MaintenanceScan.ps1`

Set those environment variables if your layout differs — no source edit needed. A copy of the
script also ships in this repository under `Scripts/`.

The scan script supports these switches:

| Switch | Effect |
|---|---|
| *(none)* | Scan only — reports reclaimable space, no files deleted |
| `-Clean` | Deletes files / runs cleanup commands in confirmed categories |
| `-IncludeConditional` | Also evaluates riskier categories (old installers, WSL disk compact, `Windows.old`, DISM `/ResetBase`, aggressive Docker prune) |
| `-SkipDiskOptimize` | Skips the SSD TRIM/retrim step (used for unattended runs) |
| `-NoReport` | Don't save a `.txt` report file |
| `-WhatIf` (with `-Clean`) | Dry run — prints what each category would do without deleting/running anything, including external tools like `docker`/`Dism.exe` |

**Testing `-Clean` safely**: run `-Clean -WhatIf` first (or via `dotnet run --project MaintenanceAgent -- --clean` isn't wired to pass `-WhatIf` through — test the PS7 script directly for a dry run, e.g. `pwsh -File "C:\Users\nayah\Scripts\Invoke-MaintenanceScan.ps1" -Clean -WhatIf`). It's a real `ShouldProcess` gate around every category (not just PowerShell's native cmdlets), so nothing is touched.

**Testing `--clean` from Visual Studio**: set the command-line argument via Project Properties → Debug → "Command line arguments" = `--clean`, or add `"commandLineArgs": "--clean"` to the relevant profile in `MaintenanceAgent/Properties/launchSettings.json`. Note the C# app's `--clean` flag maps straight to `-Clean -SkipDiskOptimize` with no `-WhatIf` passthrough today, so this really deletes — use the PS7 script directly with `-WhatIf` first if you want a dry run before doing that.

**Admin-gated categories** (DISM, Windows Update cache) need the *whole process* elevated, not just the script — a non-elevated app can't silently elevate a child process without breaking the stdout capture this app relies on. To test them: either run Visual Studio itself as Administrator (right-click its icon → "Run as administrator" → reopen the solution → F5, so the debuggee and the `pwsh.exe` child it spawns both inherit the elevated token), or run `dotnet run --project MaintenanceAgent -- --clean` from an Administrator terminal. There's no need to make the app always require elevation (that would prompt UAC even for plain scans) — elevate only when you specifically want to test those categories.

**Verified (2026-07-03, elevated session)**: `$isAdmin`'s check (`IsInRole(Administrator)`) correctly returns `true` under an elevated process, and `Dism.exe` itself is functional in that context — a read-only `Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore` run reported real data (2 reclaimable packages, "Component Store Cleanup Recommended: Yes"). One gotcha found along the way: testing the admin check with `-Clean -WhatIf` isn't conclusive by itself — `ShouldProcess` short-circuits *before* a category's `Command` scriptblock (and its internal `$isAdmin` check) ever runs, so that only proves the outer loop reaches the category, not that the elevation check inside it passes. Confirm elevation directly (or drop `-WhatIf` for a real run) if you need to verify that specifically.

**Real `/ResetBase` run (2026-07-04)**: also ran the actual (non-`-WhatIf`) `Dism.exe /Online /Cleanup-Image /StartComponentCleanup /ResetBase` on this machine. Result: succeeded (exit code 0), but reclaimed **~0 bytes** — component store size and reclaimable-package count were identical before and after, since the conservative cleanup had already run twice in the prior two days and there was nothing left in the update-rollback bucket to remove. Lesson: the "Actual Size of Component Store" figure from `AnalyzeComponentStore` (12.42 GB here) is *not* the reclaimable amount — most of it (`Shared with Windows`) is active system files, never reclaimable. Don't run `/ResetBase` expecting anywhere near that number; check "Number of Reclaimable Packages" for a more honest signal, and even that doesn't map to a specific MB figure.

**Safe categories** (no opt-in needed): JetBrains/browser/Postman caches, npm/NuGet caches, old Azure Functions Core Tools versions, User Temp, WER archives, crash dumps, Docker unused data (`system prune` + `builder prune` — never touches volumes or in-use tagged images), Windows Update component store cleanup (`DISM /StartComponentCleanup`, conservative), Recycle Bin, Windows Update download cache, Explorer thumbnail/icon cache, pip cache, Yarn cache, VS Code cache.

**Conditional categories** (`-IncludeConditional`): TechSmith old installers, WSL disk compact, `Windows.old` (removes upgrade rollback), DISM `/ResetBase` (removes ability to uninstall current updates), aggressive Docker prune (`-a --volumes`, can remove images/volumes you still need).

Some categories (Docker, DISM, Windows Update cache) need Docker running / admin rights respectively — they skip themselves with a `WARN` line if the prerequisite isn't met, rather than failing the whole scan.

### 3. Reports directory

The PowerShell script's own report, the app's combined Markdown report, and `history.jsonl` (see below) are all saved to `C:\Users\nayah\MaintenanceReports\`. Update the `ReportDir` const in `Program.cs` if you want a different location.

### Recommendation history and insights

Every run appends one line to `history.jsonl` in the reports directory: timestamp, whether it was a clean run, the model used, drive free space before/after, and — per category — how much was scanned vs. actually freed (freed is only populated on `-Clean` runs). Before asking the AI for advice, the app loads the last 10 runs and builds a compact summary (drive-space trend, and per category: average MB freed across actual cleans, and how often the AI recommended it) — that summary is appended to the prompt sent to the model, and also shown in the saved `.md` report under "Historical Insights" so you can see exactly what the AI was told.

This means categories that reliably free real space get reinforced over time, and categories the AI keeps suggesting that never actually get cleaned get called out. There's no separate config for this — it's automatic once `history.jsonl` has at least one prior run. Delete `history.jsonl` (or move it aside) to reset the learning.

### AI tool calling (deeper analysis on demand)

Beyond the fixed insights summary above, the model can call two read-only tools mid-conversation if it wants more detail than the summary gives it:

- `get_category_history(label)` — every recorded scanned/freed data point for one category across *all* history (the insights summary only covers the last 10 runs and top 8 categories).
- `get_disk_space_forecast()` — a simple trend computed from free-space-after across all runs, to gauge urgency.

This is wired through `HuggingFaceClient.GetMaintenanceAdviceAsync`'s optional `toolExecutor` parameter — when given, it sends the tools alongside the prompt and loops (up to 4 round trips) whenever the model responds with `tool_calls` instead of a final answer, executing each locally and feeding the result back. Both tools only read `history.jsonl`; there's no tool that changes anything on the system — cleanup stays something only you trigger via `-Clean`.

Not every provider behind HF's router supports tool calling equally well, so this degrades gracefully: a model that ignores the `tools` field just answers directly with no worse behavior than before tools existed (verified the non-tool-calling wire format is byte-identical whether or not a `toolExecutor` is passed).

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

# Clean mode (deletes cache/temp files found by the scan) -- left commented out on
# purpose so a casual copy-paste of this block never deletes anything by accident.
# Uncomment (drop the leading '#') whenever you actually want to free space:
# dotnet run --project MaintenanceAgent -- --clean
```

Output includes the raw scan results, AI-generated recommendations, and the path to the saved Markdown report.

## Troubleshooting

**`Hugging Face API error: The requested name is valid, but no data of the requested type was found.`**
This is a DNS-level failure, not an HF API error — it means the app is trying to reach a hostname that no longer resolves. Make sure you're on a build that posts to `https://router.huggingface.co/v1/chat/completions` (HF retired the old per-model `api-inference.huggingface.co/models/{model}/...` endpoint).

**401/403 from the HF API**
Your token likely lacks the "Inference Providers" permission. Generate a new fine-grained token at [huggingface.co/settings/tokens](https://huggingface.co/settings/tokens) with that scope explicitly checked.

**400 `model_not_supported`: "not supported by any provider you have enabled"**
The model exists but none of the providers hosting it are enabled/available on your HF account. The app already retries automatically through `HuggingFaceClient.FallbackModels`, ordered by provider coverage:

| Model | Live providers (as checked) |
|---|---|
| `zai-org/GLM-5.2` (preferred) | Novita, Together, Fireworks, Featherless, DeepInfra, zai-org |
| `openai/gpt-oss-120b` | Groq, Together, Cerebras, Novita, Fireworks, DeepInfra, Featherless, Scaleway, OVHcloud, Nscale |
| `meta-llama/Llama-3.1-8B-Instruct` | Novita, Nscale, Featherless, Scaleway, DeepInfra |
| `Qwen/Qwen2.5-Coder-32B-Instruct` | Nscale, Featherless, Scaleway |
| `Qwen/Qwen3-Coder-480B-A35B-Instruct` | Novita, Featherless |
| `Qwen/Qwen2.5-7B-Instruct-1M` | Featherless only |

If all of them fail, check which providers actually serve any given model with:
```
curl "https://huggingface.co/api/models/<org>/<model>?expand[]=inferenceProviderMapping"
```
and review/enable providers at [huggingface.co/settings/inference-providers](https://huggingface.co/settings/inference-providers).

**404 or "model not found" from the HF API**
The model in `HF_MODEL` (or the default) isn't available on the Inference Providers router at all. Check [supported models](https://huggingface.co/docs/inference-providers) and switch to one that's listed.

**Empty or truncated AI recommendations (no error, just blank/cut-off text)**
Found and fixed 2026-07-05: reasoning-capable models like GLM-5.2 spend part of their token budget on internal reasoning before the visible answer, and tool-calling rounds add more on top — the original `max_tokens: 800` was truncating real responses (`finish_reason: "length"`) well before the model finished, sometimes leaving 0 visible characters. Bumped to `max_tokens: 2000` in `HuggingFaceClient.SendOnceAsync`. If you still see this, check `finish_reason` isn't `"length"` and consider raising it further for very verbose models.

**402 "You have depleted your monthly included credits"**
You've used up Hugging Face's free monthly Inference Providers allowance (this happens fast if you're iterating/testing a lot — tool-calling conversations cost more per run since each round trip re-sends the growing conversation). Not a bug: the app surfaces this error as-is rather than silently failing. Options: wait for the monthly reset, purchase pre-paid credits, or subscribe to PRO (20x more included usage) at [huggingface.co/settings/inference-providers](https://huggingface.co/settings/inference-providers).

## Safety model

This agent runs with real permissions on a real machine, so the design is deliberately conservative:

- **Read-only by default.** A plain run only *reports* reclaimable space. Deleting requires the explicit `-Clean` flag.
- **`-WhatIf` is a genuine dry run.** Every category is wrapped in a `ShouldProcess` gate — including the ones that shell out to `docker` and `Dism.exe`, which don't understand PowerShell's `-WhatIf` themselves.
- **Destructive categories are opt-in and labelled.** The five conditional categories each carry a written warning stating exactly what you lose.
- **The model cannot delete anything.** The two tools exposed to the LLM (`get_category_history`, `get_disk_space_forecast`) are strictly read-only. The model can advise; only a human can trigger `-Clean`.
- **No hard-coded credentials.** The API key comes from the environment or the app refuses to start.

## License

Copyright (C) 2026 Jairo Alberto Zúñiga Gómez.

Licensed under the **GNU Affero General Public License v3.0 or later** (AGPL-3.0-or-later).
You may use, study, modify and redistribute this software. If you modify it and offer it to
others — including as a network or hosted service — you must publish your modified source
under the same license. See [LICENSE](LICENSE) for the full text.

As the sole copyright holder, the author may also grant separate commercial licenses.
For commercial licensing enquiries, open an issue on this repository.
