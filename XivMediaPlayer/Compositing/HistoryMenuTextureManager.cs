using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using XivMediaPlayer.Localization;

namespace XivMediaPlayer.Compositing
{
    internal class HistoryHitZone
    {
        public float StartY { get; set; }
        public float EndY { get; set; }
        public MediaHistoryEntry Entry { get; set; }
    }

    internal class HistoryMenuTextureManager : IDisposable
    {
        private readonly ITextureProvider _textureProvider;
        private IDalamudTextureWrap _textureWrap;
        private bool _disposed = false;
        private List<HistoryHitZone> _hitZones = new List<HistoryHitZone>();

        public unsafe IntPtr TextureHandle
        {
            get
            {
                if (_textureWrap == null) return IntPtr.Zero;
                var handle = _textureWrap.Handle;
                return *(IntPtr*)&handle;
            }
        }

        public HistoryMenuTextureManager(ITextureProvider textureProvider)
        {
            _textureProvider = textureProvider;
        }

        public void UpdateHistory(Dictionary<string, MediaHistoryEntry> history)
        {
            if (_disposed) return;

            // Free the old texture if it exists
            _textureWrap?.Dispose();
            _textureWrap = null;
            _hitZones.Clear();

            int width = 1920;
            int height = 1080;

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var gfx = Graphics.FromImage(bmp);
            
            // High quality text rendering
            gfx.SmoothingMode = SmoothingMode.HighQuality;
            gfx.PixelOffsetMode = PixelOffsetMode.HighQuality;
            gfx.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            
            // Fully transparent background for the screen
            gfx.Clear(Color.Transparent);

            // Draw semi-transparent dark frosted glass panel in the center
            float panelWidth = 1400;
            float panelHeight = 850;
            float panelX = (width - panelWidth) / 2f;
            float panelY = (height - panelHeight) / 2f;
            
            using var bgBrush = new SolidBrush(Color.FromArgb(220, 20, 20, 25));
            gfx.FillRectangle(bgBrush, panelX, panelY, panelWidth, panelHeight);

            // Draw a subtle border
            using var borderPen = new Pen(Color.FromArgb(100, 255, 255, 255), 2);
            gfx.DrawRectangle(borderPen, panelX, panelY, panelWidth, panelHeight);

            // Fonts
            using var titleFont = new Font("Arial", 48, FontStyle.Bold, GraphicsUnit.Pixel);
            using var itemFont = new Font("Arial", 32, FontStyle.Bold, GraphicsUnit.Pixel);
            using var progressFont = new Font("Arial", 28, FontStyle.Regular, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var progressBrush = new SolidBrush(Color.FromArgb(200, 200, 200));

            // Draw Header
            gfx.DrawString(Translation.Get("Pick Up Where You Left Off"), titleFont, textBrush, new PointF(panelX + 40, panelY + 40));

            // Draw Line
            gfx.DrawLine(borderPen, panelX + 40, panelY + 110, panelX + panelWidth - 40, panelY + 110);

            // Draw Items
            var sortedHistory = history.Values
                .Where(x => x.TimecodeMs > 5000)
                .OrderByDescending(x => x.LastPlayed)
                .Take(8)
                .ToList();

            float currentY = panelY + 130;
            float itemHeight = 80;

            foreach (var entry in sortedHistory)
            {
                // Background highlight on hover would be cool, but since it's static we just draw a subtle separator
                using var separatorPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1);
                gfx.DrawLine(separatorPen, panelX + 40, currentY + itemHeight, panelX + panelWidth - 40, currentY + itemHeight);

                // Title
                string truncatedTitle = entry.Title;
                if (truncatedTitle.Length > 60) truncatedTitle = truncatedTitle.Substring(0, 57) + "...";
                gfx.DrawString(truncatedTitle, itemFont, textBrush, new PointF(panelX + 60, currentY + 20));

                // Progress
                var ts = TimeSpan.FromMilliseconds(entry.TimecodeMs);
                string progress = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
                
                var format = new StringFormat { Alignment = StringAlignment.Far };
                gfx.DrawString(string.Format(Translation.Get("Left off at {0}"), progress), progressFont, progressBrush, new RectangleF(panelX, currentY + 24, panelWidth - 60, itemHeight), format);

                // Store Hit Zone (translate to 0.0 - 1.0 UV coords)
                _hitZones.Add(new HistoryHitZone
                {
                    StartY = currentY / height,
                    EndY = (currentY + itemHeight) / height,
                    Entry = entry
                });

                currentY += itemHeight;
            }

            if (sortedHistory.Count == 0)
            {
                gfx.DrawString(Translation.Get("Your watch history is empty."), itemFont, progressBrush, new PointF(panelX + 60, currentY + 20));
            }

            // Extract the BGRA bytes
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

        public MediaHistoryEntry? GetItemAtUV(float u, float v)
        {
            // Panel X bounds are panelX / width
            float panelXStart = (1920f - 1400f) / 2f / 1920f;
            float panelXEnd = panelXStart + (1400f / 1920f);

            if (u < panelXStart || u > panelXEnd) return null;

            foreach (var zone in _hitZones)
            {
                if (v >= zone.StartY && v <= zone.EndY)
                {
                    return zone.Entry;
                }
            }

            return null;
        }

        public void Dispose()
        {
            _disposed = true;
            _textureWrap?.Dispose();
            _textureWrap = null;
        }
    }
}
