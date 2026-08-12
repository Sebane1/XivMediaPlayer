using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace XivMediaPlayer.Compositing
{
    /// <summary>
    /// Downloads image URLs and uploads them as GPU textures for banners and idle branding.
    /// Format detection uses response Content-Type and file signatures, not URL extensions.
    /// Animated GIFs are decoded and advanced on the framework thread.
    /// </summary>
    internal sealed class ImageTextureCache : IDisposable
    {
        private static readonly TimeSpan FailedRetryDelay = TimeSpan.FromSeconds(30);

        private readonly ITextureProvider _textureProvider;
        private readonly IPluginLog _log;
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, CachedImage> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _retryAfterUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _loadingLock = new();
        private DateTime _lastAnimationTickUtc = DateTime.UtcNow;
        private bool _disposed;

        private sealed class CachedImage
        {
            public IDalamudTextureWrap? Wrap;
            public IDalamudTextureWrap[]? FrameWraps;
            public int Width;
            public int Height;
            public int[]? FrameDelaysMs;
            public int CurrentFrameIndex;
            public double AccumulatedMs;
            public readonly object Sync = new();
        }

        public ImageTextureCache(ITextureProvider textureProvider, IPluginLog log)
        {
            _textureProvider = textureProvider;
            _log = log;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XivMediaPlayer/1.0");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("image/*,*/*;q=0.8");
        }

        public void RequestLoad(string? url)
        {
            url = ImageUrlUtility.Normalize(url);
            if (_disposed || url == null) return;

            if (_cache.ContainsKey(url)) return;

            if (_retryAfterUtc.TryGetValue(url, out var retryAfter) && DateTime.UtcNow < retryAfter)
            {
                return;
            }

            lock (_loadingLock)
            {
                if (_loading.Contains(url)) return;
                _loading.Add(url);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadInternalAsync(url);
                }
                finally
                {
                    lock (_loadingLock)
                    {
                        _loading.Remove(url);
                    }
                }
            });
        }

        public unsafe bool TryGetTexture(string? url, out IntPtr srv, out int width, out int height)
        {
            srv = IntPtr.Zero;
            width = 0;
            height = 0;

            url = ImageUrlUtility.Normalize(url);
            if (url == null) return false;
            if (!_cache.TryGetValue(url, out var cached) || cached.Wrap == null) return false;

            lock (cached.Sync)
            {
                if (cached.Wrap == null) return false;

                width = cached.Width;
                height = cached.Height;
                var handle = cached.Wrap.Handle;
                srv = *(IntPtr*)&handle;
                return srv != IntPtr.Zero;
            }
        }

        public void Invalidate(string? url)
        {
            url = ImageUrlUtility.Normalize(url);
            if (url == null) return;

            _retryAfterUtc.TryRemove(url, out _);
            if (_cache.TryRemove(url, out var cached))
            {
                ReleaseGpuResources(cached);
            }
        }

        public void UpdateAnimations()
        {
            if (_disposed || _cache.IsEmpty) return;

            var now = DateTime.UtcNow;
            double deltaMs = (now - _lastAnimationTickUtc).TotalMilliseconds;
            _lastAnimationTickUtc = now;
            if (deltaMs <= 0) return;
            if (deltaMs > 500) deltaMs = 16.67;

            foreach (var cached in _cache.Values)
            {
                AdvanceAnimation(cached, deltaMs);
            }
        }

        private void AdvanceAnimation(CachedImage cached, double deltaMs)
        {
            IDalamudTextureWrap[]? frameWraps = cached.FrameWraps;
            int[]? delays = cached.FrameDelaysMs;
            if (frameWraps == null || delays == null || frameWraps.Length <= 1) return;

            lock (cached.Sync)
            {
                int indexBefore = cached.CurrentFrameIndex;
                cached.AccumulatedMs += deltaMs;
                int delayMs = delays[cached.CurrentFrameIndex];
                if (cached.AccumulatedMs < delayMs) return;

                do
                {
                    cached.AccumulatedMs -= delayMs;
                    cached.CurrentFrameIndex = (cached.CurrentFrameIndex + 1) % frameWraps.Length;
                    delayMs = delays[cached.CurrentFrameIndex];
                } while (cached.AccumulatedMs >= delayMs && frameWraps.Length > 1);

                if (cached.CurrentFrameIndex != indexBefore)
                {
                    cached.Wrap = frameWraps[cached.CurrentFrameIndex];
                }
            }
        }

        private async Task LoadInternalAsync(string url)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType;
                var data = await response.Content.ReadAsByteArrayAsync();
                if (data.Length == 0)
                {
                    MarkLoadFailed(url, "Image URL returned an empty response.");
                    return;
                }

                if (!IsLikelyImagePayload(data, contentType))
                {
                    MarkLoadFailed(url, $"URL did not return an image (Content-Type: {contentType ?? "unknown"}). Use a direct image link.");
                    return;
                }

                if (!TryDecodeImage(data, out var cached) || cached == null)
                {
                    MarkLoadFailed(url, "Download succeeded but the image could not be decoded.");
                    return;
                }

                _retryAfterUtc.TryRemove(url, out _);

                if (_cache.TryGetValue(url, out var existing))
                {
                    ReleaseGpuResources(existing);
                }

                _cache[url] = cached;
            }
            catch (Exception ex)
            {
                MarkLoadFailed(url, ex.Message, ex);
            }
        }

        private void MarkLoadFailed(string url, string message, Exception? ex = null)
        {
            _retryAfterUtc[url] = DateTime.UtcNow.Add(FailedRetryDelay);
            if (ex != null)
            {
                _log.Warning(ex, $"Failed to load image texture: {url} ({message})");
            }
            else
            {
                _log.Warning($"Failed to load image texture: {url} ({message})");
            }
        }

        private static bool IsLikelyImagePayload(byte[] data, string? contentType)
        {
            if (HasKnownImageSignature(data)) return true;

            if (!string.IsNullOrWhiteSpace(contentType)
                && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(contentType)
                && (contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("binary", StringComparison.OrdinalIgnoreCase)))
            {
                return HasKnownImageSignature(data);
            }

            return false;
        }

        private static bool HasKnownImageSignature(byte[] data)
        {
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true;
            if (data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true;
            if (data.Length >= 3 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F') return true;
            if (data.Length >= 12
                && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
                && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            {
                return true;
            }

            return false;
        }

        private bool TryDecodeImage(byte[] data, out CachedImage? cached)
        {
            cached = null;

            try
            {
                using var ms = new MemoryStream(data);
                using var image = Image.Load<Rgba32>(ms);
                if (image.Width <= 0 || image.Height <= 0) return false;

                if (image.Frames.Count > 1)
                {
                    return TryDecodeAnimatedImage(image, out cached);
                }

                var frameData = ExtractBgra32(image);
                var wrap = CreateTextureFromRaw(frameData, image.Width, image.Height);
                if (wrap == null) return false;

                cached = new CachedImage
                {
                    Wrap = wrap,
                    Width = image.Width,
                    Height = image.Height
                };
                return true;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Image decode failed.");
                return false;
            }
        }

        private bool TryDecodeAnimatedImage(Image<Rgba32> image, out CachedImage? cached)
        {
            cached = null;
            int frameCount = image.Frames.Count;
            if (frameCount <= 1) return false;

            int width = image.Width;
            int height = image.Height;
            var frameWraps = new IDalamudTextureWrap[frameCount];
            var delaysMs = new int[frameCount];

            try
            {
                for (int i = 0; i < frameCount; i++)
                {
                    using var frame = image.Frames.CloneFrame(i);
                    var frameData = ExtractBgra32(frame);
                    var wrap = CreateTextureFromRaw(frameData, width, height);
                    if (wrap == null)
                    {
                        ReleaseFrameWraps(frameWraps, i);
                        return false;
                    }

                    frameWraps[i] = wrap;
                    var gifMetadata = image.Frames[i].Metadata.GetGifMetadata();
                    int centiseconds = gifMetadata?.FrameDelay ?? 10;
                    delaysMs[i] = Math.Max(centiseconds * 10, 20);
                }
            }
            catch
            {
                ReleaseFrameWraps(frameWraps, frameWraps.Length);
                throw;
            }

            cached = new CachedImage
            {
                Wrap = frameWraps[0],
                FrameWraps = frameWraps,
                Width = width,
                Height = height,
                FrameDelaysMs = delaysMs,
                CurrentFrameIndex = 0,
                AccumulatedMs = 0
            };
            return true;
        }

        private static void ReleaseFrameWraps(IDalamudTextureWrap?[] frameWraps, int uploadedCount)
        {
            for (int i = 0; i < uploadedCount; i++)
            {
                frameWraps[i]?.Dispose();
                frameWraps[i] = null;
            }
        }

        private static void ReleaseGpuResources(CachedImage cached)
        {
            lock (cached.Sync)
            {
                if (cached.FrameWraps != null)
                {
                    foreach (var wrap in cached.FrameWraps)
                    {
                        wrap?.Dispose();
                    }

                    cached.FrameWraps = null;
                    cached.Wrap = null;
                }
                else
                {
                    cached.Wrap?.Dispose();
                    cached.Wrap = null;
                }

                cached.FrameDelaysMs = null;
            }
        }

        private static byte[] ExtractBgra32(Image<Rgba32> image)
        {
            int width = image.Width;
            int height = image.Height;
            var data = new byte[width * height * 4];

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    int rowOffset = y * width * 4;
                    for (int x = 0; x < row.Length; x++)
                    {
                        ref Rgba32 pixel = ref row[x];
                        int offset = rowOffset + (x * 4);
                        data[offset] = pixel.B;
                        data[offset + 1] = pixel.G;
                        data[offset + 2] = pixel.R;
                        data[offset + 3] = pixel.A;
                    }
                }
            });

            return data;
        }

        private IDalamudTextureWrap? CreateTextureFromRaw(byte[] rawData, int width, int height)
        {
            try
            {
                return _textureProvider.CreateFromRaw(
                    Dalamud.Interface.Textures.RawImageSpecification.Bgra32(width, height),
                    rawData);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to upload image texture frame.");
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var entry in _cache.Values)
            {
                ReleaseGpuResources(entry);
            }

            _cache.Clear();
            _httpClient.Dispose();
        }
    }
}
