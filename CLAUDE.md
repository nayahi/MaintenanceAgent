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

# List every registered plugin task (name, opt-in status, description) and exit
dotnet run --project MaintenanceAgent -- --list-tasks

# Run one native plugin task in isolation (does not require --clean)
dotnet run --project MaintenanceAgent -- --task docker
dotnet run --project MaintenanceAgent -- --task onedrive

# Clean mode + every opt-in plugin task
dotnet run --project MaintenanceAgent -- --deep-clean
```

There is no test suite; there's nothing to run for `test`/`lint` beyond `dotnet build` catching compile errors. Plugin tasks are verified manually (see "Native plugin tasks" below) since they shell out to real external state (Docker, OneDrive) that isn't practical to mock.

## Configuration (environment variables)

- `HF_API_KEY` — **required**. Hugging Face API token (Bearer auth). The app exits with an error if unset.
- `HF_MODEL` — optional. Overrides the preferred model (`HuggingFaceClient.DefaultModel`, currently `zai-org/GLM-5.2`). Must be a model ID supported by Hugging Face's [Inference Providers router](https://huggingface.co/docs/inference-providers) (optionally suffixed `:provider`, e.g. `:fastest`) — check `https://huggingface.co/api/models/{model}?expand[]=inferenceProviderMapping` for which providers actually serve a given model before switching. If the preferred model returns `model_not_supported`, `HuggingFaceClient` automatically retries the next entry in `HuggingFaceClient.FallbackModels` (ordered by provider coverage) rather than failing outright.

## Architecture

`Program.cs` (top-level statements) is a DI composition root, not a linear script: it parses CLI args (`Services/CliArgs.cs` → `Models/MaintenanceRunOptions.cs`), builds a `Microsoft.Extensions.DependencyInjection` `ServiceCollection`/`ServiceProvider`, then resolves and calls `Services/MaintenanceOrchestrator.cs` (Facade), which owns the actual pipeline. This exists specifically so cleanup capabilities (Docker, OneDrive, and whatever comes next) can be added as self-contained plugins instead of hand-wired one-offs in `Program.cs` — see "Native plugin tasks" below for the concrete mechanism and why it's shaped this way.

**DI gotcha, if you touch this again**: `HuggingFaceClient` is registered via a named `HttpClient` (`services.AddHttpClient("HuggingFace", ...)`) plus an explicit factory delegate (`services.AddSingleton(sp => new HuggingFaceClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("HuggingFace"), model))`), **not** `services.AddHttpClient<HuggingFaceClient>(...)` (a typed client). A typed-client registration lets `ActivatorUtilities` resolve the constructor, and since nothing is registered for the `string? model` parameter it silently defaults to `null` — discarding the resolved `HF_MODEL`/default-model value with no error. `HuggingFaceClient.Create(...)` (the old raw-`new HttpClient()` static factory) no longer exists; this DI wiring is what it was replaced with, and it's also what finally makes the already-referenced `Microsoft.Extensions.Http` package do something.

`MaintenanceOrchestrator.RunAsync` runs the PS7 scan/clean first (always, via `PowerShellRunner`, unconditionally — it's not one of the plugin tasks below, see why in that section), then any opted-in native plugin tasks, merges both into one `RunSummary`-shaped result, and **wraps the Hugging Face call in its own try/catch**: if the AI request fails (rate limit, `402` credit exhaustion, transient network error), the run still appends to `history.jsonl` and writes a report with a placeholder advice string, instead of the whole run aborting before those steps. (Found the hard way: before this fix, a real clean run that genuinely deleted files could still end up with zero record of itself in history/report just because the unrelated AI call failed — don't reintroduce that by moving the HF call back outside a local try/catch.)

