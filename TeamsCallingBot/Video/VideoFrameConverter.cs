namespace TeamsCallingBot.Video
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Drawing.Imaging;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Video frame colour converter between Teams NV12 / RGB24 buffers and System.Drawing.Bitmap,
    /// plus the renderer for the bot's own outgoing "status card" video.
    ///
    /// Performance notes (2026-09-04): the previous implementation used per-pixel floating point
    /// math and a managed intermediate array, costing ~150 ms for a 1080p frame - far too slow to
    /// record screen share as video. The conversions below use fixed-point integer BT.601 math
    /// over raw pointers (AllowUnsafeBlocks is already on in the csproj) and run ~10x faster.
    /// The bot card is rendered once as a static background and only the dynamic parts (status
    /// pill, waveform, timestamp) are painted per frame.
    /// </summary>
    public static class VideoFrameConverter
    {
        private static readonly object CardLock = new object();
        private static Bitmap cachedCardBackground;
        private static string cachedCardBotName;
        private static ImageCodecInfo jpegCodec;

        // ------------------------------------------------------------------
        // NV12 / RGB24 -> Bitmap
        // ------------------------------------------------------------------

        /// <summary>
        /// Converts an NV12 buffer (full-res Y plane followed by interleaved, 2x2 subsampled UV plane)
        /// into a 24bpp BGR Bitmap. <paramref name="stride"/> is the Y-plane stride reported by the
        /// media platform; the UV plane is assumed to use the same stride (this is how the Teams
        /// media SDK lays out its receive buffers).
        /// </summary>
        public static Bitmap ConvertNV12ToBitmap(IntPtr data, int width, int height, int stride)
        {
            if (data == IntPtr.Zero || width <= 0 || height <= 0)
            {
                return null;
            }

            if (stride < width)
            {
                stride = width;
            }

            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    byte* src = (byte*)data.ToPointer();
                    byte* uvPlane = src + ((long)stride * height);
                    byte* dst = (byte*)bmpData.Scan0.ToPointer();

                    for (int y = 0; y < height; y++)
                    {
                        byte* yRow = src + ((long)y * stride);
                        byte* uvRow = uvPlane + ((long)(y >> 1) * stride);
                        byte* dstRow = dst + ((long)y * bmpData.Stride);

                        for (int x = 0; x < width; x++)
                        {
                            int c = yRow[x] - 16;
                            int uvIdx = (x >> 1) << 1;
                            int d = uvRow[uvIdx] - 128;
                            int e = uvRow[uvIdx + 1] - 128;

                            int c298 = 298 * c + 128;
                            int r = (c298 + 409 * e) >> 8;
                            int g = (c298 - 100 * d - 208 * e) >> 8;
                            int b = (c298 + 516 * d) >> 8;

                            byte* px = dstRow + (x * 3);
                            px[0] = (byte)(b < 0 ? 0 : (b > 255 ? 255 : b));
                            px[1] = (byte)(g < 0 ? 0 : (g > 255 ? 255 : g));
                            px[2] = (byte)(r < 0 ? 0 : (r > 255 ? 255 : r));
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            return bmp;
        }

        /// <summary>Managed-array overload used by the recorder after the frame was copied off the media thread.</summary>
        public static Bitmap ConvertNV12ToBitmap(byte[] nv12, int width, int height, int stride)
        {
            if (nv12 == null || nv12.Length == 0)
            {
                return null;
            }

            long required = (long)Math.Max(stride, width) * height * 3 / 2;
            if (nv12.Length < required)
            {
                return null;
            }

            var handle = GCHandle.Alloc(nv12, GCHandleType.Pinned);
            try
            {
                return ConvertNV12ToBitmap(handle.AddrOfPinnedObject(), width, height, stride);
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>Converts an RGB24 (actually BGR byte order, as GDI+ expects) buffer into a 24bpp Bitmap.</summary>
        public static Bitmap ConvertRGB24ToBitmap(IntPtr data, int width, int height, int stride)
        {
            if (data == IntPtr.Zero || width <= 0 || height <= 0)
            {
                return null;
            }

            if (stride < width * 3)
            {
                stride = width * 3;
            }

            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    int rowBytes = width * 3;
                    byte* src = (byte*)data.ToPointer();
                    byte* dst = (byte*)bmpData.Scan0.ToPointer();
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(src + ((long)y * stride), dst + ((long)y * bmpData.Stride), rowBytes, rowBytes);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            return bmp;
        }

        public static Bitmap ConvertRGB24ToBitmap(byte[] rgb, int width, int height, int stride)
        {
            if (rgb == null || rgb.Length < (long)Math.Max(stride, width * 3) * height)
            {
                return null;
            }

            var handle = GCHandle.Alloc(rgb, GCHandleType.Pinned);
            try
            {
                return ConvertRGB24ToBitmap(handle.AddrOfPinnedObject(), width, height, stride);
            }
            finally
            {
                handle.Free();
            }
        }

        // ------------------------------------------------------------------
        // Bitmap -> NV12 (for the bot's outgoing video)
        // ------------------------------------------------------------------

        /// <summary>
        /// Converts a Bitmap to a tightly packed NV12 byte array of exactly width*height*3/2 bytes,
        /// resizing first if the bitmap dimensions differ.
        /// </summary>
        public static byte[] ConvertBitmapToNV12(Bitmap bitmap, int width, int height)
        {
            var nv12 = new byte[width * height * 3 / 2];
            Bitmap source = bitmap;
            bool disposeSource = false;

            if (bitmap.Width != width || bitmap.Height != height || bitmap.PixelFormat != PixelFormat.Format24bppRgb)
            {
                source = ResizeBitmap(bitmap, width, height);
                disposeSource = true;
            }

            try
            {
                var bmpData = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    unsafe
                    {
                        byte* src = (byte*)bmpData.Scan0.ToPointer();
                        fixed (byte* dst = nv12)
                        {
                            byte* yPlane = dst;
                            byte* uvPlane = dst + (width * height);

                            for (int y = 0; y < height; y++)
                            {
                                byte* row = src + ((long)y * bmpData.Stride);
                                byte* yRow = yPlane + (y * width);
                                byte* uvRow = uvPlane + ((y >> 1) * width);
                                bool sampleUv = (y & 1) == 0;

                                for (int x = 0; x < width; x++)
                                {
                                    byte* px = row + (x * 3);
                                    int b = px[0];
                                    int g = px[1];
                                    int r = px[2];

                                    yRow[x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);

                                    if (sampleUv && (x & 1) == 0)
                                    {
                                        int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                                        int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                                        uvRow[x] = (byte)(u < 0 ? 0 : (u > 255 ? 255 : u));
                                        uvRow[x + 1] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
                                    }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    source.UnlockBits(bmpData);
                }
            }
            finally
            {
                if (disposeSource)
                {
                    source.Dispose();
                }
            }

            return nv12;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        public static Bitmap ResizeBitmap(Bitmap source, int width, int height)
        {
            var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(result))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.SmoothingMode = SmoothingMode.HighSpeed;
                g.DrawImage(source, new Rectangle(0, 0, width, height));
            }

            return result;
        }

        /// <summary>Encodes a bitmap as JPEG bytes at the given quality (1-100).</summary>
        public static byte[] EncodeJpeg(Bitmap bitmap, int quality)
        {
            if (bitmap == null)
            {
                return null;
            }

            quality = Math.Max(1, Math.Min(100, quality));
            var codec = GetJpegCodec();
            using (var ms = new MemoryStream())
            {
                if (codec != null)
                {
                    using (var encParams = new EncoderParameters(1))
                    {
                        encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                        bitmap.Save(ms, codec, encParams);
                    }
                }
                else
                {
                    bitmap.Save(ms, ImageFormat.Jpeg);
                }

                return ms.ToArray();
            }
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            if (jpegCodec != null)
            {
                return jpegCodec;
            }

            foreach (var codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid)
                {
                    jpegCodec = codec;
                    break;
                }
            }

            return jpegCodec;
        }

        // ------------------------------------------------------------------
        // Bot status card (outgoing video)
        // ------------------------------------------------------------------

        /// <summary>
        /// Generates the 1280x720 status card the bot streams into the meeting as its video tile.
        /// The heavy static artwork (background, avatar, titles) is rendered once and cached; each
        /// call only paints the dynamic status pill, waveform, timestamp and optional activity line.
        /// </summary>
        public static Bitmap CreateBotStatusCard(string botName, string statusMessage, string meetingId, bool isMuted = false, int tick = 0, string activityLine = null)
        {
            Bitmap background = GetCardBackground(botName ?? "TDA Assistant");
            var bmp = (Bitmap)background.Clone();

            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                int barH = 56;
                int barY = 720 - barH;

                // Screen sharing detection
                bool isScreenSharing = (!string.IsNullOrWhiteSpace(activityLine) && activityLine.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0)
                    || (!string.IsNullOrWhiteSpace(statusMessage) && statusMessage.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0);

                // REC badge in top right
                bool pulse = (tick % 2 == 0);
                Color recDotColor = pulse ? Color.FromArgb(239, 68, 68) : Color.FromArgb(185, 28, 28);
                using (var recBg = new SolidBrush(Color.FromArgb(200, 10, 18, 36)))
                using (var recBrush = new SolidBrush(recDotColor))
                using (var recFont = new Font("Segoe UI", 12, FontStyle.Bold))
                using (var recTextBrush = new SolidBrush(Color.White))
                {
                    g.FillRectangle(recBg, 1150, 16, 110, 32);
                    g.FillEllipse(recBrush, 1162, 26, 12, 12);
                    g.DrawString("REC", recFont, recTextBrush, 1182, 22);
                }

                // Sleek modern bottom status bar
                using (var barBg = new SolidBrush(Color.FromArgb(235, 12, 22, 42)))
                {
                    g.FillRectangle(barBg, 0, barY, 1280, barH);
                }

                // Top accent line on bar
                Color accentColor = isScreenSharing ? Color.FromArgb(56, 189, 248) : Color.FromArgb(16, 185, 129);
                using (var accentPen = new Pen(accentColor, 2f))
                {
                    g.DrawLine(accentPen, 0, barY, 1280, barY);
                }

                // Title & branding on left
                using (var dotBrush = new SolidBrush(isMuted ? Color.FromArgb(239, 68, 68) : accentColor))
                using (var titleFont = new Font("Segoe UI", 13, FontStyle.Bold))
                using (var textBrush = new SolidBrush(Color.White))
                {
                    g.FillEllipse(dotBrush, 24, barY + 20, 14, 14);
                    g.DrawString("TDA ASSISTANT  •  TATA STEEL", titleFont, textBrush, 48, barY + 16);
                }

                // Dynamic Status on right
                string statusText;
                Color statusTextColor;
                if (isMuted)
                {
                    statusText = "● MUTED";
                    statusTextColor = Color.FromArgb(248, 113, 113);
                }
                else if (isScreenSharing)
                {
                    statusText = "● RECORDING SCREEN SHARE (MP4)";
                    statusTextColor = Color.FromArgb(56, 189, 248);
                }
                else
                {
                    statusText = "● ACTIVE & LISTENING | Screen Share & MoM Ready";
                    statusTextColor = Color.FromArgb(52, 211, 153);
                }

                using (var statusFont = new Font("Segoe UI", 11, FontStyle.Bold))
                using (var statusBrush = new SolidBrush(statusTextColor))
                {
                    var sz = g.MeasureString(statusText, statusFont);
                    g.DrawString(statusText, statusFont, statusBrush, 1280 - sz.Width - 24, barY + 18);
                }
            }

            return bmp;
        }

        /// <summary>
        /// Renders a 1280x720 frame that shows an arbitrary content image (chart, TDA answer card,
        /// snapshot, etc.) with a thin title bar, for the bot's outgoing video tile. This is the
        /// "share screen to show visualisation" capability (option a - the bot's video tile displays
        /// content, not a real Teams screen-share) - see BOT_CAPABILITY_EXPECTATIONS.md section 4.
        /// Off by default; CallHandler only calls this while a visualization is explicitly active,
        /// and reverts to <see cref="CreateBotStatusCard"/> otherwise.
        /// </summary>
        public static Bitmap CreateVisualizationFrame(Bitmap contentImage, string title)
        {
            var bmp = new Bitmap(1280, 720, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                using (var bgBrush = new SolidBrush(Color.FromArgb(15, 23, 42)))
                {
                    g.FillRectangle(bgBrush, 0, 0, 1280, 720);
                }

                const int titleBarHeight = 56;
                using (var titleBrush = new SolidBrush(Color.FromArgb(24, 32, 50)))
                {
                    g.FillRectangle(titleBrush, 0, 0, 1280, titleBarHeight);
                }

                using (var titleFont = new Font("Segoe UI", 18, FontStyle.Bold))
                using (var titleTextBrush = new SolidBrush(Color.White))
                {
                    g.DrawString(title ?? "Teams AI Assistant - Visualization", titleFont, titleTextBrush, new PointF(24, 12));
                }

                if (contentImage != null)
                {
                    int areaX = 20;
                    int areaY = titleBarHeight + 20;
                    int areaW = 1280 - (areaX * 2);
                    int areaH = 720 - areaY - 20;

                    float scale = Math.Min((float)areaW / contentImage.Width, (float)areaH / contentImage.Height);
                    int drawW = Math.Max(1, (int)(contentImage.Width * scale));
                    int drawH = Math.Max(1, (int)(contentImage.Height * scale));
                    int drawX = areaX + ((areaW - drawW) / 2);
                    int drawY = areaY + ((areaH - drawH) / 2);

                    using (var frameBrush = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(frameBrush, drawX - 4, drawY - 4, drawW + 8, drawH + 8);
                    }

                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(contentImage, new Rectangle(drawX, drawY, drawW, drawH));
                }
            }

            return bmp;
        }

        private static Bitmap GetCardBackground(string botName)
        {
            lock (CardLock)
            {
                if (cachedCardBackground != null && cachedCardBotName == botName)
                {
                    return cachedCardBackground;
                }

                cachedCardBackground?.Dispose();
                cachedCardBackground = RenderCardBackground(botName);
                cachedCardBotName = botName;
                return cachedCardBackground;
            }
        }

        private static Bitmap RenderCardBackground(string botName)
        {
            var bmp = new Bitmap(1280, 720, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                string[] candidateMascotPaths = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tda_mascot.png"),
                    Path.Combine(Directory.GetCurrentDirectory(), "tda_mascot.png"),
                    @"C:\Users\jaidevlalgame\Downloads\TeamsCallingBot\TeamsCallingBot\TeamsCallingBot\tda_mascot.png",
                    @"C:\Users\jaidevlalgame\.gemini\antigravity-ide\brain\f7426c11-f2b4-4321-b705-cc2ba3f62f2c\.user_uploaded\media_1788939608057.png"
                };

                string mascotPath = candidateMascotPaths.FirstOrDefault(File.Exists);
                int barH = 56;
                int availH = 720 - barH;

                if (!string.IsNullOrEmpty(mascotPath))
                {
                    try
                    {
                        using (var mascot = Image.FromFile(mascotPath))
                        {
                            int srcX = 4;
                            int srcY = 15;
                            int srcW = mascot.Width - 8;
                            int srcH = mascot.Height - 30;

                            float scale = Math.Max(1280f / srcW, (float)availH / srcH);
                            int dstW = (int)(srcW * scale);
                            int dstH = (int)(srcH * scale);
                            int dstX = (1280 - dstW) / 2;
                            int dstY = (availH - dstH) / 2;

                            g.DrawImage(mascot, new Rectangle(dstX, dstY, dstW, dstH), new Rectangle(srcX, srcY, srcW, srcH), GraphicsUnit.Pixel);
                        }
                    }
                    catch
                    {
                        using (var bgBrush = new LinearGradientBrush(new Rectangle(0, 0, 1280, 720), Color.FromArgb(10, 18, 36), Color.FromArgb(20, 32, 58), 45f))
                        {
                            g.FillRectangle(bgBrush, 0, 0, 1280, 720);
                        }
                    }
                }
                else
                {
                    using (var bgBrush = new LinearGradientBrush(new Rectangle(0, 0, 1280, 720), Color.FromArgb(10, 18, 36), Color.FromArgb(20, 32, 58), 45f))
                    {
                        g.FillRectangle(bgBrush, 0, 0, 1280, 720);
                    }
                }
            }

            return bmp;
        }
    }
}
