using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DNotes.Core
{
    /// <summary>
    /// The DNotes palette. One neon green taken from the DANISHYAR emblem, one
    /// cyan for accents, and a near-black blue-green base. Everything the app
    /// draws reads its colours from here so the note window, the edge deck and
    /// the All Notes window stay in the same key.
    /// </summary>
    internal static class Theme
    {
        // ---- base surfaces -------------------------------------------------
        public static readonly Color Void = Color.FromArgb(0x05, 0x08, 0x0B);
        public static readonly Color Base = Color.FromArgb(0x08, 0x0D, 0x12);
        public static readonly Color Surface = Color.FromArgb(0x0C, 0x13, 0x19);
        public static readonly Color SurfaceHi = Color.FromArgb(0x11, 0x1B, 0x23);
        public static readonly Color Grid = Color.FromArgb(0x0F, 0x22, 0x1A);
        public static readonly Color Hairline = Color.FromArgb(0x14, 0x2C, 0x21);

        // ---- type ----------------------------------------------------------
        public static readonly Color Text = Color.FromArgb(0xD2, 0xF5, 0xE4);
        public static readonly Color TextDim = Color.FromArgb(0x6E, 0x93, 0x84);
        public static readonly Color TextFaint = Color.FromArgb(0x44, 0x60, 0x55);

        // ---- neon ----------------------------------------------------------
        public static readonly Color Neon = Color.FromArgb(0x2B, 0xFF, 0x7A);
        public static readonly Color NeonDim = Color.FromArgb(0x14, 0x8A, 0x45);
        public static readonly Color NeonDeep = Color.FromArgb(0x0A, 0x4A, 0x26);
        public static readonly Color Cyan = Color.FromArgb(0x2E, 0xE6, 0xFF);
        public static readonly Color CyanDim = Color.FromArgb(0x11, 0x6E, 0x88);
        public static readonly Color Magenta = Color.FromArgb(0xFF, 0x3D, 0x8F);
        public static readonly Color Amber = Color.FromArgb(0xFF, 0xC2, 0x4D);
        public static readonly Color Danger = Color.FromArgb(0xFF, 0x4D, 0x5E);

        // ---- the six note accents ------------------------------------------
        // Deliberately all sit in the neon family so a deck of six still reads
        // as one palette, but they separate cleanly when stacked on the edge.
        public static readonly Color[] Accents =
        {
            Color.FromArgb(0x2B, 0xFF, 0x7A), // neon green  (default, the logo)
            Color.FromArgb(0x2E, 0xE6, 0xFF), // cyan
            Color.FromArgb(0x7C, 0xFF, 0xB0), // mint
            Color.FromArgb(0xFF, 0x3D, 0x8F), // magenta
            Color.FromArgb(0xFF, 0xC2, 0x4D), // amber
            Color.FromArgb(0x9D, 0x8C, 0xFF), // violet
        };

        public static readonly string[] AccentNames =
            { "GREEN", "CYAN", "MINT", "MAGENTA", "AMBER", "VIOLET" };

        // ---- fonts ---------------------------------------------------------
        // Consolas ships with Windows, so the body gets a real monospace
        // without shipping a typeface. Segoe UI covers the chrome labels.
        public static readonly string MonoFamily = "Consolas";
        public static readonly string UiFamily = "Segoe UI";

        private static readonly Dictionary<string, Font> _cache =
            new Dictionary<string, Font>(StringComparer.Ordinal);

        public static Font Mono(float size, FontStyle style)
        {
            return Get(MonoFamily, size, style);
        }

        public static Font Ui(float size, FontStyle style)
        {
            return Get(UiFamily, size, style);
        }

        /// <summary>
        /// Cached fonts are shared across every window, so callers must never
        /// dispose one. The cache revalidates on each hit: if a font has been
        /// disposed by mistake somewhere, a dead handle would make
        /// Graphics.MeasureString throw "Parameter is not valid" and blank out a
        /// window, so a broken entry is detected and rebuilt instead.
        /// </summary>
        private static Font Get(string family, float size, FontStyle style)
        {
            string key = family + "|" + size.ToString("R") + "|" + style;
            Font f;
            if (_cache.TryGetValue(key, out f) && f != null && Alive(f)) return f;
            try
            {
                f = new Font(family, size, style, GraphicsUnit.Point);
                using (var probe = new Bitmap(1, 1))
                using (Graphics g = Graphics.FromImage(probe))
                {
                    if (g.MeasureString("W", f).Width < 3) f = new Font("Segoe UI", size, style);
                }
            }
            catch
            {
                f = new Font("Segoe UI", size, style);
            }
            _cache[key] = f;
            return f;
        }

        private static bool Alive(Font f)
        {
            try
            {
                float ignored = f.Height;      // touches the underlying handle
                return ignored > 0;
            }
            catch { return false; }
        }

        // ---- colour maths --------------------------------------------------
        public static Color Shift(Color c, int delta)
        {
            int r = Clamp(c.R + delta), g = Clamp(c.G + delta), b = Clamp(c.B + delta);
            return Color.FromArgb(c.A, r, g, b);
        }

        public static Color Alpha(Color c, int a)
        {
            return Color.FromArgb(Clamp(a), c.R, c.G, c.B);
        }

        private static int Clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        public static Color Mix(Color a, Color b, double t)
        {
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t),
                (int)(a.A + (b.A - a.A) * t));
        }

        public static Color ReadableInk(Color bg)
        {
            double l = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            return l > 0.62 ? Color.FromArgb(0x05, 0x0A, 0x08) : Color.FromArgb(0xE8, 0xFF, 0xF2);
        }
    }
}
