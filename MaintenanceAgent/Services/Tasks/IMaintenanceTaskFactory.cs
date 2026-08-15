using MaintenanceAgent.Models;

namespace MaintenanceAgent.Services.Tasks;

public interface IMaintenanceTaskFactory
{
    // Opt-in native tasks that should run for this invocation. The PS7 baseline is not part of
    // this -- MaintenanceOrchestrator calls PowerShellRunner directly, always, first.
    IEnumerable<IMaintenanceTask> CreateTasks(MaintenanceRunOptions options);
}
