using System.Text.Json;
using MaintenanceAgent.Models;

namespace MaintenanceAgent.Services;

// Append-only run history (one JSON object per line) so past recommendations
// and their actual results can be compared across runs.
public class HistoryStore
{
    private readonly string _path;

    public HistoryStore(string reportDir)
    {
        _path = Path.Combine(reportDir, "history.jsonl");
    }

    public void AppendRun(RunRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.AppendAllText(_path, JsonSerializer.Serialize(record) + Environment.NewLine, System.Text.Encoding.UTF8);
    }

    public List<RunRecord> LoadRecent(int count = 10) => LoadAll().TakeLast(count).ToList();

    public List<RunRecord> LoadAll()
    {
        if (!File.Exists(_path)) return [];

        var records = new List<RunRecord>();
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<RunRecord>(line);
                if (record != null) records.Add(record);
            }
            catch (JsonException)
            {
                // Skip a malformed line (e.g. truncated by a crash mid-write) rather than failing the whole load
            }
        }

        return records;
    }
}
