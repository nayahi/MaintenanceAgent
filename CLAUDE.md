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
- `HF_MODEL` — optional. Overrides the preferred model (`HuggingFaceClient.DefaultModel`, currently `zai-org/GLM-5.2`). Must be a model ID supported by Hugging Face's [Inference Providers router](https://huggingface.co/docs/inference-providers) (optionally suffixed `:provider`, e.g. `:fastest`) — check `https://huggingface.co/api/models/{model}?expand[]=inferenceProviderMapping` for which providers actually serve a given model before switching. If the preferred model returns `model_not_supported`, `HuggingFaceClient` automatically retries the next entry in `HuggingFaceClient.FallbackModels` (ordered by provider coverage) rather than failing outright.

## Architecture

The app is a linear pipeline driven from `Program.cs` (top-level statements):

1. **`Services/PowerShellRunner.cs`** — shells out to `pwsh.exe` (falls back to PATH-resolved `pwsh` if the hardcoded `D:\Program Files\PowerShell\7\pwsh.exe` doesn't exist) and runs `Invoke-MaintenanceScan.ps1`. Captures stdout/stderr, and parses two sentinel lines the PS7 script emits: `REPORT_FILE:<path>` (its own saved `.txt` report) and `RUN_SUMMARY_JSON:<json>` (structured per-category scan/clean results, deserialized into `Models/RunSummary.cs`). Returns a `Models/ScanResult.cs` with both.
   - `--clean` on the C# app maps to `-Clean -SkipDiskOptimize` on the PS7 script (unattended clean, no interactive TRIM/disk-optimize step).
2. **`Services/HistoryStore.cs`** + **`Services/InsightsBuilder.cs`** — before calling the AI, `Program.cs` loads the last 10 runs from `history.jsonl` (one JSON `RunRecord` per line, in the reports directory) and `InsightsBuilder.BuildSummary` reduces them to a compact text block: drive-free-space trend, and per-category average MB actually freed vs. how often the AI recommended that category (matched by checking whether the category's `Label` appears as a substring in that run's advice text — no structured AI output needed). After the AI responds, `Program.cs` appends the new `RunRecord` (this run's categories + the advice given) so the next run can build on it.
3. **`Services/HuggingFaceClient.cs`** — posts the scan's raw text (truncated to ~6000 chars to fit free-tier context limits) plus the historical insights block to HF's unified [Inference Providers](https://huggingface.co/docs/inference-providers) router at `https://router.huggingface.co/v1/chat/completions` (OpenAI-compatible; the model is specified in the request body, not the URL path — the old per-model `api-inference.huggingface.co/models/{model}/...` endpoint was retired), using a fixed system prompt asking for the "top 3 maintenance actions." On a `400 model_not_supported` response it walks `FallbackModels` in order until one succeeds (`LastUsedModel` records which one won, so `Program.cs` can log/report it) — any other error status fails immediately rather than burning through the whole list. Request/response shapes are OpenAI-style records in `Models/HuggingFaceModels.cs` (`HfChatRequest`/`HfChatResponse`/`HfErrorResponse`/etc.).
4. **`Services/ReportWriter.cs`** — combines the raw scan output, the AI advice, and (if any) the historical insights block that was fed to the AI into one timestamped Markdown file (`WeeklyAIReport_<timestamp>.md`) under the reports directory.

The PS7 script's own `.txt` report, the C# app's combined `.md` report, and `history.jsonl` all land in the same directory: `C:\Users\nayah\MaintenanceReports\`.

### The external PowerShell script

`Invoke-MaintenanceScan.ps1` (canonical copy at `C:\Users\nayah\Scripts\`, mirrored into this repo's `Scripts/` folder for version control — keep both in sync when editing) defines cleanup targets as a list of category objects (`$SafeCategories` / `$ConditionalCategories`), each with a `Mode` of `Folder` (delete folder contents), `Command` (run a scriptblock, e.g. `npm cache clean`, `dotnet nuget locals clear`, `docker system prune`), or `DynamicAzFunc` (resolves old Azure Functions Core Tools version folders at runtime, always keeping the newest). Scan mode only measures sizes; `-Clean` actually deletes/runs the command. `-IncludeConditional` adds riskier categories that require explicit opt-in. `Command`-mode categories are only invoked during `-Clean` — in scan-only mode their reclaimable size comes from `Paths` (if any), so categories with no simple folder proxy (Docker, DISM, WSL) report `0 MB` in scan mode by design and explain why in their `Note`.

The script declares `[CmdletBinding(SupportsShouldProcess)]`; the clean-phase loop gates each category (and the final `Optimize-Volume` step) behind `$PSCmdlet.ShouldProcess(...)`, so `-Clean -WhatIf` previews every category — including external-tool ones like Docker/DISM that don't understand PowerShell's native `-WhatIf` — without ever executing them. The C# app's `--clean` flag does not currently pass `-WhatIf` through; use the PS7 script directly for a dry run. Admin-gated categories (DISM, Windows Update cache) need the whole process elevated to actually run — a non-elevated parent can't silently elevate just the `pwsh.exe` child without breaking the stdout capture `PowerShellRunner` depends on (that would require `UseShellExecute = true` + `runas`, which is incompatible with `RedirectStandardOutput/Error`).

Adding a new category (the extension point for this "framework") means appending an `[ordered]@{ Label; Note|Warning; Paths; Mode; Command? }` hashtable to `$SafeCategories` or `$ConditionalCategories` — no changes to the scan/clean loop logic are needed. Categories that need elevation check `$isAdmin` (script-scoped, set once near the top) inside their `Command` scriptblock and skip with a `WARN` if absent, rather than failing. The clean-phase loop stores `$cat['FreedBytes']` after each category runs, which feeds the `RUN_SUMMARY_JSON` sentinel the C# side uses for history — a new category's `Label` should stay stable across edits since it's the join key used to match a category across runs in `history.jsonl`.

Current Safe categories beyond the original dev-cache set: Docker unused data (`system prune` + `builder prune`, never touches volumes or in-use tagged images), Windows Update component store cleanup (`DISM /StartComponentCleanup`, conservative/rollback-preserving), Recycle Bin, Windows Update download cache, Explorer thumbnail/icon cache, pip cache, Yarn cache, VS Code cache. Conditional (opt-in, `-IncludeConditional`) additions: `Windows.old`, `DISM /ResetBase` (aggressive, drops update-rollback ability), and aggressive Docker prune (`-a --volumes`, can remove in-use-later images and volume data).

A companion script, `ScheduleMaintenanceTask.ps1`, registers a weekly Windows Scheduled Task (Monday 09:00, SYSTEM account) that runs the scan script directly in clean mode — the C# agent is a separate, optional AI-summarization layer on top, not itself scheduled.

## Notable hardcoded paths

Several paths are hardcoded to this specific machine rather than being configurable (no appsettings.json/config layer exists):
- `PowerShellRunner`'s default `pwshPath` and `scriptPath` constructor params
- `Program.cs`'s `ReportDir` const

When changing these, prefer updating the constructor defaults / const in place rather than introducing new config plumbing, since this is a single-machine personal utility, not a multi-environment service.
