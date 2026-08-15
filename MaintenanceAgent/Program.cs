// MaintenanceAgent — an AI-assisted Windows maintenance agent.
// Copyright (C) 2026 Jairo Alberto Zúñiga Gómez
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. See the LICENSE file at the root of this repository.
//
// This program is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.

using System.Net.Http.Headers;
using MaintenanceAgent.Models;
using MaintenanceAgent.Services;
using MaintenanceAgent.Services.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

// ── Configuration from environment variables ─────────────────────────────────
const string EnvKeyApiKey    = "HF_API_KEY";
const string EnvKeyModel     = "HF_MODEL";
const string EnvKeyReportDir = "MAINTENANCE_REPORT_DIR";

// Reports land in %USERPROFILE%\MaintenanceReports unless MAINTENANCE_REPORT_DIR overrides it.
var reportDir = Environment.GetEnvironmentVariable(EnvKeyReportDir) is { Length: > 0 } dir
    ? dir
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MaintenanceReports");

// Never hard-code a fallback token here: this file is public, and a committed key is a leaked key.
var apiKey = Environment.GetEnvironmentVariable(EnvKeyApiKey);
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine($"ERROR: Environment variable '{EnvKeyApiKey}' is not set.");
    Console.Error.WriteLine("Set it with:");
    Console.Error.WriteLine($"  $env:{EnvKeyApiKey} = 'hf_YOUR_TOKEN_HERE'");
    Console.Error.WriteLine("Get a free token at: https://huggingface.co/settings/tokens");
    return 1;
}

var model   = Environment.GetEnvironmentVariable(EnvKeyModel) ?? HuggingFaceClient.DefaultModel;
var options = CliArgs.Parse(args);

// ── Composition root ──────────────────────────────────────────────────────────
var services = new ServiceCollection();

services.AddHttpClient("HuggingFace", c =>
{
    c.Timeout = TimeSpan.FromSeconds(90);
    c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

services.AddSingleton<PowerShellRunner>();
services.AddSingleton(new HistoryStore(reportDir));
services.AddSingleton(new ReportWriter(reportDir));
services.AddSingleton<MaintenanceTools>();

// Registered via a named HttpClient + explicit factory delegate rather than
// services.AddHttpClient<HuggingFaceClient>(...) (a typed client): ActivatorUtilities would try
// to resolve the constructor's `string? model` parameter, find nothing registered for it, and
// silently fall back to null -- discarding the resolved HF_MODEL/default value with no error.
services.AddSingleton(sp =>
    new HuggingFaceClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("HuggingFace"), model));

services.AddSingleton<IMaintenanceTask, DockerCleanupTask>();
services.AddSingleton<IMaintenanceTask, OneDriveFreeUpTask>();
services.AddSingleton<IMaintenanceTaskFactory, MaintenanceTaskFactory>();
services.AddSingleton<MaintenanceOrchestrator>();

await using var provider = services.BuildServiceProvider();

if (args.Contains("--list-tasks", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("PS7 baseline (always runs under --clean): scan/clean via Invoke-MaintenanceScan.ps1");
    foreach (var task in provider.GetServices<IMaintenanceTask>())
        Console.WriteLine($"  {task.Name,-10} (opt-in: {task.IsOptIn})  {task.Description}");
    return 0;
}

// Typo protection: warn about any --task name that doesn't match a registered task.
var registeredNames = provider.GetServices<IMaintenanceTask>().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
foreach (var requested in options.RequestedTaskNames.Where(n => !registeredNames.Contains(n)))
    Console.Error.WriteLine($"WARNING: --task '{requested}' does not match any registered task. Run --list-tasks to see available names.");

Log($"MaintenanceAgent starting");
Log($"Mode:  {(options.CleanMode ? "CLEAN (will delete files)" : "SCAN ONLY (read-only)")}");
Log($"Model: {model}");

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));

try
{
    var orchestrator = provider.GetRequiredService<MaintenanceOrchestrator>();
    return await orchestrator.RunAsync(options, cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation timed out after 15 minutes.");
    return 2;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Hugging Face API error: {ex.Message}");
    Console.Error.WriteLine("Check your HF_API_KEY and that the model is available on the free tier.");
    return 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
    return 3;
}

static void Log(string message) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
