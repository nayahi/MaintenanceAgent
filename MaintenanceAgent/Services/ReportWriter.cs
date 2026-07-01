namespace MaintenanceAgent.Services;

public class ReportWriter
{
    private readonly string _reportDir;

    public ReportWriter(string reportDir = @"C:\Users\nayah\MaintenanceReports")
    {
        _reportDir = reportDir;
        Directory.CreateDirectory(_reportDir);
    }

    public string WriteWeeklyReport(string scanOutput, string aiAdvice, string model)
    {
        var timestamp  = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var reportPath = Path.Combine(_reportDir, $"WeeklyAIReport_{timestamp}.md");

        var content = $"""
            # Weekly Maintenance Report
            **Generated:** {DateTime.Now:dddd, MMMM dd yyyy HH:mm:ss}
            **AI Model:** {model}

            ---

            ## AI Recommendations

            {aiAdvice}

            ---

            ## Raw Scan Output

            ```
            {scanOutput.TrimEnd()}
            ```
            """;

        File.WriteAllText(reportPath, content, System.Text.Encoding.UTF8);
        return reportPath;
    }
}
