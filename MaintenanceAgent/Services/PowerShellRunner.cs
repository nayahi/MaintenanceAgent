using System.Diagnostics;
using System.Text.Json;
using MaintenanceAgent.Models;

namespace MaintenanceAgent.Services;

public class PowerShellRunner
{
    private readonly string _pwshPath;
    private readonly string _scriptPath;

    /// <param name="pwshPath">
    /// PowerShell 7 executable. Defaults to the MAINTENANCE_PWSH_PATH environment variable,
    /// then to the PATH-resolved <c>pwsh</c>.
    /// </param>
    /// <param name="scriptPath">
    /// The Invoke-MaintenanceScan.ps1 script. Defaults to the MAINTENANCE_SCRIPT_PATH
    /// environment variable, then to %USERPROFILE%\Scripts\Invoke-MaintenanceScan.ps1.
    /// </param>
    public PowerShellRunner(string? pwshPath = null, string? scriptPath = null)
    {
        var candidate = pwshPath
            ?? Environment.GetEnvironmentVariable("MAINTENANCE_PWSH_PATH");

        // Fall back to PATH-resolved pwsh if no explicit path exists on disk
        _pwshPath = !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate) ? candidate : "pwsh";

        _scriptPath = scriptPath
            ?? Environment.GetEnvironmentVariable("MAINTENANCE_SCRIPT_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Scripts",
                "Invoke-MaintenanceScan.ps1");
    }

    public async Task<ScanResult> RunScanAsync(bool cleanMode = false, CancellationToken ct = default)
    {
        var args = $"-ExecutionPolicy Bypass -NonInteractive -File \"{_scriptPath}\"";
        if (cleanMode) args += " -Clean -SkipDiskOptimize";

        var psi = new ProcessStartInfo
        {
            FileName               = _pwshPath,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        var rawOutput  = stdout.ToString();
        var reportFile = ParseSentinel(rawOutput, "REPORT_FILE:");

        return new ScanResult
        {
            RawOutput       = rawOutput,
            ReportFilePath  = reportFile ?? string.Empty,
            ScriptSucceeded = process.ExitCode == 0,
            ErrorMessage    = stderr.Length > 0 ? stderr.ToString() : null,
            Summary         = ParseRunSummary(rawOutput)
        };
    }

    // Parses a "PREFIX:<rest of line>" sentinel line emitted by the PS7 script
    private static string? ParseSentinel(string output, string prefix)
    {
        var line = output.Split('\n')
                         .FirstOrDefault(l => l.TrimStart().StartsWith(prefix));
        return line?[(line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..].Trim();
    }

    // Parses the sentinel line emitted by the PS7 script: "RUN_SUMMARY_JSON:{...}"
    private static RunSummary? ParseRunSummary(string output)
    {
        var json = ParseSentinel(output, "RUN_SUMMARY_JSON:");
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<RunSummary>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
