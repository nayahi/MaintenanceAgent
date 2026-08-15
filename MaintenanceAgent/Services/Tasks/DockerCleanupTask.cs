using System.Globalization;
using System.Text.RegularExpressions;

namespace MaintenanceAgent.Services.Tasks;

// Native reimplementation of the PS7 script's "Docker aggressive prune" ConditionalCategory --
// deliberately NOT routed through -IncludeConditional (which also bundles Windows.old + DISM
// /ResetBase). Independently invokable via --task docker / --deep-clean.
//
// Pruning alone only frees space inside Docker's WSL2 VM accounting -- the VHDX doesn't shrink
// and nothing comes back to the host filesystem without stopping Docker Desktop, running
// `wsl --shutdown`, and restarting it. That's the whole reason this exists as a task in its own
// right rather than just three docker CLI calls.
public sealed class DockerCleanupTask : IMaintenanceTask
{
    private const int ReadyPollAttempts = 24;
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromSeconds(5);

    public string Name => "docker";
    public string Description =>
        "docker system/builder prune (all unused images + volumes) + Docker Desktop restart + wsl --shutdown to compact the VHDX and actually return space to the host.";
    public bool IsOptIn => true;

    public async Task<MaintenanceTaskOutcome> RunAsync(MaintenanceTaskContext context, CancellationToken ct)
    {
        var docker = ExternalProcessRunner.FindOnPath("docker");
        if (docker is null)
            return Failed("docker CLI not found on PATH.");

        var (infoExit, _, _) = await ExternalProcessRunner.RunAsync(docker, "info", null, ct);
        if (infoExit != 0)
            return Failed("Docker daemon not running.");

        var beforeMB = await GetReclaimableMBAsync(docker, ct);

        context.Log("Running docker system prune -a --volumes --force...");
        await ExternalProcessRunner.RunAsync(docker, "system prune -a --volumes --force", context.Log, ct);

        context.Log("Running docker builder prune --all --force...");
        await ExternalProcessRunner.RunAsync(docker, "builder prune --all --force", context.Log, ct);

        var dockerDesktopExe = FindDockerDesktopExe();
        if (dockerDesktopExe is null)
        {
            context.Log("Docker Desktop.exe not found in the usual install paths -- skipping VHDX compact step.");
            var pruneOnlyAfterMB = await GetReclaimableMBAsync(docker, ct);
            return new MaintenanceTaskOutcome(Name, "Plugin", beforeMB, Math.Max(0, beforeMB - pruneOnlyAfterMB),
                Success: true, Log: "Pruned; Docker Desktop.exe not found so the VHDX was not compacted.");
        }

        context.Log("Stopping Docker Desktop to compact its virtual disk...");
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("Docker Desktop"))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exiting */ }
        }
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var wsl = ExternalProcessRunner.FindOnPath("wsl") ?? "wsl";
        await ExternalProcessRunner.RunAsync(wsl, "--shutdown", context.Log, ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        context.Log("Restarting Docker Desktop...");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dockerDesktopExe) { UseShellExecute = true });

        var ready = false;
        for (var i = 0; i < ReadyPollAttempts; i++)
        {
            await Task.Delay(ReadyPollInterval, ct);
            var (exit, _, _) = await ExternalProcessRunner.RunAsync(docker, "info", null, ct);
            if (exit == 0) { ready = true; break; }
        }

        if (!ready)
        {
            return new MaintenanceTaskOutcome(Name, "Plugin", beforeMB, null, Success: false,
                ErrorMessage: "Docker Desktop did not come back up within ~2 minutes -- check it manually.");
        }

        var afterMB = await GetReclaimableMBAsync(docker, ct);
        var freedMB = Math.Max(0, beforeMB - afterMB);
        return new MaintenanceTaskOutcome(Name, "Plugin", beforeMB, freedMB, Success: true,
            Log: $"Docker Desktop back up. Reclaimable before: {beforeMB:F0}MB, after: {afterMB:F0}MB.");
    }

    private static MaintenanceTaskOutcome Failed(string reason) =>
        new("docker", "Plugin", 0, null, Success: false, ErrorMessage: reason);

    private static string? FindDockerDesktopExe()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)")
        };

        foreach (var root in candidates)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var path = Path.Combine(root, "Docker", "Docker", "Docker Desktop.exe");
            if (File.Exists(path)) return path;
        }

        return null;
    }

    // Best-effort parse of `docker system df`'s RECLAIMABLE column, summed across all rows
    // (Images/Containers/Local Volumes/Build Cache). A row's Type column can be multi-word
    // ("Local Volumes", "Build Cache"), so this parses from the end of each line instead of
    // indexing from the front: the last token is either a "(NN%)" suffix (in which case
    // RECLAIMABLE is the token before it) or the RECLAIMABLE size itself.
    private static async Task<double> GetReclaimableMBAsync(string docker, CancellationToken ct)
    {
        var (exit, stdout, _) = await ExternalProcessRunner.RunAsync(docker, "system df", null, ct);
        if (exit != 0) return 0;

        double totalMB = 0;
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("TYPE", StringComparison.OrdinalIgnoreCase)) continue;

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;

            var last = tokens[^1];
            var sizeToken = Regex.IsMatch(last, @"^\(\d+%\)$") ? tokens[^2] : last;
            totalMB += TryParseSizeToMB(sizeToken);
        }

        return totalMB;
    }

    private static double TryParseSizeToMB(string token)
    {
        var match = Regex.Match(token, @"^([\d.]+)([a-zA-Z]+)$");
        if (!match.Success) return 0;
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0;

        return match.Groups[2].Value.ToUpperInvariant() switch
        {
            "B"  => value / 1024 / 1024,
            "KB" => value / 1024,
            "MB" => value,
            "GB" => value * 1024,
            "TB" => value * 1024 * 1024,
            _    => 0
        };
    }
}
