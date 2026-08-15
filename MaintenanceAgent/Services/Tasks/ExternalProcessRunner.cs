using System.Diagnostics;
using System.Text;

namespace MaintenanceAgent.Services.Tasks;

// Generalizes PowerShellRunner's Process-wrapping pattern (stdout/stderr capture via
// BeginOutputReadLine, WaitForExitAsync(ct)) to any executable, so DockerCleanupTask and
// OneDriveFreeUpTask don't each duplicate it.
internal static class ExternalProcessRunner
{
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, string arguments, Action<string>? onOutputLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stdout.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stderr.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // Get-Command <name> equivalent: resolves an executable via PATH + PATHEXT. Returns null if
    // not found rather than throwing, matching the PS7 script's own
    // "$docker = (Get-Command docker -ErrorAction SilentlyContinue)?.Source" pattern.
    public static string? FindOnPath(string exeName)
    {
        if (Path.IsPathRooted(exeName))
            return File.Exists(exeName) ? exeName : null;

        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var hasExtension = Path.HasExtension(exeName);

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathDirs)
        {
            if (hasExtension)
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) return candidate;
                continue;
            }

            foreach (var ext in pathExt)
            {
                var candidate = Path.Combine(dir, exeName + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }
}
