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
        private bool _disposed = false;

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
            int pulseStep = (int)(pulse * 24);
            if (message == _lastLoadingMessage && pulseStep == _lastLoadingPulseStep)
            {
                return;
            }

            _lastLoadingMessage = message;
            _lastLoadingPulseStep = pulseStep;
            _lastTitle = "";
            _lastStreamer = "";

            _textureWrap?.Dispose();
            _textureWrap = null;

            const int width = 1920;
            const int height = 1080;

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

            float panelWidth = 920;
            float panelHeight = 220;
            float panelX = (width - panelWidth) * 0.5f;
            float panelY = (height - panelHeight) * 0.5f - 20;
            using (var panelBrush = new SolidBrush(Color.FromArgb(210, 24, 24, 28)))
            using (var panelPen = new Pen(Color.FromArgb(180, 255, 255, 255), 2))
            {
                gfx.FillRectangle(panelBrush, panelX, panelY, panelWidth, panelHeight);
                gfx.DrawRectangle(panelPen, panelX, panelY, panelWidth, panelHeight);
            }

            using var titleFont = new Font("Arial", 54, FontStyle.Bold, GraphicsUnit.Pixel);
            using var subFont = new Font("Arial", 28, FontStyle.Regular, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var subBrush = new SolidBrush(Color.FromArgb(220, 210, 210, 210));
            var centerFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            var titleRect = new RectangleF(panelX + 24, panelY + 28, panelWidth - 48, 72);
            var subRect = new RectangleF(panelX + 24, panelY + 96, panelWidth - 48, 40);
            gfx.DrawString("Loading", titleFont, textBrush, titleRect, centerFormat);
            gfx.DrawString(message, subFont, subBrush, subRect, centerFormat);

            float barX = panelX + 80;
            float barY = panelY + panelHeight - 52;
            float barW = panelWidth - 160;
            float barH = 12;
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
                byte[] rawData = new byte[bytes];
                Marshal.Copy(bmpData.Scan0, rawData, 0, bytes);
                _textureWrap = _textureProvider.CreateFromRaw(
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
            _disposed = true;
            _textureWrap?.Dispose();
            _textureWrap = null;
        }
    }
}
