using MaintenanceAgent.Models;

namespace MaintenanceAgent.Services;

public static class CliArgs
{
    public static MaintenanceRunOptions Parse(string[] args)
    {
        var deepClean = args.Contains("--deep-clean", StringComparer.OrdinalIgnoreCase);
        var cleanMode = deepClean || args.Contains("--clean", StringComparer.OrdinalIgnoreCase);

        var tasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--task", StringComparison.OrdinalIgnoreCase))
                tasks.Add(args[i + 1]);
        }

        return new MaintenanceRunOptions(cleanMode, deepClean, tasks);
    }
}
