using System.Text.Json;
using MaintenanceAgent.Models;

namespace MaintenanceAgent.Services;

// Read-only tools the AI can call mid-conversation for deeper analysis than the fixed
// scan text + insights summary already provide. Deliberately read-only: the model can
// query already-recorded history, never trigger any cleanup or system change itself.
public class MaintenanceTools
{
    private readonly HistoryStore _history;

    public MaintenanceTools(HistoryStore history)
    {
        _history = history;
    }

    public static readonly IReadOnlyList<HfTool> Definitions =
    [
        new HfTool("function", new HfFunctionDef(
            "get_category_history",
            "Get the full historical scan/clean results for one specific cleanup category across all " +
            "recorded runs (not just the recent summary). Use this to check a category's long-term trend " +
            "before recommending or deprioritizing it.",
            new
            {
                type = "object",
                properties = new
                {
                    label = new
                    {
                        type = "string",
                        description = "Exact category Label as it appears in the scan output, e.g. 'npm cache'"
                    }
                },
                required = new[] { "label" }
            })),
        new HfTool("function", new HfFunctionDef(
            "get_disk_space_forecast",
            "Get a trend estimate of C: drive free space based on all recorded run history, to judge how " +
            "urgent cleanup is right now.",
            new { type = "object", properties = new { } }))
    ];

    public string Execute(string toolName, string argumentsJson)
    {
        try
        {
            return toolName switch
            {
                "get_category_history"   => GetCategoryHistory(argumentsJson),
                "get_disk_space_forecast" => GetDiskSpaceForecast(),
                _ => JsonSerializer.Serialize(new { error = $"Unknown tool '{toolName}'" })
            };
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private string GetCategoryHistory(string argumentsJson)
    {
        var args  = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var label = args.TryGetProperty("label", out var l) ? l.GetString() : null;
        if (string.IsNullOrWhiteSpace(label))
            return JsonSerializer.Serialize(new { error = "Missing 'label' argument" });

        var points = _history.LoadAll()
            .SelectMany(r => r.Categories
                .Where(c => string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase))
                .Select(c => new { r.Timestamp, c.ScannedMB, c.FreedMB }))
            .ToList();

        return JsonSerializer.Serialize(new { label, runsFound = points.Count, history = points });
    }

    private string GetDiskSpaceForecast()
    {
        var runs = _history.LoadAll();
        if (runs.Count < 2)
            return JsonSerializer.Serialize(new { note = "Not enough history yet for a trend (need at least 2 runs)." });

        var values       = runs.Select(r => r.DriveFreeGBAfter).ToList();
        var trendPerRun  = (values[^1] - values[0]) / (values.Count - 1);

        return JsonSerializer.Serialize(new
        {
            runsAnalyzed      = values.Count,
            earliestFreeGB    = values[0],
            latestFreeGB      = values[^1],
            avgChangePerRunGB = Math.Round(trendPerRun, 2),
            trend = trendPerRun switch
            {
                < -0.1 => "shrinking -- free space is trending down run over run",
                > 0.1  => "growing -- free space is trending up run over run",
                _      => "roughly stable"
            }
        });
    }
}
