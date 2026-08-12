using System;

namespace XivMediaPlayer.Compositing
{
    internal static class ImageUrlUtility
    {
        public static string? Normalize(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            return url.Trim();
        }

        public static bool IsHttpUrl(string? url)
        {
            if (Normalize(url) is not string normalized) return false;

            return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
