namespace MaintenanceAgent.Services.Tasks;

// Plugin contract for native C# maintenance capabilities (Docker cleanup, OneDrive free-up, ...).
// Deliberately excludes the PS7-script baseline -- MaintenanceOrchestrator calls PowerShellRunner
// directly, since its return shape (ScanResult) doesn't fit this contract without an LSP violation.
// This interface is only for homogeneous, independently-invokable units resolved via DI + Factory.
public interface IMaintenanceTask
{
    // Stable key: matched against --task <name> and used as the Label in the resulting outcome.
    string Name { get; }

    // Shown by --list-tasks.
    string Description { get; }

    // true = risky/destructive, must be explicitly requested via --task <name> or --deep-clean.
    // Never runs under a bare --clean.
    bool IsOptIn { get; }

    Task<MaintenanceTaskOutcome> RunAsync(MaintenanceTaskContext context, CancellationToken ct);
}
