using System;
using System.Collections.Generic;

namespace XivMediaPlayer.Networking.Models
{
    public class DiagnosticLogReport
    {
        public string PluginVersion { get; set; } = string.Empty;
        public string PluginInternalName { get; set; } = string.Empty;
        public string PluginAuthor { get; set; } = string.Empty;
        public string? PluginRepoUrl { get; set; }
        public string PluginSource { get; set; } = string.Empty;
        public bool IsTestingRelease { get; set; }
        public string OwnerId { get; set; } = string.Empty;
        public string Trigger { get; set; } = "manual";
        public string? UserNote { get; set; }
        public string Summary { get; set; } = string.Empty;
        public DateTime ClientUtc { get; set; } = DateTime.UtcNow;
        public List<string> LogLines { get; set; } = new();
    }

    public class DiagnosticLogSubmitResult
    {
        public bool Success { get; set; } = true;
        public string ReportId { get; set; } = string.Empty;
        public string SavedPath { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
    }
}
