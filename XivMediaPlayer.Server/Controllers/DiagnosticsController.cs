using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace XivMediaPlayer.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(IConfiguration configuration, ILogger<DiagnosticsController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("logs")]
    public async Task<ActionResult<DiagnosticLogSubmitResult>> SubmitLogs([FromBody] DiagnosticLogReport report)
    {
        if (report.LogLines == null || report.LogLines.Count == 0)
        {
            return BadRequest("No log lines provided.");
        }

        if (report.LogLines.Count > 500)
        {
            report.LogLines = report.LogLines.TakeLast(500).ToList();
        }

        string directory = _configuration["Diagnostics:LogDirectory"] ?? "DiagnosticReports";
        Directory.CreateDirectory(directory);

        string reportId = Guid.NewGuid().ToString("N");
        string ownerToken = SanitizeToken(report.OwnerId);
        string fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{ownerToken}_{reportId}.log";
        string fullPath = Path.Combine(directory, fileName);

        var sb = new StringBuilder();
        sb.AppendLine($"ReportId: {reportId}");
        sb.AppendLine($"ReceivedUtc: {DateTime.UtcNow:O}");
        sb.AppendLine($"ClientUtc: {report.ClientUtc:O}");
        sb.AppendLine($"Trigger: {report.Trigger}");
        sb.AppendLine($"PluginVersion: {report.PluginVersion}");
        sb.AppendLine($"OwnerId: {report.OwnerId}");
        sb.AppendLine($"Summary: {report.Summary}");
        if (!string.IsNullOrWhiteSpace(report.UserNote))
        {
            sb.AppendLine($"UserNote: {report.UserNote}");
        }

        sb.AppendLine(new string('-', 72));
        foreach (string line in report.LogLines)
        {
            sb.AppendLine(line);
            sb.AppendLine();
        }

        await System.IO.File.WriteAllTextAsync(fullPath, sb.ToString(), Encoding.UTF8);
        _logger.LogInformation("Saved diagnostic report {ReportId} to {Path}", reportId, fullPath);

        return Ok(new DiagnosticLogSubmitResult
        {
            ReportId = reportId,
            SavedPath = fileName,
        });
    }

    private static string SanitizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var chars = value.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').Take(16).ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}
