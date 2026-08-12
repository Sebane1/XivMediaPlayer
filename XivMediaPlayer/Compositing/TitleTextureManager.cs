using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using XivMediaPlayer.Localization;

namespace XivMediaPlayer.Compositing
{
    internal class TitleTextureManager : IDisposable
    {
        private readonly ITextureProvider _textureProvider;
        private IDalamudTextureWrap _titleTextureWrap;
        private IDalamudTextureWrap _loadingCompositeWrap;
        private string _lastTitle = "";
        private string _lastStreamer = "";
        private string _lastCompositeStatusMessage = "";
        private long _lastStatusUploadTick;
        private int _cachedTranslationRevision = -1;
        private bool _showingLoadingOverlay;
        private bool _disposed = false;

        // Half-res overlay keeps GPU uploads small; the panel is only a small centered HUD.
        private const int LoadingOverlayWidth = 480;
        private const int LoadingOverlayHeight = 270;
        private const int MinStatusUploadIntervalMs = 750;

        public unsafe IntPtr TextureHandle
        {
            get
            {
                IDalamudTextureWrap wrap = _showingLoadingOverlay
                    ? _loadingCompositeWrap ?? _titleTextureWrap
                    : _titleTextureWrap;

                if (wrap == null) return IntPtr.Zero;
                var handle = wrap.Handle;
                return *(IntPtr*)&handle;
            }
        }

        public TitleTextureManager(ITextureProvider textureProvider)
        {
            _textureProvider = textureProvider;
        }

        public void UpdateText(string title, string streamer)
        {
            if (_disposed) return;

            ClearLoadingOverlay();
            if (title == _lastTitle && streamer == _lastStreamer) return;

            _lastTitle = title ?? "";
            _lastStreamer = streamer ?? "";

            _titleTextureWrap?.Dispose();
            _titleTextureWrap = null;

            if (string.IsNullOrEmpty(_lastTitle)) return;

            string displayText = _lastTitle;
            if (!string.IsNullOrEmpty(_lastStreamer) && _lastStreamer != _lastTitle)
            {
                displayText += $" - {_lastStreamer}";
            }

            const int width = 1920;
            const int height = 1080;

            try
            {
                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var gfx = Graphics.FromImage(bmp))
                {
                    gfx.SmoothingMode = SmoothingMode.HighQuality;
                    gfx.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    gfx.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    gfx.Clear(Color.Transparent);

                    using var font = new Font("Arial", 48, FontStyle.Bold, GraphicsUnit.Pixel);
                    using var brush = new SolidBrush(Color.White);
                    using var shadowBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));

                    var stringFormat = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Near
                    };

                    var rect = new RectangleF(0, 40, width, height);

