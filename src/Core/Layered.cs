using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DNotes.Core
{
    /// <summary>
    /// Per-pixel alpha for a window, via UpdateLayeredWindow.
    ///
    /// This is what lets the floating button be a soft-edged glowing disc rather
    /// than a square or a hard-clipped circle: every pixel's alpha is honoured,
    /// and because the window is layered, the transparent corners pass clicks
    /// through to whatever is underneath.
    ///
    /// The one thing that must be right is the source bitmap's alpha. Windows
    /// composites a layered window as <b>premultiplied</b> ARGB, so handing it
    /// straight (non-premultiplied) pixels makes every semi-transparent edge
    /// pixel too bright and the halo grows a dark ring. <see cref="Push"/>
    /// premultiplies on the way in.
    /// </summary>
    internal static class Layered
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx, cy;
            public SIZE(int x, int y) { cx = x; cy = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x, y;
            public POINT(int x, int y) { this.x = x; this.y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public int biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors0, bmiColors1, bmiColors2;
        }

        private const int ULW_ALPHA = 0x02;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const int BI_RGB = 0;
        private const int DIB_RGB_COLORS = 0;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc,
            ref BITMAPINFO pbmi, int usage, out IntPtr ppvBits, IntPtr hSection, int offset);
        [DllImport("gdi32.dll")] private static extern bool PatBlt(IntPtr hdc, int x, int y,
            int cx, int cy, int rop);

        private const int SRCCOPY = 0x00CC0020;

        /// <summary>
        /// Composites <paramref name="bmp"/> onto the window at its current
        /// screen position with full per-pixel alpha. The caller must already
        /// have given the window a WS_EX_LAYERED style and a matching size.
        /// </summary>
        public static bool Push(Form f, Bitmap bmp)
        {
            if (f == null || bmp == null || !f.IsHandleCreated) return false;
            int w = bmp.Width, h = bmp.Height;

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                var bi = new BITMAPINFO();
                bi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bi.bmiHeader.biWidth = w;
                bi.bmiHeader.biHeight = -h;            // top-down
                bi.bmiHeader.biPlanes = 1;
                bi.bmiHeader.biBitCount = 32;
                bi.bmiHeader.biCompression = BI_RGB;
                IntPtr bits;
                dib = CreateDIBSection(screenDc, ref bi, DIB_RGB_COLORS, out bits,
                                       IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;

                // straight ARGB -> premultiplied BGRA, one row at a time
                var src = bmp.LockBits(new Rectangle(0, 0, w, h),
                                       ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = w * 4;
                    for (int y = 0; y < h; y++)
                    {
                        IntPtr s = new IntPtr(src.Scan0.ToInt64() + (long)y * src.Stride);
                        IntPtr d = new IntPtr(bits.ToInt64() + (long)y * stride);
                        byte[] rowIn = new byte[stride];
                        byte[] rowOut = new byte[stride];
                        System.Runtime.InteropServices.Marshal.Copy(s, rowIn, 0, stride);
                        for (int x = 0; x < w; x++)
                        {
                            byte b0 = rowIn[x * 4 + 0];
                            byte g0 = rowIn[x * 4 + 1];
                            byte r0 = rowIn[x * 4 + 2];
                            byte a0 = rowIn[x * 4 + 3];
                            if (a0 == 0)
                            {
                                rowOut[x * 4 + 0] = 0; rowOut[x * 4 + 1] = 0;
                                rowOut[x * 4 + 2] = 0; rowOut[x * 4 + 3] = 0;
                            }
                            else if (a0 == 255)
                            {
                                rowOut[x * 4 + 0] = b0; rowOut[x * 4 + 1] = g0;
                                rowOut[x * 4 + 2] = r0; rowOut[x * 4 + 3] = 255;
                            }
                            else
                            {
                                // premultiply; the +127 keeps rounding from
                                // making dark halos on mid-alpha pixels
                                rowOut[x * 4 + 0] = (byte)((b0 * a0 + 127) / 255);
                                rowOut[x * 4 + 1] = (byte)((g0 * a0 + 127) / 255);
                                rowOut[x * 4 + 2] = (byte)((r0 * a0 + 127) / 255);
                                rowOut[x * 4 + 3] = a0;
                            }
                        }
                        System.Runtime.InteropServices.Marshal.Copy(rowOut, 0, d, stride);
                    }
                }
                finally { bmp.UnlockBits(src); }

                old = SelectObject(memDc, dib);
                PatBlt(memDc, 0, 0, w, h, SRCCOPY);

                var dst = new POINT(f.Left, f.Top);
                var size = new SIZE(w, h);
                var srcPt = new POINT(0, 0);
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };
                return UpdateLayeredWindow(f.Handle, screenDc, ref dst, ref size,
                                           memDc, ref srcPt, 0, ref blend, ULW_ALPHA);
            }
            catch (Exception ex)
            {
                Log.Write("layered push: " + ex.Message);
                return false;
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>Size of the halo a glow bitmap needs around a disc.</summary>
        public static Rectangle Gutter(int core, int glow)
        {
            int pad = Math.Max(0, (glow - core) / 2);
            return new Rectangle(0, 0, core + pad * 2, core + pad * 2);
        }
    }
}
