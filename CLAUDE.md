# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

MaintenanceAgent is a small .NET 8 console app that orchestrates a weekly Windows disk-cleanup workflow:

1. Runs a PowerShell 7 script (external to this repo — lives at `C:\Users\nayah\Scripts\Invoke-MaintenanceScan.ps1`) that scans (and optionally cleans) known disk-space-hogging cache/temp folders.
2. Sends the scan's text output to a Hugging Face chat-completions model for AI-generated maintenance advice.
3. Writes a combined Markdown report (scan output + AI advice) to `C:\Users\nayah\MaintenanceReports\`.

There is no test project, CI config, or build script beyond the standard `dotnet` CLI.

## Common commands

```powershell
# Build
dotnet build

# Run (requires HF_API_KEY env var — see Configuration below)
dotnet run --project MaintenanceAgent

# Scan-only (read-only, default — no files deleted)
dotnet run --project MaintenanceAgent

# Clean mode (deletes cache/temp files found by the PS7 script)
dotnet run --project MaintenanceAgent -- --clean
```

There is no test suite; there's nothing to run for `test`/`lint` beyond `dotnet build` catching compile errors.

## Configuration (environment variables)

- `HF_API_KEY` — **required**. Hugging Face API token (Bearer auth). The app exits with an error if unset.
- `HF_MODEL` — optional. Overrides the default model (`HuggingFaceClient.DefaultModel`, currently `Qwen/Qwen2.5-7B-Instruct-1M`). Must be a model ID supported by Hugging Face's [Inference Providers router](https://huggingface.co/docs/inference-providers) (optionally suffixed `:provider`, e.g. `:fastest`).

## Architecture

The whole app is a linear, three-stage pipeline driven from `Program.cs` (top-level statements):

1. **`Services/PowerShellRunner.cs`** — shells out to `pwsh.exe` (falls back to PATH-resolved `pwsh` if the hardcoded `D:\Program Files\PowerShell\7\pwsh.exe` doesn't exist) and runs `Invoke-MaintenanceScan.ps1`. Captures stdout/stderr, and parses a `REPORT_FILE:<path>` sentinel line the PS7 script emits to locate its own saved `.txt` report. Returns a `Models/ScanResult.cs`.
   - `--clean` on the C# app maps to `-Clean -SkipDiskOptimize` on the PS7 script (unattended clean, no interactive TRIM/disk-optimize step).
2. **`Services/HuggingFaceClient.cs`** — posts the scan's raw text (truncated to ~6000 chars to fit free-tier context limits) to HF's unified [Inference Providers](https://huggingface.co/docs/inference-providers) router at `https://router.huggingface.co/v1/chat/completions` (OpenAI-compatible; the model is specified in the request body, not the URL path — the old per-model `api-inference.huggingface.co/models/{model}/...` endpoint was retired), using a fixed system prompt asking for the "top 3 maintenance actions." Request/response shapes are OpenAI-style records in `Models/HuggingFaceModels.cs` (`HfChatRequest`/`HfChatResponse`/etc.).
3. **`Services/ReportWriter.cs`** — combines the raw scan output and the AI advice into one timestamped Markdown file (`WeeklyAIReport_<timestamp>.md`) under the reports directory.

Both the PS7 script's own `.txt` report and the C# app's combined `.md` report land in the same directory: `C:\Users\nayah\MaintenanceReports\`.

### The external PowerShell script

`Invoke-MaintenanceScan.ps1` (not part of this .NET project, but what the whole pipeline exists to drive) defines cleanup targets as a list of category objects (`$SafeCategories` / `$ConditionalCategories`), each with a `Mode` of `Folder` (delete folder contents), `Command` (run a scriptblock, e.g. `npm cache clean`, `dotnet nuget locals clear`), or `DynamicAzFunc` (resolves old Azure Functions Core Tools version folders at runtime, always keeping the newest). Scan mode only measures sizes; `-Clean` actually deletes. `-IncludeConditional` adds riskier categories (e.g. TechSmith installers, WSL disk compaction) that require explicit opt-in. A companion script, `ScheduleMaintenanceTask.ps1`, registers a weekly Windows Scheduled Task (Monday 09:00, SYSTEM account) that runs the scan script directly in clean mode — the C# agent is a separate, optional AI-summarization layer on top, not itself scheduled.

## Notable hardcoded paths

Several paths are hardcoded to this specific machine rather than being configurable (no appsettings.json/config layer exists):
- `PowerShellRunner`'s default `pwshPath` and `scriptPath` constructor params
- `Program.cs`'s `ReportDir` const

When changing these, prefer updating the constructor defaults / const in place rather than introducing new config plumbing, since this is a single-machine personal utility, not a multi-environment service.