1. **`Services/PowerShellRunner.cs`** — shells out to `pwsh.exe` (falls back to PATH-resolved `pwsh` if the hardcoded `D:\Program Files\PowerShell\7\pwsh.exe` doesn't exist) and runs `Invoke-MaintenanceScan.ps1`. Captures stdout/stderr, and parses two sentinel lines the PS7 script emits: `REPORT_FILE:<path>` (its own saved `.txt` report) and `RUN_SUMMARY_JSON:<json>` (structured per-category scan/clean results, deserialized into `Models/RunSummary.cs`). Returns a `Models/ScanResult.cs` with both.
   - `--clean` on the C# app maps to `-Clean -SkipDiskOptimize` on the PS7 script (unattended clean, no interactive TRIM/disk-optimize step).
2. **`Services/HistoryStore.cs`** + **`Services/InsightsBuilder.cs`** — before calling the AI, `MaintenanceOrchestrator` loads the last 10 runs from `history.jsonl` (one JSON `RunRecord` per line, in the reports directory) and `InsightsBuilder.BuildSummary` reduces them to a compact text block: drive-free-space trend, and per-category average MB actually freed vs. how often the AI recommended that category (matched by checking whether the category's `Label` appears as a substring in that run's advice text — no structured AI output needed). After the AI responds (or fails — see above), `MaintenanceOrchestrator` appends the new `RunRecord` (this run's categories + plugin task outcomes + the advice given, or a placeholder if the AI call failed) so the next run can build on it.
3. **`Services/HuggingFaceClient.cs`** — posts the scan's raw text (truncated to ~6000 chars to fit free-tier context limits) plus the historical insights block to HF's unified [Inference Providers](https://huggingface.co/docs/inference-providers) router at `https://router.huggingface.co/v1/chat/completions` (OpenAI-compatible; the model is specified in the request body, not the URL path — the old per-model `api-inference.huggingface.co/models/{model}/...` endpoint was retired), using a fixed system prompt asking for the "top 3 maintenance actions." `max_tokens` is 2000, not a smaller number — reasoning-capable models (GLM-5.2 included) spend part of that budget on hidden/visible reasoning before the visible answer, and tool-calling rounds add more; 800 was silently truncating real responses (`finish_reason: "length"`, sometimes to 0 visible characters) before this was caught in testing. On a `400 model_not_supported` response it walks `FallbackModels` in order until one succeeds (`LastUsedModel` records which one won, so `Program.cs` can log/report it) — any other error status (including `402` credit exhaustion) fails immediately rather than burning through the whole list, since those aren't model-specific problems a fallback would fix. Request/response shapes are OpenAI-style records in `Models/HuggingFaceModels.cs` (`HfChatRequest`/`HfChatResponse`/`HfErrorResponse`/etc.).
   - **Tool calling**: `GetMaintenanceAdviceAsync` takes an optional `Func<string, string, string> toolExecutor` (tool name, JSON args → JSON result). When given, `RunConversationAsync` sends the OpenAI-style `tools` array (`MaintenanceTools.Definitions`) and loops — if the model responds with `tool_calls` instead of final content, each call is dispatched through `toolExecutor`, appended back as a `role: "tool"` message, and the conversation is resent — capped at `MaxToolRoundTrips` (4) to bound cost/latency if a model loops. `RequestJsonOptions` (`JsonIgnoreCondition.WhenWritingNull`) keeps the wire format byte-identical to the pre-tool-calling shape when `toolExecutor` is null, so this is fully backward compatible. Deliberately one-way: tools are read-only queries against already-recorded history, never anything that changes system state — destructive actions stay something only a human runs via `-Clean`.
   - **`Services/MaintenanceTools.cs`** defines the actual tools and executes them against `HistoryStore.LoadAll()` (the *full* history, unlike the capped-and-summarized `InsightsBuilder` text): `get_category_history(label)` returns every recorded scanned/freed data point for one category, and `get_disk_space_forecast()` computes a simple trend from `DriveFreeGBAfter` across all runs. Add new tools here following the same pattern (schema in `Definitions`, a case in `Execute`'s switch) — keep them read-only.
4. **`Services/ReportWriter.cs`** — combines the raw scan output, the AI advice, and (if any) the historical insights block that was fed to the AI into one timestamped Markdown file (`WeeklyAIReport_<timestamp>.md`) under the reports directory.

### Native plugin tasks (`Services/Tasks/`)

This is the extension point for cleanup capabilities that make more sense living natively in the C# process than as another PS7-script category — the two driving examples (Docker, OneDrive) both need to orchestrate external processes/services across a restart, which the PS7 script's `Command`-mode categories don't model well.

- **`IMaintenanceTask`** — the plugin contract: `Name` (stable key, matched against `--task <name>`), `Description` (shown by `--list-tasks`), `IsOptIn` (always `true` today — a task only runs if explicitly named via `--task` or implied by `--deep-clean`, never under a bare `--clean`), and `RunAsync(MaintenanceTaskContext, CancellationToken) → Task<MaintenanceTaskOutcome>`.
- **`IMaintenanceTaskFactory`** / **`MaintenanceTaskFactory`** (Factory pattern) — resolves `IEnumerable<IMaintenanceTask>` from DI (every `services.AddSingleton<IMaintenanceTask, X>()` registration in `Program.cs`) and filters to whichever ones this run's `MaintenanceRunOptions` actually asked for. Adding a third task is one new class implementing `IMaintenanceTask` + one new `AddSingleton` line in `Program.cs` — nothing else changes.
- **`MaintenanceTaskOutcome`** — kept separate from `Models.CategoryResult` (that record is the PS7 script's `RUN_SUMMARY_JSON` wire-format DTO) but converts into it via `.ToCategoryResult()`, which is how plugin task results end up merged into the same `RunSummary`/`history.jsonl`/AI-insights pipeline the PS7 categories already use. Plugin task categories report `Type = "Plugin"` in history (vs. the PS7 script's own `"Safe"`/`"Conditional"`) so they're distinguishable later.
- **`ExternalProcessRunner`** — generalizes `PowerShellRunner`'s `Process`/stdout-capture pattern for shelling out to arbitrary executables (`docker`, `attrib`, `reg`, `wsl`); also has a `Get-Command`-equivalent `FindOnPath` helper. Every task in this folder talks to the OS by shelling out via this, not via .NET wrapper libraries (e.g. registry reads go through `reg query`, not the `Microsoft.Win32.Registry` package) — deliberate, keeps the whole folder consistent and avoids adding package/TFM dependencies for one task.
- **`DockerCleanupTask`** (`--task docker`) — `docker system prune -a --volumes --force` + `docker builder prune --all --force`, then stops Docker Desktop, runs `wsl --shutdown`, restarts Docker Desktop, and polls `docker info` (up to ~2 min) before reporting success. The restart/WSL-shutdown step is not optional set dressing: pruning alone only frees space inside Docker's WSL2 VM accounting — the VHDX doesn't shrink and nothing comes back to the host filesystem without it. Reclaimable-MB before/after is a best-effort parse of `docker system df`'s output (parsed from the end of each line, since the `TYPE` column can be multi-word — `"Local Volumes"`, `"Build Cache"` — so front-indexing doesn't work). This is a full reimplementation independent of the PS7 script's own `-IncludeConditional` "Docker aggressive prune" category, specifically so Docker cleanup is triggerable on its own — `-IncludeConditional` also bundles `Windows.old` removal and DISM `/ResetBase`, both much riskier, with no way to opt into just one.
- **`OneDriveFreeUpTask`** (`--task onedrive`) — discovers every OneDrive account folder via `reg query "HKCU\Software\Microsoft\OneDrive\Accounts" /s` (handles personal + multiple org accounts — confirmed this correctly finds both `OneDrive` and e.g. `OneDrive - <Org Name>` on a multi-account machine), then runs `attrib +U -P "<folder>\*" /S /D` on each, which tells the OneDrive client to dehydrate (mark cloud-only) every file under it. Nothing is deleted — files stay visible and re-download automatically on open. `FreedMB` is always reported as `null`, not a guess: OneDrive dehydrates asynchronously in the background after the attribute flip (observed taking a couple of minutes to plateau on a large tree), so there's no synchronous "freed" number honestly knowable when `RunAsync` returns.

**Deliberately not done, and why**: the PS7 script's existing 21 categories are not being decomposed into `IMaintenanceTask` plugins — they stay inside `Invoke-MaintenanceScan.ps1` as one unit, called via `PowerShellRunner` directly (not through this interface, since `ScanResult`'s shape doesn't fit `MaintenanceTaskOutcome` without forcing an LSP violation). Neither `DockerCleanupTask` nor `OneDriveFreeUpTask` is wired into `ScheduleMaintenanceTask.ps1`'s unattended weekly run — Docker prune wipes volumes, OneDrive dehydration changes local file availability, and both stay manually-triggered only. If you're tempted to add `-IncludeConditional`-equivalent automation to the weekly SYSTEM task, don't, without a human in the loop that week — see the git history around when `DockerCleanupTask` was added for the reasoning (a named/labeled compose volume with real database data nearly got wiped by the underlying `--volumes --force` flag during manual testing).

The PS7 script's own `.txt` report, the C# app's combined `.md` report, and `history.jsonl` all land in the same directory: `C:\Users\nayah\MaintenanceReports\`.

### The external PowerShell script

`Invoke-MaintenanceScan.ps1` (canonical copy at `C:\Users\nayah\Scripts\`, mirrored into this repo's `Scripts/` folder for version control — keep both in sync when editing) defines cleanup targets as a list of category objects (`$SafeCategories` / `$ConditionalCategories`), each with a `Mode` of `Folder` (delete folder contents), `Command` (run a scriptblock, e.g. `npm cache clean`, `dotnet nuget locals clear`, `docker system prune`), or `DynamicAzFunc` (resolves old Azure Functions Core Tools version folders at runtime, always keeping the newest). Scan mode only measures sizes; `-Clean` actually deletes/runs the command. `-IncludeConditional` adds riskier categories that require explicit opt-in. `Command`-mode categories are only invoked during `-Clean` — in scan-only mode their reclaimable size comes from `Paths` (if any), so categories with no simple folder proxy (Docker, DISM, WSL) report `0 MB` in scan mode by design and explain why in their `Note`.

The script declares `[CmdletBinding(SupportsShouldProcess)]`; the clean-phase loop gates each category (and the final `Optimize-Volume` step) behind `$PSCmdlet.ShouldProcess(...)`, so `-Clean -WhatIf` previews every category — including external-tool ones like Docker/DISM that don't understand PowerShell's native `-WhatIf` — without ever executing them. The C# app's `--clean` flag does not currently pass `-WhatIf` through; use the PS7 script directly for a dry run. Admin-gated categories (DISM, Windows Update cache) need the whole process elevated to actually run — a non-elevated parent can't silently elevate just the `pwsh.exe` child without breaking the stdout capture `PowerShellRunner` depends on (that would require `UseShellExecute = true` + `runas`, which is incompatible with `RedirectStandardOutput/Error`).

Adding a new category (the extension point for this "framework") means appending an `[ordered]@{ Label; Note|Warning; Paths; Mode; Command? }` hashtable to `$SafeCategories` or `$ConditionalCategories` — no changes to the scan/clean loop logic are needed. Categories that need elevation check `$isAdmin` (script-scoped, set once near the top) inside their `Command` scriptblock and skip with a `WARN` if absent, rather than failing. The clean-phase loop stores `$cat['FreedBytes']` after each category runs, which feeds the `RUN_SUMMARY_JSON` sentinel the C# side uses for history — a new category's `Label` should stay stable across edits since it's the join key used to match a category across runs in `history.jsonl`.

Current Safe categories beyond the original dev-cache set: Docker unused data (`system prune` + `builder prune`, never touches volumes or in-use tagged images), Windows Update component store cleanup (`DISM /StartComponentCleanup`, conservative/rollback-preserving), Recycle Bin, Windows Update download cache, Explorer thumbnail/icon cache, pip cache, Yarn cache, VS Code cache. Conditional (opt-in, `-IncludeConditional`) additions: `Windows.old`, `DISM /ResetBase` (aggressive, drops update-rollback ability), and aggressive Docker prune (`-a --volumes`, can remove in-use-later images and volume data).

The DISM component-store category's `Note` is dynamically replaced at scan time (before the scan/clean loop, alongside the `DynamicAzFunc` resolution) by `Get-ComponentStoreAnalysis`, which runs the read-only `Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore` (only when `$isAdmin`) and surfaces its real reclaimable-package count and cleanup recommendation — DISM doesn't report an exact MB figure, so this replaces the static placeholder text rather than populating `ScannedBytes`. This is the pattern to follow for adding other "live diagnostic" data to a category: mutate `$cat.Note` (or add a new hashtable key) in the resolution block before `# SCAN PHASE` begins, so it flows into `Write-Report`'s output and — since that raw text is what gets sent to the AI — into the model's input automatically, with no changes needed to `HuggingFaceClient` or the prompt.

A companion script, `ScheduleMaintenanceTask.ps1`, registers a weekly Windows Scheduled Task (Monday 09:00, SYSTEM account) that runs the scan script directly in clean mode — the C# agent is a separate, optional AI-summarization layer on top, not itself scheduled.

## Notable hardcoded paths

Several paths are hardcoded to this specific machine rather than being configurable (no appsettings.json/config layer exists):
- `PowerShellRunner`'s default `pwshPath` and `scriptPath` constructor params
- `Program.cs`'s `ReportDir` const

When changing these, prefer updating the constructor defaults / const in place rather than introducing new config plumbing, since this is a single-machine personal utility, not a multi-environment service.
