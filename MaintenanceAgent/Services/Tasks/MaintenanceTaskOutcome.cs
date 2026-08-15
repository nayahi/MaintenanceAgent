using MaintenanceAgent.Models;

namespace MaintenanceAgent.Services.Tasks;

// Kept separate from Models.CategoryResult: that record is the PS7 script's RUN_SUMMARY_JSON
// wire-format DTO, and native tasks need fields (Success, ErrorMessage, Log) that don't belong on
// it. ToCategoryResult() is the single, explicit seam that flows this into the existing
// RunSummary/RunRecord/InsightsBuilder/AI-advice/history pipeline unchanged.
public sealed record MaintenanceTaskOutcome(
    string Label,
    string Type,
    double ScannedMB,
    double? FreedMB,
    bool Success,
    string? ErrorMessage = null,
    string? Log = null)
{
    public CategoryResult ToCategoryResult() => new(Label, Type, ScannedMB, FreedMB);
}
