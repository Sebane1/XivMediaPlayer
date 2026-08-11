namespace XivMediaPlayer.Server;

public class DiagnosticLogReport
{
    public string PluginVersion { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Trigger { get; set; } = "manual";
    public string? UserNote { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DateTime ClientUtc { get; set; } = DateTime.UtcNow;
    public List<string> LogLines { get; set; } = new();
}

public class DiagnosticLogSubmitResult
{
    public string ReportId { get; set; } = string.Empty;
    public string SavedPath { get; set; } = string.Empty;
}
