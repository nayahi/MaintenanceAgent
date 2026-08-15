using System.Text.RegularExpressions;

namespace MaintenanceAgent.Services.Tasks;

// Discovers OneDrive account folder(s) via HKCU\Software\Microsoft\OneDrive\Accounts (UserFolder
// value per account -- handles personal + org accounts, e.g. both "OneDrive" and
// "OneDrive - Universidad Hispanoamericana" being present on this machine) and runs
// `attrib +U -P <folder>\* /S /D` on each, which tells the OneDrive sync client to dehydrate
// (mark cloud-only) every file under it. Nothing is deleted -- files stay visible and re-download
// automatically the moment they're opened.
//
// Registry is read via `reg query` (ExternalProcessRunner), not the Microsoft.Win32.Registry
// NuGet package, specifically so this stays consistent with how every other task in this folder
// talks to the OS (shell out via Process) and avoids a TargetFramework change.
public sealed class OneDriveFreeUpTask : IMaintenanceTask
{
    private const string AccountsKey = @"HKCU\Software\Microsoft\OneDrive\Accounts";

    public string Name => "onedrive";
    public string Description =>
        "Marks all files under each discovered OneDrive account folder cloud-only (attrib +U -P /S /D) so OneDrive dehydrates them in the background. Non-destructive; files re-download on open.";
    public bool IsOptIn => true;

    public async Task<MaintenanceTaskOutcome> RunAsync(MaintenanceTaskContext context, CancellationToken ct)
    {
        var folders = await DiscoverOneDriveFoldersAsync(ct);
        if (folders.Count == 0)
        {
            return new MaintenanceTaskOutcome(Name, "Plugin", 0, null, Success: false,
                ErrorMessage: "No OneDrive account folders found in the registry.");
        }

        var attrib = ExternalProcessRunner.FindOnPath("attrib") ?? "attrib";
        var logLines = new List<string>();

        foreach (var folder in folders)
        {
            context.Log($"Unpinning (marking cloud-only) everything under: {folder}");
            var arguments = $"+U -P \"{folder}\\*\" /S /D";
            var (exitCode, _, _) = await ExternalProcessRunner.RunAsync(attrib, arguments, context.Log, ct);
            logLines.Add($"{folder} (exit {exitCode})");
        }

        // FreedMB is deliberately null, not a guess -- OneDrive dehydrates asynchronously in the
        // background after the attribute flip (confirmed this session: took ~2 minutes to plateau
        // on a 48GB/125K-file tree), so there's no synchronous "freed" number honestly knowable
        // here. ScannedMB is 0 for the same reason: measuring the full tree's logical size
        // up front risked being too slow to fit the pipeline's 15-minute budget on a tree this size.
        return new MaintenanceTaskOutcome(Name, "Plugin", 0, null, Success: true,
            Log: $"Unpinned {folders.Count} OneDrive folder(s): {string.Join(", ", logLines)}. Space is reclaimed asynchronously by the OneDrive client over the next few minutes.");
    }

    internal static async Task<List<string>> DiscoverOneDriveFoldersAsync(CancellationToken ct)
    {
        var reg = ExternalProcessRunner.FindOnPath("reg") ?? "reg";
        var (exitCode, stdout, _) = await ExternalProcessRunner.RunAsync(reg, $"query \"{AccountsKey}\" /s", null, ct);
        if (exitCode != 0) return [];

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in stdout.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*UserFolder\s+REG_SZ\s+(.+?)\s*$");
            if (!match.Success) continue;

            var path = match.Groups[1].Value.Trim();
            if (Directory.Exists(path))
                folders.Add(path);
        }

        return folders.ToList();
    }
}
