using System;
using System.Drawing;
using System.Windows.Forms;
using DNotes.Core;

namespace DNotes.Core
{
    /// <summary>Global metrics, so every window agrees on rounding.</summary>
    internal static class Ui
    {
        private static float _scale = 1f;

        public static float Scale { get { return _scale; } }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        /// <summary>
        /// Read the real display DPI off the screen DC. Measuring it on a
        /// throwaway Control is tempting but unreliable: an unrealized control
        /// can report a default 96 now and something else a moment later, which
        /// would leave two windows disagreeing about every metric.
        /// </summary>
        public static void InitScale(Control probe)
        {
            try
            {
                IntPtr hdc = GetDC(IntPtr.Zero);
                int dpi = 0;
                if (hdc != IntPtr.Zero)
                {
                    try { dpi = Native.GetDeviceCaps(hdc, Native.LOGPIXELSX); }
                    finally { ReleaseDC(IntPtr.Zero, hdc); }
                }
                float d = (dpi > 0 ? dpi : 96) / 96f;
                if (d < 1f) d = 1f;
                if (d > 2f) d = 2f;
                _scale = (float)Math.Round(d * 4f) / 4f;   // snap to quarters
            }
            catch { _scale = 1f; }
        }

        public static int Px(int v) { return (int)Math.Round(v * _scale); }

        /// <summary>Device pixels back to logical units, for stored geometry.</summary>
        public static int Logical(int v)
        {
            if (_scale <= 0.01f) return v;
            return (int)Math.Round(v / _scale);
        }
    }

    /// <summary>Flat, neon-outlined renderer for the context menus.</summary>
    internal sealed class NeonMenuRenderer : ToolStripProfessionalRenderer
    {
        public NeonMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Theme.Neon : Theme.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var r = new Rectangle(e.Item.ContentRectangle.X,
                                  e.Item.ContentRectangle.Y + 2,
                                  e.Item.ContentRectangle.Width, 1);
            using (var p = new Pen(Theme.Alpha(Theme.Neon, 45)))
                e.Graphics.DrawLine(p, r.X, r.Y, r.Right, r.Y);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var r = e.AffectedBounds;
            using (var p = new Pen(Theme.Alpha(Theme.Neon, 120), 1f))
                e.Graphics.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Surface))
                e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

        private sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
            public override Color MenuItemSelected { get { return Theme.Alpha(Theme.Neon, 34); } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Alpha(Theme.Neon, 34); } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Alpha(Theme.Neon, 34); } }
            public override Color MenuItemBorder { get { return Theme.Alpha(Theme.Neon, 90); } }
            public override Color MenuBorder { get { return Theme.Alpha(Theme.Neon, 120); } }
            public override Color SeparatorDark { get { return Theme.Alpha(Theme.Neon, 45); } }
            public override Color SeparatorLight { get { return Theme.Alpha(Theme.Neon, 45); } }
        }
    }
}

namespace DNotes.Windows
{
    /// <summary>Small modal helpers, all drawn in the same skin.</summary>
    internal static class Dialogs
    {
        public static void ConfirmDelete(IWin32Window owner, string title, Action yes)
        {
            using (var f = new ConfirmForm("DELETE NOTE", "This removes \"" + title +
                "\" from the deck.", "DELETE", "KEEP", Theme.Danger))
            {
                if (f.ShowDialog(owner) == DialogResult.Yes) yes();
            }
        }

        public static void Info(IWin32Window owner, string heading, string body)
        {
            using (var f = new ConfirmForm(heading, body, "OKAY", "CLOSE", Theme.Neon))
            {
                f.ShowDialog(owner);
            }
        }
    }

    /// <summary>Cyberpunk confirm/info dialog. Click-through is never an issue here.</summary>
    internal sealed class ConfirmForm : Form
    {
        private readonly string _head, _body, _yes, _no;
        private readonly Color _tint;
        private Rectangle _rYes, _rNo;
        private Rectangle _hot = Rectangle.Empty;
        private readonly string[] _lines;

        public ConfirmForm(string head, string body, string yes, string no, Color tint)
        {
            _head = head; _body = body; _yes = yes; _no = no; _tint = tint;
            _lines = Wrap(body, 46);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Ui.Px(400), Ui.Px(210));
            BackColor = Theme.Base;
            DoubleBuffered = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;

