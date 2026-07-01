namespace MaintenanceAgent.Models;

public class ScanResult
{
    public DateTime RunAt           { get; init; } = DateTime.Now;
    public string   RawOutput       { get; init; } = string.Empty;
    public string   ReportFilePath  { get; init; } = string.Empty;
    public bool     ScriptSucceeded { get; init; }
    public string?  ErrorMessage    { get; init; }
}
