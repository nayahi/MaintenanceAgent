using MaintenanceAgent.Models;
using MaintenanceAgent.Services.Tasks;

namespace MaintenanceAgent.Services;

// Facade over the whole run: baseline PS7 scan/clean -> opted-in native plugin tasks -> aggregate
// into one RunSummary shape -> AI advice -> history -> report. Single entry point Program.cs calls.
//
// PowerShellRunner is called directly here, not via IMaintenanceTask -- its real return shape
// (ScanResult: raw text + REPORT_FILE + its own 21-category summary) doesn't fit
// MaintenanceTaskOutcome without an LSP violation. IMaintenanceTask is reserved for the
// homogeneous native plugins (Docker, OneDrive, future ones).
public sealed class MaintenanceOrchestrator
{
    private readonly PowerShellRunner _psRunner;
    private readonly IMaintenanceTaskFactory _taskFactory;
    private readonly HistoryStore _history;
    private readonly HuggingFaceClient _hfClient;
    private readonly MaintenanceTools _tools;
    private readonly ReportWriter _reportWriter;
    private readonly Action<string> _log;

    public MaintenanceOrchestrator(
        PowerShellRunner psRunner,
        IMaintenanceTaskFactory taskFactory,
        HistoryStore history,
        HuggingFaceClient hfClient,
        MaintenanceTools tools,
        ReportWriter reportWriter,
        Action<string>? log = null)
    {
        _psRunner    = psRunner;
        _taskFactory = taskFactory;
        _history     = history;
        _hfClient    = hfClient;
        _tools       = tools;
        _reportWriter = reportWriter;
        _log = log ?? (msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}"));
    }

    public async Task<int> RunAsync(MaintenanceRunOptions options, CancellationToken ct)
    {
        _log("Running PS7 maintenance scan script...");
        var scan = await _psRunner.RunScanAsync(options.CleanMode, ct);

        if (!scan.ScriptSucceeded && !string.IsNullOrEmpty(scan.ErrorMessage))
            Console.Error.WriteLine($"Script stderr:\n{scan.ErrorMessage}");

        Console.WriteLine(scan.RawOutput);

        if (string.IsNullOrWhiteSpace(scan.RawOutput))
        {
            Console.Error.WriteLine("ERROR: No output from the scan script. Check that the path is correct.");
            return 3;
        }

        var outcomes = new List<MaintenanceTaskOutcome>();
        foreach (var task in _taskFactory.CreateTasks(options))
        {
            _log($"Running plugin task: {task.Name}...");
            outcomes.Add(await RunTaskSafelyAsync(task, ct));
        }

        var combinedText = scan.RawOutput;
        if (outcomes.Count > 0)
        {
            var taskSection = string.Join("\n", outcomes.Select(o =>
                $"[{o.Label}] {(o.Success ? "OK" : "FAILED")}: {o.Log ?? o.ErrorMessage}"));
            combinedText = $"{scan.RawOutput}\n\n== Plugin tasks ==\n{taskSection}";
        }

        var combinedSummary = scan.Summary is { } s
            ? s with { Categories = s.Categories.Concat(outcomes.Select(o => o.ToCategoryResult())).ToList() }
            : null;

        var recentHistory = _history.LoadRecent(10);
        var insights = InsightsBuilder.BuildSummary(recentHistory);
        if (insights != null)
            _log($"Loaded {recentHistory.Count} past run(s) from history.jsonl to inform this run's recommendations.");

        _log("Sending scan to Hugging Face for AI analysis...");
        string advice;
        string usedModel;
        try
        {
            advice = await _hfClient.GetMaintenanceAdviceAsync(combinedText, insights, toolExecutor: _tools.Execute, ct: ct);
            usedModel = _hfClient.LastUsedModel ?? "?";
        }
        catch (HttpRequestException ex)
        {
            // Bug found this session: previously this exception propagated out of the whole
            // pipeline before history/report ever got written, so a real cleanup run (files
            // genuinely deleted) silently left no record of itself just because the AI call
            // failed for an unrelated reason (e.g. HF free-tier credits depleted). Cleanup
            // results are never lost to an AI/billing failure now.
            Console.Error.WriteLine($"Hugging Face API error: {ex.Message}");
            advice = $"(AI advice unavailable this run: {ex.Message})";
            usedModel = "none";
        }

        Console.WriteLine();
        Console.WriteLine("══ AI RECOMMENDATIONS ══════════════════════════════════════════");
        Console.WriteLine(advice);
        Console.WriteLine("════════════════════════════════════════════════════════════════");

        if (combinedSummary is { } summary)
        {
            _history.AppendRun(new RunRecord(
                summary.Timestamp, summary.CleanMode, usedModel,
                summary.DriveFreeGBBefore, summary.DriveFreeGBAfter, summary.TotalReclaimableMB,
                summary.Categories, advice));
            _log("Run recorded to history.jsonl.");
        }
        else
        {
            _log("WARNING: scan script did not emit a RUN_SUMMARY_JSON line (older script version?) -- this run was not recorded to history.");
        }

        var reportPath = _reportWriter.WriteWeeklyReport(combinedText, advice, usedModel, insights);
        _log($"Report saved: {reportPath}");

        return 0;
    }

    // Mirrors MaintenanceTools.Execute's own catch-and-report pattern for consistency: one
    // broken opt-in task must never abort AI-advice/history/report for the rest of the run.
    private static async Task<MaintenanceTaskOutcome> RunTaskSafelyAsync(IMaintenanceTask task, CancellationToken ct)
    {
        try
        {
            var context = new MaintenanceTaskContext(Log: msg =>
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{task.Name}] {msg}"));
            return await task.RunAsync(context, ct);
        }
        catch (Exception ex)
        {
            return new MaintenanceTaskOutcome(task.Name, "Plugin", 0, null, Success: false, ErrorMessage: ex.Message);
        }
    }
}
