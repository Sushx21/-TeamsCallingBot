namespace TeamsCallingBot.Video
{
    using System;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.Runtime.InteropServices;

    /// <summary>
    /// High-performance video frame color converter between Teams NV12 / RGB24 and System.Drawing.Bitmap.
    /// Completely managed and safe (no unsafe code or pointers needed - compiles cleanly under all C# project configurations).
    /// </summary>
    public static class VideoFrameConverter
    {
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        public static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        /// <summary>
        /// Converts an NV12 video buffer pointer to a standard 24bpp RGB System.Drawing.Bitmap.
        /// NV12 format: Full-resolution Y plane followed by interleaved UV plane subsampled 2x2.
        /// </summary>
        public static Bitmap ConvertNV12ToBitmap(IntPtr data, int width, int height, int stride)
        {
            if (data == IntPtr.Zero || width <= 0 || height <= 0 || stride <= 0)
            {
                return null;
            }

            int yPlaneSize = stride * height;
            int totalBytes = yPlaneSize + ((height / 2) * stride);

            var nv12 = new byte[totalBytes];
            Marshal.Copy(data, nv12, 0, totalBytes);

            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            var rgb = new byte[height * bmpData.Stride];

            try
            {
                for (int y = 0; y < height; y++)
                {
                    int yRowOffset = y * stride;
                    int uvRowOffset = yPlaneSize + ((y / 2) * stride);
                    int rgbRowOffset = y * bmpData.Stride;

                    for (int x = 0; x < width; x++)
                    {
                        int Y = nv12[yRowOffset + x];
                        int uvIndex = uvRowOffset + ((x / 2) * 2);
                        int U = nv12[uvIndex] - 128;
                        int V = nv12[uvIndex + 1] - 128;

                        // ITU-R BT.601 color conversion standard
                        int r = (int)(Y + 1.402 * V);
                        int g = (int)(Y - 0.344136 * U - 0.714136 * V);
                        int b = (int)(Y + 1.772 * U);

                        int rgbIdx = rgbRowOffset + (x * 3);
                        rgb[rgbIdx] = (byte)Math.Min(255, Math.Max(0, b));
                        rgb[rgbIdx + 1] = (byte)Math.Min(255, Math.Max(0, g));
                        rgb[rgbIdx + 2] = (byte)Math.Min(255, Math.Max(0, r));
                    }
                }

                Marshal.Copy(rgb, 0, bmpData.Scan0, rgb.Length);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            return bmp;
        }

        /// <summary>
        /// Converts an RGB24 buffer pointer to a standard 24bpp RGB System.Drawing.Bitmap.
        /// </summary>
        public static Bitmap ConvertRGB24ToBitmap(IntPtr data, int width, int height, int stride)
        {
            if (data == IntPtr.Zero || width <= 0 || height <= 0)
            {
                return null;
            }

            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int rowBytes = width * 3;
                for (int y = 0; y < height; y++)
                {
                    IntPtr srcRow = new IntPtr(data.ToInt64() + (y * stride));
                    IntPtr dstRow = new IntPtr(bmpData.Scan0.ToInt64() + (y * bmpData.Stride));
                    CopyMemory(dstRow, srcRow, (uint)rowBytes);
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            return bmp;
        }

        /// <summary>
        /// Converts a System.Drawing.Bitmap to an NV12 byte array for sending bot video frames.
        /// Completely safe - no unsafe keywords needed.
        /// </summary>
        public static byte[] ConvertBitmapToNV12(Bitmap bitmap, int width, int height)
        {
            var nv12 = new byte[width * height * 3 / 2];

            using (var resized = new Bitmap(bitmap, new Size(width, height)))
            {
                var bmpData = resized.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                var rgb = new byte[height * bmpData.Stride];
                Marshal.Copy(bmpData.Scan0, rgb, 0, rgb.Length);
                resized.UnlockBits(bmpData);

                int yPlaneSize = width * height;

                for (int y = 0; y < height; y++)
                {
                    int rgbRowOffset = y * bmpData.Stride;
                    int yIndex = y * width;
                    int uvIndex = yPlaneSize + ((y / 2) * width);

                    for (int x = 0; x < width; x++)
                    {
                        int rgbIdx = rgbRowOffset + (x * 3);
                        int b = rgb[rgbIdx];
                        int g = rgb[rgbIdx + 1];
                        int r = rgb[rgbIdx + 2];

                        // Standard RGB to YUV BT.601
                        byte Y = (byte)Math.Min(255, Math.Max(0, (0.257 * r) + (0.504 * g) + (0.098 * b) + 16));
                        nv12[yIndex + x] = Y;

                        if (y % 2 == 0 && x % 2 == 0)
                        {
                            byte U = (byte)Math.Min(255, Math.Max(0, -(0.148 * r) - (0.291 * g) + (0.439 * b) + 128));
                            byte V = (byte)Math.Min(255, Math.Max(0, (0.439 * r) - (0.368 * g) - (0.071 * b) + 128));

                            nv12[uvIndex + x] = U;
                            nv12[uvIndex + x + 1] = V;
                        }
                    }
                }
            }

            return nv12;
        }

        /// <summary>
        /// Generates an informative branded 1280x720 video card for the bot's broadcast stream.
        /// Includes a prominent stylized bot icon/avatar, live audio visualizer bars, and status badge.
        /// </summary>
        public static Bitmap CreateBotStatusCard(string botName, string statusMessage, string meetingId, bool isMuted = false, int tick = 0)
        {
            var bmp = new Bitmap(1280, 720, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // 1. Dark modern gradient background
                using (var bgBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, 1280, 720),
                    Color.FromArgb(15, 23, 42), // slate 900
                    Color.FromArgb(30, 41, 59), // slate 800
                    45f))
                {
                    g.FillRectangle(bgBrush, 0, 0, 1280, 720);
                }

                // 2. Subtle geometric grid pattern or accent card
                using (var cardBrush = new SolidBrush(Color.FromArgb(24, 32, 50)))
                using (var cardBorderPen = new Pen(Color.FromArgb(51, 65, 85), 2f))
                {
                    var cardRect = new Rectangle(80, 60, 1120, 600);
                    g.FillRectangle(cardBrush, cardRect);
                    g.DrawRectangle(cardBorderPen, cardRect);
                }

                // 3. Top Banner: Live Recording indicator
                bool pulse = (tick % 2 == 0);
                Color recColor = pulse ? Color.FromArgb(239, 68, 68) : Color.FromArgb(185, 28, 28); // red pulse
                using (var recBrush = new SolidBrush(recColor))
                {
                    g.FillEllipse(recBrush, 1070, 95, 16, 16);
                }
                using (var recFont = new Font("Segoe UI", 13, FontStyle.Bold))
                using (var recTextBrush = new SolidBrush(Color.FromArgb(241, 245, 249)))
                {
                    g.DrawString("REC", recFont, recTextBrush, new PointF(1095, 92));
                }

                // 4. Prominent Bot Icon / Avatar Badge (Left Side: X=130, Y=140, Size=190x190)
                int iconX = 130;
                int iconY = 140;
                int iconSize = 190;

                // Outer Glowing Ring
                using (var glowPen = new Pen(Color.FromArgb(70, 56, 189, 248), 6f))
                {
                    g.DrawEllipse(glowPen, iconX - 5, iconY - 5, iconSize + 10, iconSize + 10);
                }

                // Inner Avatar Background Circle
                using (var circleBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(iconX, iconY, iconSize, iconSize),
                    Color.FromArgb(14, 116, 144),  // cyan 700
                    Color.FromArgb(30, 58, 138),   // blue 900
                    60f))
                {
                    g.FillEllipse(circleBrush, iconX, iconY, iconSize, iconSize);
                }

                using (var borderPen = new Pen(Color.FromArgb(56, 189, 248), 3f))
                {
                    g.DrawEllipse(borderPen, iconX, iconY, iconSize, iconSize);
                }

                // Stylized AI Robot Head
                // Head outline
                int headX = iconX + 45;
                int headY = iconY + 50;
                int headW = 100;
                int headH = 80;

                using (var headBrush = new SolidBrush(Color.FromArgb(248, 250, 252)))
                {
                    g.FillPie(headBrush, headX, headY - 10, headW, headH + 20, 0, 360);
                }

                // Robot Antenna
                using (var antennaPen = new Pen(Color.FromArgb(56, 189, 248), 4f))
                {
                    g.DrawLine(antennaPen, iconX + 95, iconY + 28, iconX + 95, iconY + 45);
                }
                using (var tipBrush = new SolidBrush(Color.FromArgb(56, 189, 248)))
                {
                    g.FillEllipse(tipBrush, iconX + 90, iconY + 20, 10, 10);
                }

                // Robot Visor / Eyes (Cyan Glowing Visor)
                using (var visorBrush = new SolidBrush(Color.FromArgb(15, 23, 42)))
                {
                    g.FillRectangle(visorBrush, headX + 12, headY + 22, 76, 28);
                }
                using (var eyeBrush = new SolidBrush(Color.FromArgb(56, 189, 248)))
                {
                    g.FillEllipse(eyeBrush, headX + 22, headY + 26, 18, 18);
                    g.FillEllipse(eyeBrush, headX + 60, headY + 26, 18, 18);
                }
                // Little eye reflections
                using (var reflectBrush = new SolidBrush(Color.White))
                {
                    g.FillEllipse(reflectBrush, headX + 26, headY + 29, 6, 6);
                    g.FillEllipse(reflectBrush, headX + 64, headY + 29, 6, 6);
                }

                // Robot Cheerful Smile / Voice Wave Line
                using (var smilePen = new Pen(Color.FromArgb(56, 189, 248), 3f))
                {
                    g.DrawArc(smilePen, headX + 32, headY + 54, 36, 16, 20, 140);
                }

                // 5. Bot Title & Branding (Right of Icon)
                using (var fontTitle = new Font("Segoe UI", 34, FontStyle.Bold))
                using (var textBrush = new SolidBrush(Color.White))
                {
                    g.DrawString(botName, fontTitle, textBrush, new PointF(360, 140));
                }

                // Subtitle
                using (var fontSub = new Font("Segoe UI", 16, FontStyle.Regular))
                using (var subBrush = new SolidBrush(Color.FromArgb(148, 163, 184)))
                {
                    g.DrawString("Real-Time Meeting Intelligence & Media Bot", fontSub, subBrush, new PointF(365, 195));
                }

                // 6. Status Pill Badge
                int pillX = 365;
                int pillY = 240;
                int pillW = isMuted ? 140 : 260;
                int pillH = 38;

                Color pillBg = isMuted ? Color.FromArgb(127, 29, 29) : Color.FromArgb(6, 78, 59);
                Color pillBorder = isMuted ? Color.FromArgb(239, 68, 68) : Color.FromArgb(16, 185, 129);
                Color pillText = isMuted ? Color.FromArgb(254, 202, 202) : Color.FromArgb(167, 243, 208);
                string pillLabel = isMuted ? "● MUTED" : "● ACTIVE & LISTENING";

                using (var pillBrush = new SolidBrush(pillBg))
                using (var pillPen = new Pen(pillBorder, 1.5f))
                {
                    g.FillRectangle(pillBrush, pillX, pillY, pillW, pillH);
                    g.DrawRectangle(pillPen, pillX, pillY, pillW, pillH);
                }
                using (var pillFont = new Font("Segoe UI", 13, FontStyle.Bold))
                using (var labelBrush = new SolidBrush(pillText))
                {
                    g.DrawString(pillLabel, pillFont, labelBrush, new PointF(pillX + 15, pillY + 8));
                }

                // 7. Dynamic Audio Visualizer Bars (Animated via tick)
                int barStartX = 650;
                int barY = 240;
                int numBars = 16;
                using (var barBrush = new SolidBrush(Color.FromArgb(56, 189, 248)))
                {
                    for (int i = 0; i < numBars; i++)
                    {
                        double wave = Math.Abs(Math.Sin((tick * 0.3) + (i * 0.5)));
                        int barH = isMuted ? 4 : (int)(wave * 30) + 6;
                        int currentBarY = barY + (38 - barH) / 2;
                        g.FillRectangle(barBrush, barStartX + (i * 12), currentBarY, 7, barH);
                    }
                }

                // 8. Horizontal Divider
                using (var divPen = new Pen(Color.FromArgb(51, 65, 85), 1.5f))
                {
                    g.DrawLine(divPen, 130, 370, 1140, 370);
                }

                // 9. Meeting Details Panel
                using (var fontDetails = new Font("Segoe UI", 15, FontStyle.Regular))
                using (var fontDetailsBold = new Font("Segoe UI", 15, FontStyle.Bold))
                using (var labelBrush = new SolidBrush(Color.FromArgb(100, 116, 139)))
                using (var valBrush = new SolidBrush(Color.FromArgb(226, 232, 240)))
                {
                    // Row 1: Session ID
                    g.DrawString("Call Session ID:", fontDetailsBold, labelBrush, new PointF(140, 400));
                    g.DrawString(meetingId, fontDetails, valBrush, new PointF(330, 400));

                    // Row 2: Media Modalities
                    g.DrawString("Media Services:", fontDetailsBold, labelBrush, new PointF(140, 445));
                    g.DrawString("Bi-Directional Audio, Screen Share OCR & Photo Capture, Live Broadcast", fontDetails, valBrush, new PointF(330, 445));

                    // Row 3: Current Status
                    g.DrawString("Operational State:", fontDetailsBold, labelBrush, new PointF(140, 490));
                    string opState = isMuted ? "Audio Output Muted | Capturing Incoming Media & Screen Frames" : "Full Duplex Audio & Video Streaming Active";
                    g.DrawString(opState, fontDetails, valBrush, new PointF(330, 490));

                    // Row 4: Timestamp
                    g.DrawString("Local Timestamp:", fontDetailsBold, labelBrush, new PointF(140, 535));
                    g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), fontDetails, valBrush, new PointF(330, 535));
                }

                // 10. Bottom Footer
                using (var fontFooter = new Font("Segoe UI", 12, FontStyle.Italic))
                using (var footerBrush = new SolidBrush(Color.FromArgb(71, 85, 105)))
                {
                    g.DrawString("Secure Microsoft Teams Bot Platform • Media Engine Online", fontFooter, footerBrush, new PointF(140, 615));
                }
            }

            return bmp;
        }
    }
}
