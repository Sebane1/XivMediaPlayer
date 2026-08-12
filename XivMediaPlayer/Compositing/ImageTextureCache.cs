using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace XivMediaPlayer.Compositing
{
    /// <summary>
    /// Downloads image URLs and uploads them as GPU textures for banners and idle branding.
    /// Animated GIFs are decoded and advanced on the framework thread.
    /// </summary>
    internal sealed class ImageTextureCache : IDisposable
    {
        private const int PropertyTagFrameDelay = 0x5100;

        private readonly ITextureProvider _textureProvider;
        private readonly IPluginLog _log;
        private readonly HttpClient _httpClient = new();
        private readonly ConcurrentDictionary<string, CachedImage> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _loadingLock = new();
        private DateTime _lastAnimationTickUtc = DateTime.UtcNow;
        private bool _disposed;

        private sealed class CachedImage
        {
            public IDalamudTextureWrap? Wrap;
            public int Width;
            public int Height;
            public byte[][]? Frames;
            public int[]? FrameDelaysMs;
            public int CurrentFrameIndex;
            public double AccumulatedMs;
            public readonly object Sync = new();
        }

        public ImageTextureCache(ITextureProvider textureProvider, IPluginLog log)
        {
            _textureProvider = textureProvider;
            _log = log;
        }

        public void RequestLoad(string? url)
        {
            if (_disposed || string.IsNullOrWhiteSpace(url)) return;

            if (_cache.ContainsKey(url)) return;

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

            if (string.IsNullOrWhiteSpace(url)) return false;
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
            if (string.IsNullOrWhiteSpace(url)) return;
            if (_cache.TryRemove(url, out var cached))
            {
                lock (cached.Sync)
                {
                    cached.Wrap?.Dispose();
                    cached.Wrap = null;
                }
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
            byte[][]? frames = cached.Frames;
            int[]? delays = cached.FrameDelaysMs;
            if (frames == null || delays == null || frames.Length <= 1) return;

            lock (cached.Sync)
            {
                cached.AccumulatedMs += deltaMs;
                int delayMs = delays[cached.CurrentFrameIndex];
                if (cached.AccumulatedMs < delayMs) return;

                do
                {
                    cached.AccumulatedMs -= delayMs;
                    cached.CurrentFrameIndex = (cached.CurrentFrameIndex + 1) % frames.Length;
                    delayMs = delays[cached.CurrentFrameIndex];
                } while (cached.AccumulatedMs >= delayMs && frames.Length > 1);

                SetCurrentFrameTexture(cached, frames[cached.CurrentFrameIndex]);
            }
        }

        private async Task LoadInternalAsync(string url)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;

                if (TryLoadAnimatedGif(ms, out byte[][] frames, out int[] delaysMs, out int width, out int height))
                {
                    var wrap = CreateTextureFromRaw(frames[0], width, height);
                    if (wrap == null) return;

                    var cached = new CachedImage
                    {
                        Wrap = wrap,
                        Width = width,
                        Height = height,
                        Frames = frames,
                        FrameDelaysMs = delaysMs,
                        CurrentFrameIndex = 0,
                        AccumulatedMs = 0
                    };

                    if (_cache.TryGetValue(url, out var existing))
                    {
                        lock (existing.Sync)
                        {
                            existing.Wrap?.Dispose();
                        }
                    }

                    _cache[url] = cached;
                    return;
                }

                ms.Position = 0;
                using var bitmap = new Bitmap(ms);
                width = bitmap.Width;
                height = bitmap.Height;
                if (width <= 0 || height <= 0) return;

                var staticWrap = CreateTextureFromBitmap(bitmap, width, height);
                if (staticWrap == null) return;

                if (_cache.TryGetValue(url, out var existingStatic))
                {
                    lock (existingStatic.Sync)
                    {
                        existingStatic.Wrap?.Dispose();
                    }
                }

                _cache[url] = new CachedImage
                {
                    Wrap = staticWrap,
                    Width = width,
                    Height = height
                };
            }
            catch (Exception ex)
            {
                _log.Warning(ex, $"Failed to load image texture: {url}");
            }
        }

        private static bool TryLoadAnimatedGif(
            MemoryStream ms,
            out byte[][] frames,
            out int[] delaysMs,
            out int width,
            out int height)
        {
            frames = Array.Empty<byte[]>();
            delaysMs = Array.Empty<int>();
            width = 0;
            height = 0;

            ms.Position = 0;
            using var image = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            width = image.Width;
            height = image.Height;
            if (width <= 0 || height <= 0) return false;

            int frameCount;
            try
            {
                frameCount = image.GetFrameCount(FrameDimension.Time);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (frameCount <= 1) return false;

            delaysMs = ReadGifFrameDelays(image, frameCount);
            frames = new byte[frameCount][];

            for (int i = 0; i < frameCount; i++)
            {
                image.SelectActiveFrame(FrameDimension.Time, i);
                using var frameBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(frameBitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.DrawImage(image, new Rectangle(0, 0, width, height));
                }

                frames[i] = ExtractBgraFromBitmap(frameBitmap, width, height);
            }

            return true;
        }

        private static int[] ReadGifFrameDelays(Image image, int frameCount)
        {
            var delays = new int[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                delays[i] = 100;
            }

            try
            {
                var delayItem = image.GetPropertyItem(PropertyTagFrameDelay);
                for (int i = 0; i < frameCount; i++)
                {
                    int centiseconds = delayItem.Value.Length >= (i + 1) * 2
                        ? BitConverter.ToUInt16(delayItem.Value, i * 2)
                        : 1;
                    delays[i] = Math.Max(centiseconds * 10, 20);
                }
            }
            catch (ArgumentException)
            {
            }

            return delays;
        }

        private void SetCurrentFrameTexture(CachedImage cached, byte[] frameData)
        {
            cached.Wrap?.Dispose();
            cached.Wrap = CreateTextureFromRaw(frameData, cached.Width, cached.Height);
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

        private IDalamudTextureWrap? CreateTextureFromBitmap(Bitmap bmp, int width, int height)
        {
            return CreateTextureFromRaw(ExtractBgraFromBitmap(bmp, width, height), width, height);
        }

        private static byte[] ExtractBgraFromBitmap(Bitmap bmp, int width, int height)
        {
            var bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int bytes = Math.Abs(bmpData.Stride) * bmp.Height;
                var rawData = new byte[bytes];
                Marshal.Copy(bmpData.Scan0, rawData, 0, bytes);
                return rawData;
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var entry in _cache.Values)
            {
                lock (entry.Sync)
                {
                    entry.Wrap?.Dispose();
                }
            }

            _cache.Clear();
            _httpClient.Dispose();
        }
    }
}
