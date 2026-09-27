using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace DNotes.Core
{
    /// <summary>
    /// Loads the embedded logo art once and hands out ready-to-paint bitmaps.
    /// The neon bolt is used in the note header, the edge deck, the tray and
    /// the All Notes window - it is the mark the user asked to be built around.
    /// </summary>
    internal static class Assets
    {
        private static readonly Dictionary<string, Bitmap> _cache =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        private static Bitmap Load(string name)
        {
            Bitmap bmp;
            if (_cache.TryGetValue(name, out bmp) && bmp != null) return bmp;

            Assembly asm = typeof(Assets).Assembly;
            string res = "DNotes.Assets." + name;
            using (Stream s = asm.GetManifestResourceStream(res))
            {
                if (s == null) return null;
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    ms.Position = 0;
                    // clone so the stream can close
                    using (var tmp = new Bitmap(ms))
                        bmp = new Bitmap(tmp);
                }
            }
            _cache[name] = bmp;
            return bmp;
        }

        public static Bitmap Mark { get { return Load("mark"); } }
        public static Bitmap MarkBold { get { return Load("mark_bold"); } }
        public static Bitmap MarkGlow { get { return Load("mark_glow"); } }
        public static Bitmap Ring { get { return Load("ring"); } }
        public static Bitmap Emblem { get { return Load("emblem"); } }
        public static Bitmap Wordmark { get { return Load("wordmark"); } }

        /// <summary>The floating button's face, already cut to a disc.</summary>
        public static Bitmap Orb { get { return Load("orb"); } }

        private static readonly Dictionary<int, Icon> _icons = new Dictionary<int, Icon>();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon TrayIcon(int size)
        {
            Icon ic;
            if (_icons.TryGetValue(size, out ic) && ic != null) return ic;
            Bitmap bmp = Load("tray_" + size);
            if (bmp == null) return SystemIcons.Application;
            // GetHicon hands back an HICON we own, and Icon.FromHandle does not
            // take ownership of it. Clone so the wrapper owns its own copy, then
            // destroy the original - otherwise every tray size leaks a GDI handle
            // for the life of the process.
            IntPtr tmp = bmp.GetHicon();
            try
            {
                ic = (Icon)Icon.FromHandle(tmp).Clone();
            }
            finally
            {
                DestroyIcon(tmp);
            }
            _icons[size] = ic;
            return ic;
        }

        /// <summary>
        /// Draws the bolt fitted into a square, centred, with optional bloom
        /// underneath. Returns a new bitmap - callers own it.
        /// </summary>
        public static Bitmap MarkTile(int size, bool glow, Color? glowTint = null)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Bitmap mk = MarkBold ?? Mark;
                if (mk == null) return bmp;

                if (glow)
                {
                    Color tint = glowTint ?? Theme.Neon;
                    int gi = (int)(size * 1.9);
                    int go = -(int)(size * 0.45);
                    using (var big = new Bitmap(gi, gi, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (Graphics bg = Graphics.FromImage(big))
                        {
                            bg.InterpolationMode =
                                System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            var src = MarkGlow ?? mk;
                            bg.DrawImage(src, new Rectangle(0, 0, gi, gi));
                        }
                        using (var attr = new System.Drawing.Imaging.ImageAttributes())
                        {
                            var cm = new System.Drawing.Imaging.ColorMatrix();
                            cm.Matrix00 = tint.R / 255f;
                            cm.Matrix11 = tint.G / 255f;
                            cm.Matrix22 = tint.B / 255f;
                            cm.Matrix33 = 0.85f;
                            attr.SetColorMatrix(cm);
                            g.DrawImage(big, new Rectangle(go, go, gi, gi),
                                        0, 0, gi, gi, GraphicsUnit.Pixel, attr);
                        }
                    }
                }

                int inner = (int)(size * 0.94);
                g.InterpolationMode =
                    System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(mk, new Rectangle((size - inner) / 2, (size - inner) / 2, inner, inner));
            }
            return bmp;
        }

        public static void Release()
        {
            foreach (var b in _cache.Values) { if (b != null) b.Dispose(); }
            _cache.Clear();
        }
    }
}
