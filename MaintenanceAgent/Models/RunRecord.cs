namespace MaintenanceAgent.Models;

// One persisted line in history.jsonl: a run's category results plus the AI
// advice given for it, so future runs can compare recommendations against
// what was actually cleaned.
public record RunRecord(
    DateTime Timestamp,
    bool CleanMode,
    string Model,
    double DriveFreeGBBefore,
    double DriveFreeGBAfter,
    double TotalReclaimableMB,
    List<CategoryResult> Categories,
    string Advice
);
