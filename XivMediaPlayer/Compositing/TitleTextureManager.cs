using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace XivMediaPlayer.Compositing
{
    internal class TitleTextureManager : IDisposable
    {
        private readonly ITextureProvider _textureProvider;
        private IDalamudTextureWrap _textureWrap;
        private string _lastTitle = "";
        private string _lastStreamer = "";
        private string _lastLoadingMessage = "";
        private int _lastLoadingPulseStep = -1;
        private DateTime _lastLoadingOverlayRebuildUtc = DateTime.MinValue;
        private byte[] _loadingRawBuffer = Array.Empty<byte>();
        private bool _disposed = false;

        private const int LoadingOverlayWidth = 960;
        private const int LoadingOverlayHeight = 540;
        private const int LoadingPulseSteps = 8;
        private const double MinLoadingOverlayRebuildMs = 250;

        public unsafe IntPtr TextureHandle
        {
            get
            {
                if (_textureWrap == null) return IntPtr.Zero;
                var handle = _textureWrap.Handle;
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
            _lastLoadingMessage = "";
            _lastLoadingPulseStep = -1;
            if (title == _lastTitle && streamer == _lastStreamer) return;

            _lastTitle = title ?? "";
            _lastStreamer = streamer ?? "";

            // Free the old texture if it exists
            _textureWrap?.Dispose();
            _textureWrap = null;

            if (string.IsNullOrEmpty(_lastTitle)) return;

            string displayText = _lastTitle;
            if (!string.IsNullOrEmpty(_lastStreamer) && _lastStreamer != _lastTitle)
            {
                displayText += $" - {_lastStreamer}";
            }

            // We render to a 1920x1080 canvas to match standard 16:9 ratio. 
            // This ensures it maps perfectly 1:1 with the VideoTexture UVs!
            int width = 1920;
            int height = 1080;

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var gfx = Graphics.FromImage(bmp);
            
            // High quality text rendering
            gfx.SmoothingMode = SmoothingMode.HighQuality;
            gfx.PixelOffsetMode = PixelOffsetMode.HighQuality;
            gfx.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            
            // Fully transparent background
            gfx.Clear(Color.Transparent);

            // Use a clean, modern font
            using var font = new Font("Arial", 48, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            using var shadowBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));

            // Measure text to center it at the top
            var stringFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Near
            };

            // Draw at the top, slightly padded
            var rect = new RectangleF(0, 40, width, height);
            
            // Draw a subtle dark shadow/outline for readability against bright videos
            gfx.DrawString(displayText, font, shadowBrush, new RectangleF(2, 42, width, height), stringFormat);
            gfx.DrawString(displayText, font, shadowBrush, new RectangleF(-2, 38, width, height), stringFormat);
            gfx.DrawString(displayText, font, shadowBrush, new RectangleF(2, 38, width, height), stringFormat);
            gfx.DrawString(displayText, font, shadowBrush, new RectangleF(-2, 42, width, height), stringFormat);
            
            // Draw the white text
            gfx.DrawString(displayText, font, brush, rect, stringFormat);

            // Extract the BGRA bytes
            var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int bytes = Math.Abs(bmpData.Stride) * bmp.Height;
                byte[] rawData = new byte[bytes];
                Marshal.Copy(bmpData.Scan0, rawData, 0, bytes);

                // We can just use the BGRA byte array directly!
                _textureWrap = _textureProvider.CreateFromRaw(
                    Dalamud.Interface.Textures.RawImageSpecification.Bgra32(width, height),
                    rawData);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }

        /// <summary>
        /// Renders a centered loading overlay for the world-space TV.
        /// </summary>
        public void UpdateLoadingOverlay(string message, float pulse)
        {
            if (_disposed) return;

            message = string.IsNullOrWhiteSpace(message) ? "Loading video..." : message;
            int pulseStep = (int)(pulse * LoadingPulseSteps);
            bool messageChanged = message != _lastLoadingMessage;
            if (!messageChanged && pulseStep == _lastLoadingPulseStep)
            {
                return;
            }

            // Pulse animates every frame throttle GPU texture rebuilds to avoid OOM.
            if (!messageChanged
                && (DateTime.UtcNow - _lastLoadingOverlayRebuildUtc).TotalMilliseconds < MinLoadingOverlayRebuildMs)
            {
                return;
            }

            _lastLoadingMessage = message;
            _lastLoadingPulseStep = pulseStep;
            _lastLoadingOverlayRebuildUtc = DateTime.UtcNow;
            _lastTitle = "";
            _lastStreamer = "";

            const int width = LoadingOverlayWidth;
            const int height = LoadingOverlayHeight;

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var gfx = Graphics.FromImage(bmp);

            gfx.SmoothingMode = SmoothingMode.HighQuality;
            gfx.PixelOffsetMode = PixelOffsetMode.HighQuality;
            gfx.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            gfx.Clear(Color.Transparent);

            // Dim the full frame so it is obvious the TV is busy.
            using (var dimBrush = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
            {
                gfx.FillRectangle(dimBrush, 0, 0, width, height);
            }

            float panelWidth = 460;
            float panelHeight = 110;
            float panelX = (width - panelWidth) * 0.5f;
            float panelY = (height - panelHeight) * 0.5f - 10;
            using (var panelBrush = new SolidBrush(Color.FromArgb(210, 24, 24, 28)))
            using (var panelPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1))
            {
                gfx.FillRectangle(panelBrush, panelX, panelY, panelWidth, panelHeight);
                gfx.DrawRectangle(panelPen, panelX, panelY, panelWidth, panelHeight);
            }

            using var titleFont = new Font("Arial", 27, FontStyle.Bold, GraphicsUnit.Pixel);
            using var subFont = new Font("Arial", 14, FontStyle.Regular, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var subBrush = new SolidBrush(Color.FromArgb(220, 210, 210, 210));
            var centerFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            var titleRect = new RectangleF(panelX + 12, panelY + 14, panelWidth - 24, 36);
            var subRect = new RectangleF(panelX + 12, panelY + 48, panelWidth - 24, 20);
            gfx.DrawString("Loading", titleFont, textBrush, titleRect, centerFormat);
            gfx.DrawString(message, subFont, subBrush, subRect, centerFormat);

            float barX = panelX + 40;
            float barY = panelY + panelHeight - 26;
            float barW = panelWidth - 80;
            float barH = 6;
            using (var trackBrush = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
            {
                gfx.FillRectangle(trackBrush, barX, barY, barW, barH);
            }

            float segmentW = barW * 0.34f;
            float travel = barW - segmentW;
            float segX = barX + travel * pulse;
            using (var fillBrush = new SolidBrush(Color.FromArgb(255, 79, 195, 247)))
            {
                gfx.FillRectangle(fillBrush, segX, barY, segmentW, barH);
            }

            var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int bytes = Math.Abs(bmpData.Stride) * bmp.Height;
                if (_loadingRawBuffer.Length != bytes)
                {
                    _loadingRawBuffer = new byte[bytes];
                }
                Marshal.Copy(bmpData.Scan0, _loadingRawBuffer, 0, bytes);

                IDalamudTextureWrap? newWrap;
                try
                {
                    newWrap = _textureProvider.CreateFromRaw(
                        Dalamud.Interface.Textures.RawImageSpecification.Bgra32(width, height),
                        _loadingRawBuffer);
                }
                catch (OutOfMemoryException)
                {
                    return;
                }

                _textureWrap?.Dispose();
                _textureWrap = newWrap;
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _textureWrap?.Dispose();
            _textureWrap = null;
            _loadingRawBuffer = Array.Empty<byte>();
        }
    }
}
