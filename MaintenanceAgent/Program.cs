using MaintenanceAgent.Models;
using MaintenanceAgent.Services;

// ── Configuration from environment variables ─────────────────────────────────
const string EnvKeyApiKey = "HF_API_KEY";
const string EnvKeyModel  = "HF_MODEL";
const string ReportDir    = @"C:\Users\nayah\MaintenanceReports";

var apiKey = Environment.GetEnvironmentVariable(EnvKeyApiKey);
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine($"ERROR: Environment variable '{EnvKeyApiKey}' is not set.");
    Console.Error.WriteLine("Set it with:");
    Console.Error.WriteLine($"  $env:{EnvKeyApiKey} = 'hf_YOUR_TOKEN_HERE'");
    Console.Error.WriteLine("Get a free token at: https://huggingface.co/settings/tokens");
    return 1;
}

var model     = Environment.GetEnvironmentVariable(EnvKeyModel) ?? HuggingFaceClient.DefaultModel;
var cleanMode = args.Contains("--clean", StringComparer.OrdinalIgnoreCase);

Log($"MaintenanceAgent starting");
Log($"Mode:  {(cleanMode ? "CLEAN (will delete files)" : "SCAN ONLY (read-only)")}");
Log($"Model: {model}");

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));

try
{
    // 1. Run the PS7 maintenance scan script ──────────────────────────────────
    Log("Running PS7 maintenance scan script...");
    var runner = new PowerShellRunner();
    var scan   = await runner.RunScanAsync(cleanMode, cts.Token);

    if (!scan.ScriptSucceeded && !string.IsNullOrEmpty(scan.ErrorMessage))
        Console.Error.WriteLine($"Script stderr:\n{scan.ErrorMessage}");

    Console.WriteLine(scan.RawOutput);

    if (string.IsNullOrWhiteSpace(scan.RawOutput))
    {
        Console.Error.WriteLine("ERROR: No output from the scan script. Check that the path is correct.");
        return 3;
    }

    // 2. Load run history and build insights from past recommendations vs results ─
    var historyStore  = new HistoryStore(ReportDir);
    var recentHistory = historyStore.LoadRecent(10);
    var insights      = InsightsBuilder.BuildSummary(recentHistory);
    if (insights != null)
        Log($"Loaded {recentHistory.Count} past run(s) from history.jsonl to inform this run's recommendations.");

    // 3. Send scan output (+ historical insights) to Hugging Face for AI analysis ─
    //    Tools let the model pull deeper read-only history data mid-conversation if it wants more
    //    than the fixed insights summary already gives it.
    Log($"Sending scan to Hugging Face (preferred model: {model})...");
    var hfClient   = HuggingFaceClient.Create(apiKey, model);
    var tools      = new MaintenanceTools(historyStore);
    var advice     = await hfClient.GetMaintenanceAdviceAsync(
        scan.RawOutput, insights, toolExecutor: tools.Execute, ct: cts.Token);
    var usedModel = hfClient.LastUsedModel ?? model;
    if (usedModel != model)
        Log($"Preferred model unavailable; fell back to: {usedModel}");

    Console.WriteLine();
    Console.WriteLine("══ AI RECOMMENDATIONS ══════════════════════════════════════════");
    Console.WriteLine(advice);
    Console.WriteLine("════════════════════════════════════════════════════════════════");

    // 4. Record this run to history so future runs can learn from it ────────────
    if (scan.Summary is { } summary)
    {
        historyStore.AppendRun(new RunRecord(
            summary.Timestamp, summary.CleanMode, usedModel,
            summary.DriveFreeGBBefore, summary.DriveFreeGBAfter, summary.TotalReclaimableMB,
            summary.Categories, advice));
        Log("Run recorded to history.jsonl.");
    }
    else
    {
        Log("WARNING: scan script did not emit a RUN_SUMMARY_JSON line (older script version?) -- this run was not recorded to history.");
    }

    // 5. Save combined Markdown report ────────────────────────────────────────
    var writer     = new ReportWriter(ReportDir);
    var reportPath = writer.WriteWeeklyReport(scan.RawOutput, advice, usedModel, insights);
    Log($"Report saved: {reportPath}");

    return 0;
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
