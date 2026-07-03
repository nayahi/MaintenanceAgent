using MaintenanceAgent.Models;

namespace MaintenanceAgent.Services;

// Turns raw run history into a compact text block the AI can use to prioritize
// recommendations that have actually paid off before, rather than repeating
// advice that's never acted on.
public static class InsightsBuilder
{
    private const int MaxCategoriesShown = 8;
    private const int MaxLength          = 1500;

    public static string? BuildSummary(IReadOnlyList<RunRecord> history)
    {
        if (history.Count == 0) return null;

        var lines = new List<string>
        {
            $"HISTORICAL DATA FROM {history.Count} PAST RUN(S) (oldest to newest):",
            $"C: free space after each run: {string.Join(" -> ", history.Select(r => $"{r.DriveFreeGBAfter:0.#}GB"))}"
        };

        var byLabel = history
            .SelectMany(r => r.Categories.Select(c => (Run: r, Category: c)))
            .GroupBy(x => x.Category.Label)
            .Select(group =>
            {
                var cleaned          = group.Where(x => x.Category.FreedMB is > 0).ToList();
                var avgFreedMB       = cleaned.Count > 0 ? cleaned.Average(x => x.Category.FreedMB!.Value) : 0;
                var recommendedCount = group.Count(x =>
                    x.Run.Advice.Contains(x.Category.Label, StringComparison.OrdinalIgnoreCase));
                return new { Label = group.Key, AvgFreedMB = avgFreedMB, CleanedCount = cleaned.Count, RecommendedCount = recommendedCount };
            })
            .OrderByDescending(c => c.AvgFreedMB)
            .Take(MaxCategoriesShown);

        lines.Add("Category performance (avg MB freed per actual clean, times recommended by AI):");
        foreach (var c in byLabel)
        {
            var cleanedText = c.CleanedCount > 0
                ? $"avg {c.AvgFreedMB:0} MB freed across {c.CleanedCount} clean(s)"
                : "never actually cleaned yet";
            lines.Add($"- {c.Label}: {cleanedText}; recommended {c.RecommendedCount}/{history.Count} run(s)");
        }

        var summary = string.Join('\n', lines);
        return summary.Length > MaxLength ? summary[..MaxLength] + "\n[...truncated...]" : summary;
    }
}
