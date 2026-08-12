using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XivMediaPlayer.Diagnostics
{
    /// <summary>
    /// Watches dalamud.log for new XivMediaPlayer warnings and errors.
    /// </summary>
    public sealed class DalamudLogMonitor
    {
        private static readonly Regex LogLineStart = new(
            @"^\d{2}:\d{2}:\d{2}\.\d{3}\s+\||^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}",
            RegexOptions.Compiled);

        private readonly object _lock = new();
        private readonly List<string> _pendingLines = new();
        private readonly HashSet<string> _seenFingerprints = new(StringComparer.Ordinal);

        private long _filePosition;
        private string? _logPath;

        public int PendingLineCount
        {
            get
            {
                lock (_lock)
                {
                    return _pendingLines.Count;
                }
            }
        }

        public string PendingSummary
        {
            get
            {
                lock (_lock)
                {
                    if (_pendingLines.Count == 0)
                    {
                        return string.Empty;
                    }

                    string last = _pendingLines[^1];
                    return last.Length > 160 ? last[..160] + "..." : last;
                }
            }
        }

        public bool HasPendingReports => PendingLineCount > 0;

        public string? LogFilePath
        {
            get
            {
                EnsureLogPath();
                return _logPath;
            }
        }

        public int ScanForNewIssues()
        {
            EnsureLogPath();
            if (_logPath == null || !File.Exists(_logPath))
            {
                return 0;
            }

            int added = 0;
            try
            {
                using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length < _filePosition)
                {
                    _filePosition = 0;
                    lock (_lock)
                    {
                        _seenFingerprints.Clear();
                    }
                }

                if (_filePosition > stream.Length)
                {
                    _filePosition = 0;
                }

                stream.Seek(_filePosition, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var block = new StringBuilder();
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    block.AppendLine(line);
                }

                _filePosition = stream.Position;
                if (block.Length == 0)
                {
                    return 0;
                }

                added = ParseAndStore(block.ToString());
            }
            catch (IOException)
            {
                // Log may be locked briefly while Dalamud writes.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return added;
        }

        public List<string> GetPendingLinesSnapshot()
        {
            lock (_lock)
            {
                return new List<string>(_pendingLines);
            }
        }

        public void ClearPending()
        {
            lock (_lock)
            {
                _pendingLines.Clear();
            }
        }

        private void EnsureLogPath()
        {
            if (_logPath != null)
            {
                return;
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates =
            {
                Path.Combine(appData, "XIVLauncher", "dalamud.log"),
                Path.Combine(localAppData, "XIVLauncher", "dalamud.log"),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    _logPath = candidate;
                    return;
                }
            }

            _logPath = candidates[0];
        }

        private int ParseAndStore(string text)
        {
            int added = 0;
            var entries = new List<string>();
            var current = new List<string>();

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (LogLineStart.IsMatch(line))
                {
                    if (current.Count > 0)
                    {
                        entries.Add(string.Join(Environment.NewLine, current));
                        current.Clear();
                    }

                    current.Add(line);
                }
                else if (current.Count > 0)
                {
                    current.Add(line);
                }
            }

            if (current.Count > 0)
            {
                entries.Add(string.Join(Environment.NewLine, current));
            }

            lock (_lock)
            {
                foreach (string entry in entries)
                {
                    if (!IsPluginIssue(entry))
                    {
                        continue;
                    }

                    string fingerprint = entry.GetHashCode(StringComparison.Ordinal).ToString();
                    if (!_seenFingerprints.Add(fingerprint))
                    {
                        continue;
                    }

                    _pendingLines.Add(RedactSensitive(entry));
                    added++;
                    if (_pendingLines.Count > 400)
                    {
                        _pendingLines.RemoveRange(0, _pendingLines.Count - 400);
                    }
                }
            }

            return added;
        }

        private static bool IsPluginIssue(string entry)
        {
            if (!entry.Contains("XivMediaPlayer", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IsDiagnosticNoise(entry))
            {
                return false;
            }

            return entry.Contains("| WRN |", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("| ERR |", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("[WRN]", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("[ERR]", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("error", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDiagnosticNoise(string entry)
        {
            return entry.Contains("LocalPlayer is null", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("LocalPlayer not ready yet", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("Long UiBuilder(XivMediaPlayer)", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("[HITCH]", StringComparison.OrdinalIgnoreCase);
        }

        private static string RedactSensitive(string entry)
        {
            // Avoid shipping cookie file contents if they ever appear in logs.
            if (entry.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                && entry.Contains('\t'))
            {
                return "[redacted cookie line]";
            }

            return entry;
        }
    }
}
