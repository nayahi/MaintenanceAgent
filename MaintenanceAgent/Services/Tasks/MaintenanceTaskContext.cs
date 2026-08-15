namespace MaintenanceAgent.Services.Tasks;

// Per-task-run context. No DryRun in v1 -- both current tasks shell out to external processes
// with no real preview mode, so a half-implemented dry run would be false confidence.
public sealed record MaintenanceTaskContext(Action<string> Log);