                    gfx.DrawString(displayText, font, shadowBrush, new RectangleF(2, 42, width, height), stringFormat);
                    gfx.DrawString(displayText, font, shadowBrush, new RectangleF(-2, 38, width, height), stringFormat);
                    gfx.DrawString(displayText, font, shadowBrush, new RectangleF(2, 38, width, height), stringFormat);
                    gfx.DrawString(displayText, font, shadowBrush, new RectangleF(-2, 42, width, height), stringFormat);
                    gfx.DrawString(displayText, font, brush, rect, stringFormat);
                }

                _titleTextureWrap = CreateTextureFromBitmap(bmp, width, height);
            }
            catch (Exception)
            {
            }
        }

        public void InvalidateLoadingCache()
        {
            _lastCompositeStatusMessage = "";
            _lastStatusUploadTick = 0;
            _cachedTranslationRevision = -1;
            _loadingCompositeWrap?.Dispose();
            _loadingCompositeWrap = null;
        }

        /// <summary>
        /// Shows the 3D TV loading HUD. Uploads one small GPU texture when the status text changes.
        /// Bar animation stays on the 2D player window (ImGui); this path avoids per-frame uploads.
        /// </summary>
        public void UpdateLoadingOverlay(string message, int translationRevision = 0)
        {
            if (_disposed) return;

            if (translationRevision != _cachedTranslationRevision)
            {
                _cachedTranslationRevision = translationRevision;
                _lastCompositeStatusMessage = "";
                _lastStatusUploadTick = 0;
            }

            message = string.IsNullOrWhiteSpace(message) ? Translation.Get("Loading video...") : message;

            if (_loadingCompositeWrap != null && message == _lastCompositeStatusMessage)
            {
                _showingLoadingOverlay = true;
                _lastTitle = "";
                _lastStreamer = "";
                return;
            }

            long now = Environment.TickCount64;
            if (_loadingCompositeWrap != null
                && now - _lastStatusUploadTick < MinStatusUploadIntervalMs)
            {
                _showingLoadingOverlay = true;
                return;
            }

            try
            {
                IDalamudTextureWrap? composite = CreateLoadingCompositeTexture(message);
                if (composite == null)
                {
                    return;
                }

                _loadingCompositeWrap?.Dispose();
                _loadingCompositeWrap = composite;
                _lastCompositeStatusMessage = message;
                _lastStatusUploadTick = now;
                _showingLoadingOverlay = true;
                _lastTitle = "";
                _lastStreamer = "";
                _titleTextureWrap?.Dispose();
                _titleTextureWrap = null;
            }
            catch (Exception)
            {
                _showingLoadingOverlay = _loadingCompositeWrap != null;
            }
        }

        private IDalamudTextureWrap? CreateLoadingCompositeTexture(string statusMessage)
        {
            using var bmp = new Bitmap(LoadingOverlayWidth, LoadingOverlayHeight, PixelFormat.Format32bppArgb);
            using (var gfx = Graphics.FromImage(bmp))
            {
                gfx.SmoothingMode = SmoothingMode.HighQuality;
                gfx.PixelOffsetMode = PixelOffsetMode.HighQuality;
                gfx.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                gfx.Clear(Color.Transparent);

                DrawLoadingOverlayChrome(gfx, LoadingOverlayWidth, LoadingOverlayHeight);
                DrawLoadingOverlayStatus(gfx, LoadingOverlayWidth, LoadingOverlayHeight, statusMessage);
            }

            return CreateTextureFromBitmap(bmp, LoadingOverlayWidth, LoadingOverlayHeight);
        }

        private static void DrawLoadingOverlayChrome(Graphics gfx, int width, int height)
        {
            using (var dimBrush = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
            {
                gfx.FillRectangle(dimBrush, 0, 0, width, height);
            }

            float panelWidth = 460f * width / 960f;
            float panelHeight = 110f * height / 540f;
            float panelX = (width - panelWidth) * 0.5f;
            float panelY = (height - panelHeight) * 0.5f - (10f * height / 540f);
            using (var panelBrush = new SolidBrush(Color.FromArgb(210, 24, 24, 28)))
            using (var panelPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1))
            {
                gfx.FillRectangle(panelBrush, panelX, panelY, panelWidth, panelHeight);
                gfx.DrawRectangle(panelPen, panelX, panelY, panelWidth, panelHeight);
            }

            using var titleFont = new Font("Arial", 14, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var centerFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            var titleRect = new RectangleF(panelX + 6, panelY + 7, panelWidth - 12, 18);
            gfx.DrawString(Translation.Get("Loading"), titleFont, textBrush, titleRect, centerFormat);

            float barX = panelX + 20;
            float barY = panelY + panelHeight - 13;
            float barW = panelWidth - 40;
            float barH = 3;
            using (var trackBrush = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
            {
                gfx.FillRectangle(trackBrush, barX, barY, barW, barH);
            }
        }

        private static void DrawLoadingOverlayStatus(Graphics gfx, int width, int height, string statusMessage)
        {
            if (string.IsNullOrWhiteSpace(statusMessage))
            {
                return;
            }

            float panelWidth = 460f * width / 960f;
            float panelHeight = 110f * height / 540f;
            float panelX = (width - panelWidth) * 0.5f;
            float panelY = (height - panelHeight) * 0.5f - (10f * height / 540f);

            using var subFont = new Font("Arial", 9, FontStyle.Regular, GraphicsUnit.Pixel);
            using var subBrush = new SolidBrush(Color.FromArgb(220, 210, 210, 210));
            var centerFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            var subRect = new RectangleF(panelX + 6, panelY + 24, panelWidth - 12, 10);
            gfx.DrawString(statusMessage, subFont, subBrush, subRect, centerFormat);
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

        private void ClearLoadingOverlay()
        {
            _showingLoadingOverlay = false;
            _lastCompositeStatusMessage = "";
            _lastStatusUploadTick = 0;
            _loadingCompositeWrap?.Dispose();
            _loadingCompositeWrap = null;
        }

        public void Dispose()
        {
            _disposed = true;
            ClearLoadingOverlay();
            _titleTextureWrap?.Dispose();
            _titleTextureWrap = null;
        }
    }
}