            using (Graphics g = CreateGraphics()) Gfx.Setup(g);
            Shape.Round(this, Ui.Px(12));

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.Yes; Close(); }
            };
            MouseDown += OnClick;
            MouseMove += (s, e) =>
            {
                Rectangle b = Hit(e.Location);
                if (b != _hot) { _hot = b; Invalidate(); }
                Cursor = b != Rectangle.Empty ? Cursors.Hand : Cursors.Default;
            };
            MouseLeave += (s, e) => { if (_hot != Rectangle.Empty) { _hot = Rectangle.Empty; Invalidate(); } };
        }

        private static string[] Wrap(string s, int width)
        {
            var words = (s ?? "").Split(' ');
            var lines = new System.Collections.Generic.List<string>();
            var cur = new System.Text.StringBuilder();
            foreach (var w in words)
            {
                if (cur.Length + w.Length + 1 > width) { lines.Add(cur.ToString()); cur.Clear(); }
                if (cur.Length > 0) cur.Append(' ');
                cur.Append(w);
            }
            if (cur.Length > 0) lines.Add(cur.ToString());
            return lines.ToArray();
        }

        private Rectangle Hit(Point p)
        {
            if (_rYes.Contains(p)) return _rYes;
            if (_rNo.Contains(p)) return _rNo;
            return Rectangle.Empty;
        }

        private void OnClick(object s, MouseEventArgs e)
        {
            Rectangle b = Hit(e.Location);
            if (b == _rYes) { DialogResult = DialogResult.Yes; Close(); }
            else if (b == _rNo) { DialogResult = DialogResult.Cancel; Close(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Gfx.Setup(g);
            using (var b = new SolidBrush(Theme.Base)) g.FillRectangle(b, ClientRectangle);
            Neon.Grid(g, ClientRectangle, Ui.Px(18), Theme.Alpha(Theme.Grid, 150));
            Neon.Scanlines(g, ClientRectangle, Theme.Neon, Ui.Px(3), 9);
            Neon.RoundOutline(g, new Rectangle(0, 0, Width - 1, Height - 1), Ui.Px(12),
                              Theme.Alpha(_tint, 150));
            Neon.GlowRect(g, new Rectangle(1, 1, Width - 3, Height - 3), _tint, Ui.Px(12), 2);
            Neon.Brackets(g, new Rectangle(0, 0, Width, Height), Ui.Px(5), Ui.Px(12),
                          Theme.Alpha(_tint, 160));

            int pad = Ui.Px(20);
            using (Bitmap mk = Assets.MarkTile(Ui.Px(30), true, _tint))
                g.DrawImage(mk, pad, pad);

            Neon.Caps(g, _head, Theme.Ui(9f, FontStyle.Bold),
                      new Point(pad + Ui.Px(40), pad + Ui.Px(8)),
                      Theme.Alpha(_tint, 235), 2);

            var f = Theme.Ui(9.2f, FontStyle.Regular);
            int y = pad + Ui.Px(48);
            foreach (string ln in _lines)
            {
                g.DrawString(ln, f, new SolidBrush(Theme.TextDim), pad, y);
                y += Ui.Px(16);
            }

            int bw = Ui.Px(96), bh = Ui.Px(28);
            int by = Height - pad - bh;
            _rYes = new Rectangle(Width - pad - bw, by, bw, bh);
            _rNo = new Rectangle(_rYes.X - bw - Ui.Px(8), by, bw, bh);

            Btn(g, _rYes, _yes, _tint, _hot == _rYes, true);
            Btn(g, _rNo, _no, Theme.TextDim, _hot == _rNo, false);
        }

        private static void Btn(Graphics g, Rectangle r, string label, Color tint, bool hot, bool primary)
        {
            if (primary)
            {
                Neon.FillRound(g, r, Ui.Px(5), Theme.Alpha(tint, hot ? 70 : 40));
                Neon.RoundOutline(g, r, Ui.Px(5), Theme.Alpha(tint, 200));
            }
            else
            {
                Neon.RoundOutline(g, r, Ui.Px(5), Theme.Alpha(Theme.Hairline, 210));
            }
            var f = Theme.Ui(7.6f, FontStyle.Bold);
            int tw = (int)Neon.CapsWidth(g, label, f, 1);
            Neon.Caps(g, label, f,
                      new Point(r.X + (r.Width - tw) / 2, r.Y + (r.Height - Ui.Px(10)) / 2),
                      primary ? tint : Theme.TextDim, 1);
        }
    }
}
