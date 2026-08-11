using System;

namespace XivMediaPlayer.Localization
{
    /// <summary>
    /// Prevents user media URLs, file paths, and other dynamic content from being sent to the remote translator.
    /// </summary>
    internal static class TranslationGuard
    {
        private static readonly string[] UrlMarkers =
        {
            "://",
            "youtube.com",
            "youtu.be",
            "twitch.tv",
            "vimeo.com",
            "soundcloud.com",
            "googlevideo.com",
            "m3u8",
            "xivmp-sabr",
            "/watch?v=",
            "&list=",
        };

        public static bool ShouldSkipTranslation(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            string trimmed = text.Trim();

            // UI catalog strings are short; long strings are almost always dynamic status or pasted content.
            if (trimmed.Length > 240)
            {
                return true;
            }

            if (ContainsUrlLikeContent(trimmed))
            {
                return true;
            }

            if (LooksLikeFilePath(trimmed))
            {
                return true;
            }

            if (IsDynamicStatusMessage(trimmed))
            {
                return true;
            }

            return false;
        }

        private static bool ContainsUrlLikeContent(string text)
        {
            foreach (string marker in UrlMarkers)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (Uri.TryCreate(text, UriKind.Absolute, out Uri? absolute)
                && (absolute.Scheme == Uri.UriSchemeHttp
                    || absolute.Scheme == Uri.UriSchemeHttps
                    || absolute.Scheme == "rtmp"
                    || absolute.Scheme == "rtsp"))
            {
                return true;
            }

            return false;
        }

        private static bool LooksLikeFilePath(string text)
        {
            if (text.Contains('\\', StringComparison.Ordinal))
            {
                return true;
            }

            if (text.Length >= 3
                && char.IsLetter(text[0])
                && text[1] == ':'
                && (text[2] == '\\' || text[2] == '/'))
            {
                return true;
            }

            return text.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
                || text.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                || text.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                || text.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDynamicStatusMessage(string text)
        {
            if (text.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase)
                && text.EndsWith("...", StringComparison.Ordinal))
            {
                return true;
            }

            if (text.StartsWith("Using cookies from:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (text.StartsWith("Using cookies from browser:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (text.StartsWith("SABR download ended", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (text.StartsWith("Stream URL resolved", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
    }
}
