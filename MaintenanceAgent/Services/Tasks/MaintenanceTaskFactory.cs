using MaintenanceAgent.Models;

namespace MaintenanceAgent.Services.Tasks;

public sealed class MaintenanceTaskFactory : IMaintenanceTaskFactory
{
    private readonly IReadOnlyList<IMaintenanceTask> _registered;

    // IEnumerable<IMaintenanceTask> here is resolved by the DI container from every
    // services.AddSingleton<IMaintenanceTask, X>() registration -- the concrete OCP payoff: adding
    // a third task later is one new class + one new DI line, zero changes to this factory.
    public MaintenanceTaskFactory(IEnumerable<IMaintenanceTask> registered) =>
        _registered = registered.ToList();

    public IEnumerable<IMaintenanceTask> CreateTasks(MaintenanceRunOptions options) =>
        _registered.Where(t => t.IsOptIn && (options.DeepClean || options.RequestedTaskNames.Contains(t.Name)));
}
