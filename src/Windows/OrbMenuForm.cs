using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DNotes.Windows
{
    using Core;

    /// <summary>
    /// The shortcut fan that opens when you tap the floating button: a dimmed
    /// disc, the orb at the centre, and six shortcuts on a hexagonal ring joined
    /// by neon spokes. Drawn as a layered window too, so the backdrop fades out
    /// instead of ending in a hard black square, and so a click on the empty
    /// space between the spokes falls through to the desktop.
    ///
    /// The ring is rotated so it opens away from the nearest screen edge - if
    /// the orb is parked on the right edge you do not want half the menu hanging
    /// off the side of the display.
    /// </summary>
    internal sealed class OrbMenuForm : Form
    {
        private const int DISC = 46;          // node diameter
        private const int PAD = 26;           // room for labels and the rim

        private readonly App _app;
        private readonly OrbForm _orb;

        public sealed class Item
        {
            public string Label;
            public string Glyph;
            public Color Tint;
            public Action Run;
            public Item(string label, string glyph, Color tint, Action run)
            {
                Label = label; Glyph = glyph; Tint = tint; Run = run;
            }
        }

        private readonly List<Item> _items = new List<Item>();
        private readonly List<Point> _centres = new List<Point>();
        private readonly List<Rectangle> _hits = new List<Rectangle>();
        private readonly List<string[]> _spokes = new List<string[]>();  // x0,y0,x1,y1

        private int _win, _disc, _cx, _cy, _ring;
        private int _hover = -1;
        private Bitmap _face;

        public bool IsOpen { get { return Visible; } }
        public int NodeCount { get { return _items.Count; } }

        /// <summary>Node rectangles in screen space, for the self-test.</summary>
        public List<Rectangle> NodeRects
        {
            get
            {
                var l = new List<Rectangle>();
                foreach (Point p in _centres)
                    l.Add(new Rectangle(Left + p.X - _disc / 2, Top + p.Y - _disc / 2, _disc, _disc));
                return l;
            }
        }

        private int S(int v) { return (int)Math.Round(v * Ui.Scale); }

        public OrbMenuForm(App app, OrbForm orb, IEnumerable<Item> items)
        {
            _app = app;
            _orb = orb;
            _items.AddRange(items);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            Text = "DNotes Menu";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _disc = S(DISC);
            _ring = S(112);
            _win = (_ring + _disc / 2 + S(PAD)) * 2;
            _cx = _win / 2;
            _cy = _win / 2;
            Size = new Size(_win, _win);

            Bitmap f = Assets.Orb;
            if (f != null)
            {
                _face = new Bitmap(S(58), S(58), PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(_face))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(f, new Rectangle(0, 0, _face.Width, _face.Height));
                }
            }
        }

        /// <summary>Never take focus off whatever the user is typing in.</summary>
        protected override bool ShowWithoutActivation { get { return true; } }

        /// <summary>
        /// Ask for the style at handle creation rather than patching it on
        /// afterwards - WS_EX_NOACTIVATE only reliably prevents activation when
        /// it is present from the moment the window is born.
        /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW
                            | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        /// <summary>Opens the fan, anchored on the orb, kept fully on screen.</summary>
        public void OpenNear()
        {
            int ox = _orb.Left + _orb.Core / 2;
            int oy = _orb.Top + _orb.Core / 2;
            int x = ox - _win / 2;
            int y = oy - _win / 2;

            var wa = Screen.FromPoint(new Point(ox, oy)).WorkingArea;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;
            if (x + _win > wa.Right) x = wa.Right - _win;
            if (y + _win > wa.Bottom) y = wa.Bottom - _win;

            StartAngle();

            // Position and show with SWP_NOACTIVATE, then hand the foreground
            // straight back if anything disturbed it. Tapping the button is a
            // deliberate act on this UI, so a momentary activation is fine, but
            // the fan must not be left sitting on the user's caret afterwards.
            IntPtr keep = Native.GetForegroundWindow();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, _win, _win,
                                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Show();
            Render();

            if (keep != IntPtr.Zero && Native.GetForegroundWindow() != keep)
                Native.SetForegroundWindow(keep);
        }

        /// <summary>
        /// Rotates the ring so the gap faces the nearest screen edge: whatever is
        /// behind the orb does not need shortcuts, the free side of the screen
        /// does.
        /// </summary>
        private void StartAngle()
        {
            int ox = _orb.Left + _orb.Core / 2;
            int oy = _orb.Top + _orb.Core / 2;
            var wa = Screen.FromPoint(new Point(ox, oy)).WorkingArea;

            double dir;                 // 0 = right, 90 = down, 180 = left, 270 = up
            int dl = Math.Abs(ox - wa.Left), dr = Math.Abs(wa.Right - ox);
            int dt = Math.Abs(oy - wa.Top), db = Math.Abs(wa.Bottom - oy);
            int best = dl; dir = 180;
            if (dr < best) { best = dr; dir = 0; }
            if (dt < best) { best = dt; dir = 270; }
            if (db < best) { best = db; dir = 90; }

            // put the first node opposite the nearest edge, and step around
            _centres.Clear();
            _spokes.Clear();
            int n = _items.Count;
            if (n == 0) return;
            double step = 360.0 / n;
            for (int i = 0; i < n; i++)
            {
                double a = (dir + 180 + i * step) * Math.PI / 180.0;
                int px = _cx + (int)Math.Round(Math.Cos(a) * _ring);
                int py = _cy + (int)Math.Round(Math.Sin(a) * _ring);
                _centres.Add(new Point(px, py));
                _spokes.Add(new string[]
                {
                    _cx.ToString(), _cy.ToString(), px.ToString(), py.ToString()
                });
            }
        }

        // ==================================================================
        // painting
        // ==================================================================

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        public void Render()
        {
            try
            {
                using (var bmp = new Bitmap(_win, _win, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        Gfx.Setup(g);

                        // soft backdrop
                        var rim = new Rectangle(S(6), S(6), _win - S(12), _win - S(12));
                        using (var path = Neon.RoundRect(rim, rim.Width / 2))
                        using (var b = new SolidBrush(Color.FromArgb(206, 5, 9, 12)))
                            g.FillPath(b, path);
                        using (var path = Neon.RoundRect(rim, rim.Width / 2))
                        using (var p = new Pen(Color.FromArgb(120, 43, 255, 122), 1.2f))
                            g.DrawPath(p, path);
                        Neon.Glow(g, (gg, p2) => gg.DrawEllipse(p2, rim.X, rim.Y, rim.Width, rim.Height),
                                  Theme.Neon, 2);

                        // spokes, then the nodes
                        for (int i = 0; i < _spokes.Count; i++)
                        {
                            string[] s = _spokes[i];
                            int x0 = int.Parse(s[0]), y0 = int.Parse(s[1]);
                            int x1 = int.Parse(s[2]), y1 = int.Parse(s[3]);
                            bool hot = i == _hover;
                            using (var p = new Pen(Theme.Alpha(hot ? Theme.Neon : Theme.NeonDim,
                                                               hot ? 210 : 90), hot ? 1.8f : 1f))
                            {
                                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                                g.DrawLine(p, x0, y0, x1, y1);
                            }
                        }

                        for (int i = 0; i < _centres.Count; i++)
                        {
                            DrawNode(g, _centres[i], _items[i], i == _hover);
                        }

                        // the orb again, in the middle
                        if (_face != null)
                            g.DrawImage(_face, new Rectangle(_cx - _face.Width / 2,
                                                             _cy - _face.Height / 2,
                                                             _face.Width, _face.Height));
                    }
                    Layered.Push(this, bmp);
                }
            }
            catch (Exception ex) { Log.Write("orb menu render: " + ex.Message); }
        }

        private void DrawNode(Graphics g, Point c, Item it, bool hot)
        {
            var r = new Rectangle(c.X - _disc / 2, c.Y - _disc / 2, _disc, _disc);
            Color tint = hot ? Theme.Neon : it.Tint;

            if (hot)
                Neon.Glow(g, (gg, p) => gg.DrawEllipse(p, r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2),
                          tint, 3);

            using (var path = Neon.RoundRect(r, r.Width / 2))
            using (var b = new SolidBrush(hot
                       ? Theme.Alpha(Theme.Neon, 60)
                       : Color.FromArgb(232, 10, 16, 21)))
                g.FillPath(b, path);
            using (var path = Neon.RoundRect(r, r.Width / 2))
            using (var pen = new Pen(Theme.Alpha(tint, hot ? 245 : 165), hot ? 1.6f : 1f))
                g.DrawPath(pen, path);

            DrawGlyph(g, c, it.Glyph, hot ? Theme.Neon : Theme.Alpha(tint, 220), hot);

            // label under the node
            var f = Theme.Ui(6.4f, FontStyle.Bold);
            int tw = (int)Neon.CapsWidth(g, it.Label, f, 1);
            int ly = r.Bottom + S(3);
            int lx = c.X - tw / 2;
            // keep the label inside the window
            if (lx < 2) lx = 2;
            if (lx + tw > _win - 2) lx = _win - tw - 2;
            Neon.Caps(g, it.Label, f, new Point(lx, ly), hot ? Theme.Neon : Theme.TextDim, 1);
        }

        /// <summary>Geometric glyphs, so nothing depends on an icon font.</summary>
        private void DrawGlyph(Graphics g, Point c, string glyph, Color col, bool hot)
        {
            int d = S(9);
            using (var p = new Pen(col, hot ? 1.8f : 1.4f)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round
            })
            {
                switch (glyph)
                {
                    case "plus":
                        g.DrawLine(p, c.X - d, c.Y, c.X + d, c.Y);
                        g.DrawLine(p, c.X, c.Y - d, c.X, c.Y + d);
                        break;
                    case "grid":
                        for (int i = -1; i <= 1; i++)
                            for (int j = -1; j <= 1; j++)
                                g.DrawRectangle(p, c.X + i * d - d / 3, c.Y + j * d - d / 3,
                                                d * 2 / 3, d * 2 / 3);
                        break;
                    case "box":                      // archive
                        g.DrawLine(p, c.X - d, c.Y - d, c.X + d, c.Y - d);
                        g.DrawLine(p, c.X - d, c.Y - d, c.X - d, c.Y + d);
                        g.DrawLine(p, c.X + d, c.Y - d, c.X + d, c.Y + d);
                        g.DrawLine(p, c.X - d, c.Y + d, c.X + d, c.Y + d);
                        g.DrawLine(p, c.X - d / 2, c.Y - d + S(3), c.X + d / 2, c.Y - d + S(3));
                        break;
                    case "move":
                        g.DrawLine(p, c.X, c.Y - d, c.X, c.Y + d);
                        g.DrawLines(p, new[]
                        {
                            new Point(c.X - d / 2, c.Y - d + S(3)),
                            new Point(c.X, c.Y - d),
                            new Point(c.X + d / 2, c.Y - d + S(3))
                        });
                        g.DrawLines(p, new[]
                        {
                            new Point(c.X - d / 2, c.Y + d - S(3)),
                            new Point(c.X, c.Y + d),
                            new Point(c.X + d / 2, c.Y + d - S(3))
                        });
                        break;
                    case "eye":
                        g.DrawEllipse(p, c.X - d, c.Y - d / 2, d * 2, d);
                        g.DrawEllipse(p, c.X - d / 3, c.Y - d / 3, d * 2 / 3, d * 2 / 3);
                        break;
                    case "sliders":
                        for (int i = -1; i <= 1; i += 2)
                        {
                            g.DrawLine(p, c.X - d, c.Y + i * d / 2, c.X + d, c.Y + i * d / 2);
                            g.DrawEllipse(p, c.X + i * d / 2 - S(2), c.Y + i * d / 2 - S(2),
                                          S(4), S(4));
                        }
                        break;
                    case "power":
                        g.DrawArc(p, c.X - d, c.Y - d, d * 2, d * 2, -70, 140);
                        g.DrawLine(p, c.X, c.Y - d, c.X, c.Y - S(1));
                        break;
                    default:
                        g.DrawEllipse(p, c.X - d / 2, c.Y - d / 2, d, d);
                        break;
                }
            }
        }

        // ==================================================================
        // input
        // ==================================================================

        private int NodeAt(Point p)
        {
            for (int i = 0; i < _centres.Count; i++)
            {
                var r = new Rectangle(_centres[i].X - _disc / 2, _centres[i].Y - _disc / 2, _disc, _disc);
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = NodeAt(e.Location);
            if (h != _hover)
            {
                _hover = h;
                Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
                Render();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Render(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { Close(); return; }
            if (e.Button != MouseButtons.Left) return;
            int h = NodeAt(e.Location);
            if (h < 0 || h >= _items.Count) { Close(); return; }   // tap the void to dismiss
            Action run = _items[h].Run;
            Close();
            if (run != null) run();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _face != null) { _face.Dispose(); _face = null; }
            base.Dispose(disposing);
        }
    }
}
