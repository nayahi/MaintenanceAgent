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

    // 2. Send scan output to Hugging Face for AI analysis ─────────────────────
    Log($"Sending scan to Hugging Face ({model})...");
    var hfClient = HuggingFaceClient.Create(apiKey, model);
    var advice   = await hfClient.GetMaintenanceAdviceAsync(scan.RawOutput, cts.Token);

    Console.WriteLine();
    Console.WriteLine("══ AI RECOMMENDATIONS ══════════════════════════════════════════");
    Console.WriteLine(advice);
    Console.WriteLine("════════════════════════════════════════════════════════════════");

    // 3. Save combined Markdown report ────────────────────────────────────────
    var writer     = new ReportWriter(ReportDir);
    var reportPath = writer.WriteWeeklyReport(scan.RawOutput, advice, model);
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
