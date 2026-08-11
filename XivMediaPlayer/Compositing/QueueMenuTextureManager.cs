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
    internal class QueueHitZone
    {
        public float StartX { get; set; }
        public float EndX { get; set; }
        public float StartY { get; set; }
        public float EndY { get; set; }
        public string Action { get; set; } = string.Empty;
    }

    internal class QueueMenuTextureManager : IDisposable
    {
        private readonly ITextureProvider _textureProvider;
        private IDalamudTextureWrap? _textureWrap;
        private bool _disposed = false;
        private List<QueueHitZone> _hitZones = new List<QueueHitZone>();

        public unsafe IntPtr TextureHandle
        {
            get
            {
                if (_textureWrap == null) return IntPtr.Zero;
                var handle = _textureWrap.Handle;
                return *(IntPtr*)&handle;
            }
        }

        public QueueMenuTextureManager(ITextureProvider textureProvider)
        {
            _textureProvider = textureProvider;
        }

        public void UpdateQueue(Queue<string> queue, string currentPlayingTitle)
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
            using var subFont = new Font("Arial", 28, FontStyle.Regular, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var subBrush = new SolidBrush(Color.FromArgb(200, 200, 200));
            using var redBrush = new SolidBrush(Color.FromArgb(255, 80, 80));

            // Draw Header
            gfx.DrawString(Translation.Get("Up Next"), titleFont, textBrush, new PointF(panelX + 40, panelY + 40));

            // Top Buttons
            float btnWidth = 250;
            float btnHeight = 60;
            float pasteBtnX = panelX + panelWidth - 40 - btnWidth;
            float pasteBtnY = panelY + 30;

            float clearBtnX = pasteBtnX - btnWidth - 20;
            float clearBtnY = panelY + 30;

            using var btnBgBrush = new SolidBrush(Color.FromArgb(50, 255, 255, 255));
            
            // Paste from Clipboard Button
            gfx.FillRectangle(btnBgBrush, pasteBtnX, pasteBtnY, btnWidth, btnHeight);
            gfx.DrawRectangle(borderPen, pasteBtnX, pasteBtnY, btnWidth, btnHeight);
            gfx.DrawString(Translation.Get("Paste URL"), itemFont, textBrush, new RectangleF(pasteBtnX, pasteBtnY, btnWidth, btnHeight), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            
            _hitZones.Add(new QueueHitZone { StartX = pasteBtnX / width, EndX = (pasteBtnX + btnWidth) / width, StartY = pasteBtnY / height, EndY = (pasteBtnY + btnHeight) / height, Action = "paste" });

            // Clear Queue Button
            gfx.FillRectangle(btnBgBrush, clearBtnX, clearBtnY, btnWidth, btnHeight);
            gfx.DrawRectangle(borderPen, clearBtnX, clearBtnY, btnWidth, btnHeight);
            gfx.DrawString(Translation.Get("Clear Queue"), itemFont, redBrush, new RectangleF(clearBtnX, clearBtnY, btnWidth, btnHeight), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            
            _hitZones.Add(new QueueHitZone { StartX = clearBtnX / width, EndX = (clearBtnX + btnWidth) / width, StartY = clearBtnY / height, EndY = (clearBtnY + btnHeight) / height, Action = "clear" });

            // Draw Line
            gfx.DrawLine(borderPen, panelX + 40, panelY + 110, panelX + panelWidth - 40, panelY + 110);

            float currentY = panelY + 130;
            float itemHeight = 80;

            // Currently Playing
            gfx.DrawString(Translation.Get("Now Playing:"), subFont, subBrush, new PointF(panelX + 40, currentY));
            currentY += 40;
            
            string playingText = string.IsNullOrEmpty(currentPlayingTitle) ? Translation.Get("Nothing") : currentPlayingTitle;
            if (playingText.Length > 70) playingText = playingText.Substring(0, 67) + "...";
            gfx.DrawString(playingText, itemFont, textBrush, new PointF(panelX + 60, currentY));
            currentY += 60;

            gfx.DrawLine(borderPen, panelX + 40, currentY, panelX + panelWidth - 40, currentY);
            currentY += 20;

            var queueList = queue.ToList();
            int limit = Math.Min(queueList.Count, 6);

            for (int i = 0; i < limit; i++)
            {
                using var separatorPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1);
                gfx.DrawLine(separatorPen, panelX + 40, currentY + itemHeight, panelX + panelWidth - 40, currentY + itemHeight);

                string truncatedUrl = queueList[i];
                if (truncatedUrl.Length > 60) truncatedUrl = truncatedUrl.Substring(0, 57) + "...";
                
                gfx.DrawString($"{i + 1}. {truncatedUrl}", itemFont, textBrush, new PointF(panelX + 60, currentY + 20));

                // Draw X button
                float xBtnWidth = 50;
                float xBtnHeight = 50;
                float xBtnX = panelX + panelWidth - 40 - xBtnWidth;
                float xBtnY = currentY + 15;

                gfx.FillRectangle(btnBgBrush, xBtnX, xBtnY, xBtnWidth, xBtnHeight);
                gfx.DrawString("X", itemFont, redBrush, new RectangleF(xBtnX, xBtnY, xBtnWidth, xBtnHeight), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });

                _hitZones.Add(new QueueHitZone { 
                    StartX = xBtnX / width, 
                    EndX = (xBtnX + xBtnWidth) / width, 
                    StartY = xBtnY / height, 
                    EndY = (xBtnY + xBtnHeight) / height, 
                    Action = $"remove:{i}" 
                });

                currentY += itemHeight;
            }

            if (queueList.Count == 0)
            {
                gfx.DrawString(Translation.Get("The queue is empty."), itemFont, subBrush, new PointF(panelX + 60, currentY + 20));
            }
            else if (queueList.Count > limit)
            {
                gfx.DrawString(string.Format(Translation.Get("... and {0} more items."), queueList.Count - limit), subFont, subBrush, new PointF(panelX + 60, currentY + 20));
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

        public string? GetActionAtUV(float u, float v)
        {
            float panelXStart = (1920f - 1400f) / 2f / 1920f;
            float panelXEnd = panelXStart + (1400f / 1920f);
            float panelYStart = (1080f - 850f) / 2f / 1080f;
            float panelYEnd = panelYStart + (850f / 1080f);

            // If click is outside panel, we can return "close" to close the menu
            if (u < panelXStart || u > panelXEnd || v < panelYStart || v > panelYEnd) return "close";

            foreach (var zone in _hitZones)
            {
                if (u >= zone.StartX && u <= zone.EndX && v >= zone.StartY && v <= zone.EndY)
                {
                    return zone.Action;
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
