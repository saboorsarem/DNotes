using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DNotes.Core
{
    /// <summary>
    /// Every neon primitive in the app funnels through here: glows, hairlines,
    /// corner brackets, circuit grids, scanlines and chevrons. Keeping them in
    /// one place is what stops a dozen windows from each inventing their own
    /// idea of what the style looks like.
    /// </summary>
    internal static class Neon
    {
        private static readonly Color[] _glowCache = new Color[8];

        static Neon()
        {
            // a soft falloff ramp, brightest at the stroke
            for (int i = 0; i < _glowCache.Length; i++)
            {
                double t = 1.0 - (double)i / (_glowCache.Length - 1);
                _glowCache[i] = Color.FromArgb((int)Math.Round(70 * t * t), 255, 255, 255);
            }
        }

        // ------------------------------------------------------------------
        // strokes
        // ------------------------------------------------------------------

        /// <summary>A 1px hairline that stays crisp at any DPI (no half-pixel blur).</summary>
        public static void Hair(Graphics g, Rectangle r, Color c, int inset = 0)
        {
            using (var p = new Pen(Theme.Alpha(c, 190), 1f))
            {
                p.Alignment = PenAlignment.Center;
                g.DrawRectangle(p, r.X + inset + 1, r.Y + inset + 1,
                                     r.Width - inset * 2 - 3, r.Height - inset * 2 - 3);
            }
        }

        public static void VLine(Graphics g, int x, int y0, int y1, Color c, float w = 1f)
        {
            using (var p = new Pen(c, w) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                g.DrawLine(p, x, y0, x, y1);
        }

        public static void HLine(Graphics g, int y, int x0, int x1, Color c, float w = 1f)
        {
            using (var p = new Pen(c, w) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                g.DrawLine(p, x0, y, x1, y);
        }

        // ------------------------------------------------------------------
        // glow
        // ------------------------------------------------------------------

        /// <summary>
        /// Neon bloom. Strokes the shape a few times at increasing width and
        /// falling alpha, which is cheaper and crisper than a real blur.
        /// </summary>
        public static void Glow(Graphics g, Action<Graphics, Pen> stroke, Color tint, int strength = 3)
        {
            for (int i = strength; i >= 1; i--)
            {
                float w = i * 2.6f;
                int a = (int)(34 - i * 5);
                if (a <= 2) continue;
                using (var p = new Pen(Theme.Alpha(tint, a), w))
                {
                    p.LineJoin = LineJoin.Round;
                    stroke(g, p);
                }
            }
            using (var p = new Pen(Theme.Alpha(tint, 235), 1.4f) { LineJoin = LineJoin.Round })
                stroke(g, p);
        }

        public static void GlowRect(Graphics g, Rectangle r, Color tint, int radius, int strength = 3)
        {
            using (var path = RoundRect(r, radius))
                Glow(g, (gg, p) => gg.DrawPath(p, path), tint, strength);
        }

        public static void GlowLine(Graphics g, Point a, Point b, Color tint, float w = 1.4f, int strength = 3)
        {
            Glow(g, (gg, p) => gg.DrawLine(p, a, b), tint, strength);
        }

        // ------------------------------------------------------------------
        // shapes
        // ------------------------------------------------------------------

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius) * 2;
            if (d > r.Width) d = Math.Max(1, r.Width);
            if (d > r.Height) d = Math.Max(1, r.Height);
            var p = new GraphicsPath();
            if (d <= 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill)
        {
            using (var p = RoundRect(r, radius))
            using (var b = new SolidBrush(fill))
                g.FillPath(b, p);
        }

        public static void RoundOutline(Graphics g, Rectangle r, int radius, Color c, float w = 1f)
        {
            using (var p = RoundRect(r, radius))
            using (var pen = new Pen(c, w))
                g.DrawPath(pen, p);
        }

        /// <summary>
        /// The signature frame: four L-shaped corner brackets that sit just
        /// inside the window, like a targeting reticle. This is the one detail
        /// that reads as "cyberpunk" without adding any noise.
        /// </summary>
        public static void Brackets(Graphics g, Rectangle r, int inset, int arm, Color tint, float w = 1.6f)
        {
            int l = r.Left + inset, t = r.Top + inset;
            int rr = r.Right - inset - 1, bb = r.Bottom - inset - 1;
            using (var p = new Pen(tint, w) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
            {
                g.DrawLines(p, new[]
                {
                    new Point(l + arm, t), new Point(l, t), new Point(l, t + arm)
                });
                g.DrawLines(p, new[]
                {
                    new Point(rr - arm, t), new Point(rr, t), new Point(rr, t + arm)
                });
                g.DrawLines(p, new[]
                {
                    new Point(l, bb - arm), new Point(l, bb), new Point(l + arm, bb)
                });
                g.DrawLines(p, new[]
                {
                    new Point(rr, bb - arm), new Point(rr, bb), new Point(rr - arm, bb)
                });
            }
        }

        /// <summary>Faint circuit board grid, clipped to a region.</summary>
        public static void Grid(Graphics g, Rectangle clip, int cell, Color line, float w = 1f)
        {
            if (cell < 6) cell = 6;
            using (var p = new Pen(line, w))
            {
                int x0 = (clip.Left / cell) * cell;
                for (int x = x0; x < clip.Right; x += cell)
                    g.DrawLine(p, x, clip.Top, x, clip.Bottom);
                int y0 = (clip.Top / cell) * cell;
                for (int y = y0; y < clip.Bottom; y += cell)
                    g.DrawLine(p, clip.Left, y, clip.Right, y);
            }
        }

        /// <summary>CRT scanlines. Kept at very low alpha so text stays crisp.</summary>
        public static void Scanlines(Graphics g, Rectangle clip, Color tint, int spacing = 3, int alpha = 12)
        {
            using (var p = new Pen(Theme.Alpha(tint, alpha), 1f))
            {
                for (int y = clip.Top; y < clip.Bottom; y += spacing)
                    g.DrawLine(p, clip.Left, y, clip.Right, y);
            }
        }

        /// <summary>Row of small lit "pixels" - used for status readouts.</summary>
        public static void Pips(Graphics g, int x, int y, int count, int lit, Color on, Color off, int size = 3, int gap = 3)
        {
            for (int i = 0; i < count; i++)
            {
                int px = x + i * (size + gap);
                using (var b = new SolidBrush(i < lit ? on : off))
                    g.FillRectangle(b, px, y, size, size);
            }
        }

        /// <summary>A chevron, for affordances and expanders.</summary>
        public static void Chevron(Graphics g, Point c, int size, Color tint, float w = 1.4f, bool down = true)
        {
            int dy = down ? 1 : -1;
            using (var p = new Pen(tint, w))
            {
                g.DrawLines(p, new[]
                {
                    new Point(c.X - size, c.Y),
                    new Point(c.X, c.Y + dy * size),
                    new Point(c.X + size, c.Y)
                });
            }
        }

        /// <summary>Right-pointing triangle, for list affordances.</summary>
        public static void Triangle(Graphics g, Rectangle r, Color c)
        {
            using (var b = new SolidBrush(c))
                g.FillPolygon(b, new[]
                {
                    new Point(r.Left, r.Top),
                    new Point(r.Right, r.Top + r.Height / 2),
                    new Point(r.Left, r.Bottom)
                });
        }

        /// <summary>Writes small letter-spaced caps, the app's label style.</summary>
        public static void Caps(Graphics g, string text, Font f, Point at, Color c, int tracking = 2)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (var b = new SolidBrush(c))
            {
                float x = at.X;
                foreach (char ch in text.ToUpperInvariant())
                {
                    g.DrawString(ch.ToString(), f, b, x, at.Y);
                    x += g.MeasureString(ch.ToString(), f).Width + tracking;
                }
            }
        }

        public static float CapsWidth(Graphics g, string text, Font f, int tracking = 2)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            float w = 0;
            foreach (char ch in text.ToUpperInvariant())
                w += g.MeasureString(ch.ToString(), f).Width + tracking;
            return w - tracking;
        }

        /// <summary>
        /// Trims text so it measures no wider than maxWidth, appending an ellipsis
        /// when it has to be cut. Lets callers right-align a label without ever
        /// starting the draw outside their own bounds.
        ///
        /// Returns an empty string when not even the ellipsis fits: the ellipsis is
        /// wider than a couple of characters, so on a very narrow gap it has to be
        /// dropped rather than allowed to overflow.
        /// </summary>
        public static string Fit(Graphics g, string text, Font f, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0) return string.Empty;
            if (g.MeasureString(text, f).Width <= maxWidth) return text;

            const string ell = "…";
            if (g.MeasureString(ell, f).Width > maxWidth) return string.Empty;

            int lo = 0, hi = text.Length - 1, best = -1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                string cand = text.Substring(0, mid).TrimEnd() + ell;
                if (g.MeasureString(cand, f).Width <= maxWidth) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return best <= 0 ? ell : text.Substring(0, best).TrimEnd() + ell;
        }

        /// <summary>Dotted "data stream" rule.</summary>
        public static void DashRule(Graphics g, int x0, int x1, int y, Color c, int on = 3, int off = 4, float w = 1f)
        {
            using (var p = new Pen(c, w))
                for (int x = x0; x < x1; x += on + off)
                    g.DrawLine(p, x, y, Math.Min(x + on, x1), y);
        }

        /// <summary>A soft radial bloom behind a rectangle (corner light spill).</summary>
        public static void Spill(Graphics g, Rectangle r, Color tint, int alpha = 40)
        {
            int d = Math.Max(8, Math.Min(r.Width, r.Height));
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r.X - d, r.Y - d, d * 2, d * 2);
                using (var b = new PathGradientBrush(path))
                {
                    b.CenterPoint = new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
                    b.CenterColor = Theme.Alpha(tint, alpha);
                    b.SurroundColors = new[] { Color.FromArgb(0, tint.R, tint.G, tint.B) };
                    g.FillPath(b, path);
                }
            }
        }
    }

    /// <summary>
    /// Rounded window region. Control.Region shadows the Region type name, so
    /// every window goes through here rather than calling Region.FromRegion
    /// directly.
    /// </summary>
    internal static class Shape
    {
        public static void Round(Control c, int radius)
        {
            if (c == null) return;
            using (var path = Neon.RoundRect(new Rectangle(0, 0, c.Width, c.Height), radius))
                c.Region = new Region(path);
        }
    }

    /// <summary>Graphics setup shared by every custom surface.</summary>
    internal static class Gfx
    {        public static Graphics Setup(Graphics g, bool aa = true, bool textAa = true)
        {
            g.SmoothingMode = aa ? SmoothingMode.AntiAlias : SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = textAa
                ? System.Drawing.Text.TextRenderingHint.ClearTypeGridFit
                : System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.CompositingQuality = CompositingQuality.HighQuality;
            return g;
        }

        public static void Clip(Graphics g, Rectangle r, int radius)
        {
            var p = Neon.RoundRect(r, radius);
            g.SetClip(p);
            p.Dispose();
        }
    }

    /// <summary>
    /// A control that paints its own surface. Used for the deck, headers and
    /// status strips. Never used as a parent of the note editor.
    /// </summary>
    internal class NeonPanel : Control
    {
        public NeonPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // painted in OnPaint
        }
    }
}
