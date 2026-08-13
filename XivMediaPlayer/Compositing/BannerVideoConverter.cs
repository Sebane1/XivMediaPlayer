using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XivMediaPlayer.Compositing
{
    /// <summary>
    /// Converts short banner video URLs into a cached PNG frame sequence sized for in-world display.
    /// Frames are scaled with Lanczos and stored losslessly at the target resolution (no GIF palette).
    /// </summary>
    internal static class BannerVideoConverter
    {
        // Rough px per world unit — a ~2-unit banner rarely needs a 4K bake.
        private const float PixelsPerWorldUnit = 192f;

        private static readonly Regex FpsRegex = new(@"(?<fps>\d+(?:\.\d+)?)\s*fps\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static int EstimateTargetPixelWidth(float scaleX)
        {
            float worldWidth = scaleX > 0.001f ? scaleX : 2f;
            int pixels = (int)Math.Round(worldWidth * PixelsPerWorldUnit);
            pixels = Math.Clamp(pixels, 64, 2048);
            return pixels - (pixels % 2);
        }

        public static string GetScaledCacheKey(string url, int targetPixelWidth)
        {
            return $"{url}\0{targetPixelWidth}";
        }

        public static bool IsLikelyVideoPayload(ReadOnlySpan<byte> data, string? contentType, string? url)
        {
            if (!string.IsNullOrWhiteSpace(contentType)
                && contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                ReadOnlySpan<char> path = url.AsSpan();
                int query = path.IndexOf('?');
                if (query >= 0) path = path[..query];
                if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (data.Length >= 12
                && data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p')
            {
                return true;
            }

            if (data.Length >= 4
                && data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
            {
                return true;
            }

            return false;
        }

        public static async Task<BannerVideoFrameSet?> EnsureFramesAsync(
            string ffmpegPath,
            string cacheRoot,
            string sourceUrl,
            byte[] sourceBytes,
            int targetPixelWidth)
        {
            if (!File.Exists(ffmpegPath))
            {
                return null;
            }

            string cacheDir = Path.Combine(cacheRoot, HashCacheKey(sourceUrl, targetPixelWidth));
            Directory.CreateDirectory(cacheDir);

            string inputPath = Path.Combine(cacheDir, "source.bin");
            string framePattern = Path.Combine(cacheDir, "frame_%05d.png");

            var existing = Directory.GetFiles(cacheDir, "frame_*.png", SearchOption.TopDirectoryOnly);
            if (existing.Length > 0)
            {
                int delayMs = ReadCachedDelayMs(cacheDir) ?? 33;
                Array.Sort(existing, StringComparer.OrdinalIgnoreCase);
                return new BannerVideoFrameSet(existing, delayMs);
            }

            await File.WriteAllBytesAsync(inputPath, sourceBytes).ConfigureAwait(false);

            double fps = await ProbeAverageFpsAsync(ffmpegPath, inputPath).ConfigureAwait(false);
            if (fps <= 0) fps = 30;

            string args =
                $"-hide_banner -loglevel error -y -i {Quote(inputPath)} " +
                $"-vf scale={targetPixelWidth}:-2:flags=lanczos -vsync 0 {Quote(framePattern)}";

            if (!await RunProcessAsync(ffmpegPath, args).ConfigureAwait(false))
            {
                return null;
            }

            var frames = Directory.GetFiles(cacheDir, "frame_*.png", SearchOption.TopDirectoryOnly);
            if (frames.Length == 0)
            {
                return null;
            }

            Array.Sort(frames, StringComparer.OrdinalIgnoreCase);
            int frameDelayMs = InferDelayMsFromFps(fps);
            WriteCachedDelayMs(cacheDir, frameDelayMs);
            return new BannerVideoFrameSet(frames, frameDelayMs);
        }

        private static int InferDelayMsFromFps(double fps)
        {
            fps = Math.Clamp(fps, 1, 240);
            return Math.Max(1, (int)Math.Round(1000.0 / fps));
        }

        private static async Task<double> ProbeAverageFpsAsync(string ffmpegPath, string inputPath)
        {
            if (!File.Exists(inputPath))
            {
                return 30;
            }

            string args = $"-hide_banner -i {Quote(inputPath)}";
            string stderr = await RunProcessCaptureStderrAsync(ffmpegPath, args).ConfigureAwait(false);
            var match = FpsRegex.Match(stderr);
            if (match.Success
                && double.TryParse(match.Groups["fps"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps))
            {
                return fps;
            }

            return 30;
        }

        private static string HashCacheKey(string url, int targetPixelWidth)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{url}|{targetPixelWidth}"));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static int? ReadCachedDelayMs(string cacheDir)
        {
            string metaPath = Path.Combine(cacheDir, "delay_ms.txt");
            if (!File.Exists(metaPath)) return null;
            string text = File.ReadAllText(metaPath).Trim();
            return int.TryParse(text, out int delayMs) && delayMs > 0 ? delayMs : null;
        }

        private static void WriteCachedDelayMs(string cacheDir, int delayMs)
        {
            File.WriteAllText(Path.Combine(cacheDir, "delay_ms.txt"), delayMs.ToString(CultureInfo.InvariantCulture));
        }

        private static async Task<bool> RunProcessAsync(string exe, string arguments)
        {
            using var process = CreateProcess(exe, arguments);
            if (process == null) return false;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0;
        }

        private static async Task<string> RunProcessCaptureStderrAsync(string exe, string arguments)
        {
            using var process = CreateProcess(exe, arguments, redirectStdErr: true);
            if (process == null) return string.Empty;
            string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return stderr;
        }

        private static Process? CreateProcess(string exe, string arguments, bool redirectStdErr = false)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = redirectStdErr,
                        RedirectStandardOutput = false,
                    }
                };
                process.Start();
                return process;
            }
            catch
            {
                return null;
            }
        }

        private static string Quote(string path) => $"\"{path}\"";
    }

    internal sealed class BannerVideoFrameSet
    {
        public BannerVideoFrameSet(IReadOnlyList<string> framePaths, int frameDelayMs)
        {
            FramePaths = framePaths;
            FrameDelayMs = frameDelayMs;
        }

        public IReadOnlyList<string> FramePaths { get; }
        public int FrameDelayMs { get; }
    }
}
