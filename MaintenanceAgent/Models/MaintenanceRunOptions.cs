namespace MaintenanceAgent.Models;

public sealed record MaintenanceRunOptions(
    bool CleanMode,
    bool DeepClean,
    IReadOnlySet<string> RequestedTaskNames);
