using System.Diagnostics;
using MaintenanceAgent.Models;

namespace MaintenanceAgent.Services;

public class PowerShellRunner
{
    private readonly string _pwshPath;
    private readonly string _scriptPath;

    public PowerShellRunner(
        string pwshPath   = @"D:\Program Files\PowerShell\7\pwsh.exe",
        string scriptPath = @"C:\Users\nayah\Scripts\Invoke-MaintenanceScan.ps1")
    {
        // Fall back to PATH-resolved pwsh if the default path doesn't exist
        _pwshPath   = File.Exists(pwshPath) ? pwshPath : "pwsh";
        _scriptPath = scriptPath;
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
        var reportFile = ParseReportFilePath(rawOutput);

        return new ScanResult
        {
            RawOutput       = rawOutput,
            ReportFilePath  = reportFile ?? string.Empty,
            ScriptSucceeded = process.ExitCode == 0,
            ErrorMessage    = stderr.Length > 0 ? stderr.ToString() : null
        };
    }

    // Parses the sentinel line emitted by the PS7 script: "REPORT_FILE:C:\..."
    private static string? ParseReportFilePath(string output)
    {
        var line = output.Split('\n')
                         .FirstOrDefault(l => l.TrimStart().StartsWith("REPORT_FILE:"));
        return line?.Substring(line.IndexOf(':') + 1).Trim();
    }
}
