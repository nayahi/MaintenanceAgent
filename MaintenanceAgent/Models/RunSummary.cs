namespace MaintenanceAgent.Models;

// Structured per-category scan/clean result, parsed from the PS7 script's
// RUN_SUMMARY_JSON sentinel line. FreedMB is null unless -Clean actually ran.
public record CategoryResult(string Label, string Type, double ScannedMB, double? FreedMB);

// Structured counterpart to ScanResult.RawOutput, parsed from the PS7 script's
// RUN_SUMMARY_JSON sentinel line.
public record RunSummary(
    DateTime Timestamp,
    bool CleanMode,
    double DriveFreeGBBefore,
    double DriveFreeGBAfter,
    double TotalReclaimableMB,
    List<CategoryResult> Categories
);
