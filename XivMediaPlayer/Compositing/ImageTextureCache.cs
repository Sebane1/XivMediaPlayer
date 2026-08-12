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
    /// Downloads static image URLs and uploads them as GPU textures for banners and idle branding.
    /// </summary>
    internal sealed class ImageTextureCache : IDisposable
    {
        private readonly ITextureProvider _textureProvider;
        private readonly IPluginLog _log;
        private readonly HttpClient _httpClient = new();
        private readonly ConcurrentDictionary<string, CachedImage> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _loadingLock = new();
        private bool _disposed;

        private sealed class CachedImage
        {
            public IDalamudTextureWrap Wrap;
            public int Width;
            public int Height;
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

            width = cached.Width;
            height = cached.Height;
            var handle = cached.Wrap.Handle;
            srv = *(IntPtr*)&handle;
            return srv != IntPtr.Zero;
        }

        public void Invalidate(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (_cache.TryRemove(url, out var cached))
            {
                cached.Wrap?.Dispose();
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

                using var bitmap = new Bitmap(ms);
                int width = bitmap.Width;
                int height = bitmap.Height;
                if (width <= 0 || height <= 0) return;

                var wrap = CreateTextureFromBitmap(bitmap, width, height);
                if (wrap == null) return;

                if (_cache.TryGetValue(url, out var existing))
                {
                    existing.Wrap?.Dispose();
                }

                _cache[url] = new CachedImage
                {
                    Wrap = wrap,
                    Width = width,
                    Height = height
                };
            }
            catch (Exception ex)
            {
                _log.Warning(ex, $"Failed to load image texture: {url}");
            }
        }

        private IDalamudTextureWrap? CreateTextureFromBitmap(Bitmap bmp, int width, int height)
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

                return _textureProvider.CreateFromRaw(
                    Dalamud.Interface.Textures.RawImageSpecification.Bgra32(width, height),
                    rawData);
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
                entry.Wrap?.Dispose();
            }

            _cache.Clear();
            _httpClient.Dispose();
        }
    }
}
