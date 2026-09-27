using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace DNotes.Windows
{
    using Core;

    /// <summary>
    /// Settings, drawn in the same skin as everything else. The window sizes
    /// itself to whatever the content needs, so a section can never be pushed
    /// off the bottom by a taller font or a longer diagnostic string.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly App _app;
        private readonly Store _store;
        private readonly List<Rectangle> _hits = new List<Rectangle>();
        private readonly List<Action> _actions = new List<Action>();
        private Rectangle _rClose;
        private int _hover = -1;
        private bool _sized;
        private int _needed;

        private int S(int v) { return (int)Math.Round(v * Ui.Scale); }

        public SettingsForm(App app, Store store)
        {
            _app = app;
            _store = store;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            BackColor = Theme.Base;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(S(468), S(600));
            Text = "DNotes \\ Settings";
            Shape.Round(this, S(12));
        }

        public void Reload() { Invalidate(); }

        private void Hit(Rectangle r, Action a)
        {
            _hits.Add(r);
            _actions.Add(a);
        }

        // ==================================================================
        // one layout pass, used both to measure and to draw
        // ==================================================================

        private int Content(Graphics g, int W, int H, bool draw)
        {
            int pad = S(20), y = pad;
            if (draw) { _hits.Clear(); _actions.Clear(); }

            Settings s = _app.Settings;

            // ---- header -------------------------------------------------
            if (draw)
            {
                using (Bitmap mk = Assets.MarkTile(S(26), true, Theme.Neon))
                    g.DrawImage(mk, pad, y - S(2));
                Neon.Caps(g, "SETTINGS", Theme.Ui(9.2f, FontStyle.Bold),
                          new Point(pad + S(35), y + S(2)), Theme.Text, 2);
                Neon.Caps(g, "DNOTES 1.0  \\  DANISHYAR", Theme.Ui(6.4f, FontStyle.Bold),
                          new Point(pad + S(35), y + S(19)), Theme.TextFaint, 1);
            }
            _rClose = new Rectangle(W - pad - S(22), y - S(1), S(22), S(22));
            if (draw)
            {
                X(g, _rClose);
                Hit(_rClose, Close);
            }
            y += S(36);
            if (draw) Neon.HLine(g, y, pad, W - pad, Theme.Alpha(Theme.Neon, 55));
            y += S(15);

            // ---- general ------------------------------------------------
            y = Section(g, pad, y, W, draw, "GENERAL");
            y = Toggle(g, pad, y, draw, "Start DNotes when I sign in", s.AutoStart,
                       v => { s.AutoStart = v; Autostart.Apply(v); });
            y = Toggle(g, pad, y, draw, "Ask before deleting a note", s.ConfirmDelete, v => s.ConfirmDelete = v);
            y = Toggle(g, pad, y, draw, "Shade a note when it loses focus", s.CollapseOnFocusLoss,
                       v => s.CollapseOnFocusLoss = v);
            y += S(11);

            // ---- appearance ---------------------------------------------
            y = Section(g, pad, y, W, draw, "APPEARANCE");
            y = Stepper(g, pad, y, W, draw, "Body text size", s.FontSize + " PT",
                        () => s.FontSize.ToString() + " PT",
                        d => { s.FontSize = Clamp(s.FontSize + d, 8, 28); _app.ReapplyFonts(); });
            y += S(9);
            y = Swatches(g, pad, y, W, draw, s);
            y += S(15);


            // ---- floating button ---------------------------------------------
            y = Section(g, pad, y, W, draw, "FLOATING BUTTON");
            y = Toggle(g, pad, y, draw, "Show the floating button", s.OrbShow,
                       v => _app.SetOrbVisible(v));
            if (draw && !s.OrbShow)
            {
                // the button is the only way into the app, so say plainly how
                // to get it back rather than letting someone hide it and then
                // wonder what happened
                Neon.Caps(g, "HIDDEN \u2014 PRESS CTRL+ALT+B, OR RIGHT-CLICK THE TRAY ICON",
                          Theme.Ui(6.2f, FontStyle.Bold),
                          new Point(pad + S(4), y - S(4)), Theme.Alpha(Theme.Cyan, 220), 1);
            }
            y = Toggle(g, pad, y, draw, "Snap it to a screen edge", s.OrbSnap,
                       v => { s.OrbSnap = v; _store.SaveSettings(s); });
            y += S(6);
            if (draw)
            {
                var rb = new Rectangle(pad, y, S(168), S(24));
                Neon.RoundOutline(g, rb, S(4), Theme.Alpha(Theme.Cyan, 190));
                Neon.Caps(g, "RESET ITS POSITION", Theme.Ui(6.2f, FontStyle.Bold),
                          new Point(rb.X + S(10), rb.Y + S(7)), Theme.Cyan, 1);
                Hit(rb, () => { _app.ResetOrbPosition(); Invalidate(); });
                y += S(30);
            }
            else y += S(30);
            y = Info(g, pad, y, W, draw, "LEFT CLICK", "SHORTCUT FAN");
            y = Info(g, pad, y, W, draw, "RIGHT CLICK", "LIST MENU");
            y = Info(g, pad, y, W, draw, "DRAG IT", "ANYWHERE ON SCREEN");
            y += S(15);

            // ---- keyboard ---------------------------------------------------
            y = Section(g, pad, y, W, draw, "KEYBOARD");
            y = Info(g, pad, y, W, draw, "GLOBAL", "CTRL+ALT+ N L A E H");
            y = Info(g, pad, y, W, draw, "IN A NOTE", "CTRL+ B I U  F  S  1-6  TAB");
            y = Info(g, pad, y, W, draw, "TUCK AWAY", "ESC   /   CTRL+ENTER");
            y += S(15);

            // ---- self check --------------------------------------------------
            y = Section(g, pad, y, W, draw, "SELF-CHECK");
            y = Diagnose(g, pad, y, W, draw);

            // ---- footer -------------------------------------------------------
            int bh = S(28);
            int fy = H - pad - bh;
            if (draw)
            {
                Neon.HLine(g, fy - S(10), pad, W - pad, Theme.Alpha(Theme.Neon, 55));
                var bClose = new Rectangle(W - pad - S(84), fy, S(84), bh);
                Neon.FillRound(g, bClose, S(5), Theme.Alpha(Theme.Neon, 36));
                Neon.RoundOutline(g, bClose, S(5), Theme.Alpha(Theme.Neon, 190));
                var bf = Theme.Ui(7.2f, FontStyle.Bold);
                int bw = (int)Neon.CapsWidth(g, "CLOSE", bf, 1);
                Neon.Caps(g, "CLOSE", bf, new Point(bClose.X + (bClose.Width - bw) / 2,
                          bClose.Y + (bClose.Height - S(9)) / 2), Theme.Neon, 1);
                Hit(bClose, Close);

                var bFolder = new Rectangle(pad, fy, S(140), bh);
                Neon.RoundOutline(g, bFolder, S(5), Theme.Alpha(Theme.Hairline, 210));
                int fw = (int)Neon.CapsWidth(g, "OPEN DATA FOLDER", bf, 1);
                Neon.Caps(g, "OPEN DATA FOLDER", bf, new Point(bFolder.X + (bFolder.Width - fw) / 2,
                          bFolder.Y + (bFolder.Height - S(9)) / 2), Theme.TextDim, 1);
                Hit(bFolder, () => OpenFolder(_app.DataFolder));
            }
            return y;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        // ==================================================================
        // paint
        // ==================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Gfx.Setup(g);

            // first pass measures, so the window can be exactly as tall as it
            // needs to be on this font size and this diagnostic result
            if (!_sized)
            {
                _sized = true;
                using (var probe = new Bitmap(1, 1))
                using (var pg = Graphics.FromImage(probe))
                {
                    _needed = Content(pg, ClientSize.Width, int.MaxValue / 4, false);
                }
                int want = _needed + S(20) + S(28) + S(20);
                int maxH = Screen.FromControl(this).WorkingArea.Height - S(30);
                ClientSize = new Size(ClientSize.Width, Math.Min(want, maxH));
                Shape.Round(this, S(12));
            }

            using (var b = new SolidBrush(Theme.Base)) g.FillRectangle(b, ClientRectangle);
            Neon.Grid(g, ClientRectangle, S(20), Theme.Alpha(Theme.Grid, 120));
            Neon.Scanlines(g, ClientRectangle, Theme.Neon, S(3), 8);
            Neon.RoundOutline(g, new Rectangle(0, 0, Width - 1, Height - 1), S(12),
                              Theme.Alpha(Theme.Neon, 170));
            Neon.GlowRect(g, new Rectangle(1, 1, Width - 3, Height - 3), Theme.Neon, S(12), 2);
            Neon.Brackets(g, ClientRectangle, S(5), S(14), Theme.Alpha(Theme.Neon, 140), 1.4f);

            Content(g, ClientSize.Width, ClientSize.Height, true);
        }

        // ==================================================================
        // section builders
        // ==================================================================

        private int Section(Graphics g, int x, int y, int W, bool draw, string label)
        {
            var f = Theme.Ui(6.8f, FontStyle.Bold);
            if (draw)
            {
                Neon.Caps(g, label, f, new Point(x, y + S(3)), Theme.Alpha(Theme.Neon, 190), 2);
                int w = (int)Neon.CapsWidth(g, label, f, 2);
                Neon.DashRule(g, x + w + S(10), W - x, y + S(7), Theme.Alpha(Theme.Neon, 45));
            }
            return y + S(20);
        }

        private int Toggle(Graphics g, int x, int y, bool draw, string label, bool on,
                           Action<bool> set)
        {
            int h = S(26);
            var box = new Rectangle(Width - S(20) - S(38), y + S(3), S(38), S(18));
            if (draw)
            {
                Neon.Caps(g, label, Theme.Ui(8.2f, FontStyle.Regular), new Point(x, y + S(5)),
                          Theme.Text, 1);
                if (on)
                {
                    Neon.FillRound(g, box, S(9), Theme.Alpha(Theme.Neon, 55));
                    Neon.RoundOutline(g, box, S(9), Theme.Alpha(Theme.Neon, 210));
                    using (var b = new SolidBrush(Theme.Neon))
                        g.FillEllipse(b, box.Right - S(16), box.Y + S(2), S(14), S(14));
                }
                else
                {
                    Neon.RoundOutline(g, box, S(9), Theme.Alpha(Theme.Hairline, 220));
                    using (var b = new SolidBrush(Theme.TextFaint))
                        g.FillEllipse(b, box.X + S(2), box.Y + S(2), S(14), S(14));
                }
                Hit(new Rectangle(x, y, Width - x - S(20), h),
                    () => { set(!on); _store.SaveSettings(_app.Settings); Invalidate(); });
            }
            return y + h + S(2);
        }

        private int Stepper(Graphics g, int x, int y, int W, bool draw, string label,
                            string value, Func<string> get, Action<int> set)
        {
            int h = S(26), bw = S(24);
            int bx = W - S(20) - bw;
            var plus = new Rectangle(bx, y + S(2), bw, h - S(4));
            var minus = new Rectangle(plus.X - bw - S(4), plus.Y, bw, plus.Height);
            var val = new Rectangle(minus.X - S(48), y, S(44), h);
            if (draw)
            {
                Neon.Caps(g, label, Theme.Ui(8.2f, FontStyle.Regular), new Point(x, y + S(5)),
                          Theme.Text, 1);
                var vf = Theme.Mono(8.4f, FontStyle.Bold);
                SizeF vs = g.MeasureString(value, vf);
                g.DrawString(value, vf, new SolidBrush(Theme.Neon), val.Right - vs.Width, y + S(4));
                BtnBox(g, minus, false);
                BtnBox(g, plus, true);
                Hit(minus, () => { set(-1); _store.SaveSettings(_app.Settings); Invalidate(); });
                Hit(plus, () => { set(1); _store.SaveSettings(_app.Settings); Invalidate(); });
            }
            return y + h + S(2);
        }

        private void BtnBox(Graphics g, Rectangle r, bool plus)
        {
            bool hot = _hover >= 0 && _hover < _hits.Count && _hits[_hover] == r;
            Neon.RoundOutline(g, r, S(4), Theme.Alpha(hot ? Theme.Neon : Theme.Hairline, 210));
            if (hot) Neon.FillRound(g, r, S(4), Theme.Alpha(Theme.Neon, 36));
            using (var p = new Pen(hot ? Theme.Neon : Theme.TextDim, 1.5f)
            {
                StartCap = LineCap.Square,
                EndCap = LineCap.Square
            })
            {
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, d = S(5);
                g.DrawLine(p, cx - d, cy, cx + d, cy);
                if (plus) g.DrawLine(p, cx, cy - d, cx, cy + d);
            }
        }

        private int Segments(Graphics g, int x, int y, int W, bool draw, Settings s,
                             string[] opts, string cur, Action<string> set)
        {
            int sh = S(22), sw = S(64), gap = S(4);
            int total = opts.Length * (sw + gap) - gap;
            int sx = W - S(20) - total;
            if (draw)
                Neon.Caps(g, "Dock to edge", Theme.Ui(8.2f, FontStyle.Regular),
                          new Point(x, y + S(5)), Theme.Text, 1);
            for (int i = 0; i < opts.Length; i++)
            {
                var r = new Rectangle(sx + i * (sw + gap), y + S(2), sw, sh);
                bool on = opts[i] == cur;
                if (draw)
                {
                    if (on)
                    {
                        Neon.FillRound(g, r, S(4), Theme.Alpha(Theme.Neon, 38));
                        Neon.RoundOutline(g, r, S(4), Theme.Alpha(Theme.Neon, 200));
                    }
                    else Neon.RoundOutline(g, r, S(4), Theme.Alpha(Theme.Hairline, 200));
                    var f = Theme.Ui(6f, FontStyle.Bold);
                    int tw = (int)Neon.CapsWidth(g, opts[i], f, 1);
                    Neon.Caps(g, opts[i], f, new Point(r.X + (r.Width - tw) / 2,
                              r.Y + (r.Height - S(8)) / 2), on ? Theme.Neon : Theme.TextDim, 1);
                    int idx = i;
                    Hit(r, () => { set(opts[idx]); _store.SaveSettings(_app.Settings); Invalidate(); });
                }
            }
            return y + S(28);
        }

        private int Swatches(Graphics g, int x, int y, int W, bool draw, Settings s)
        {
            int sz = S(16), gap = S(6);
            int sx = W - S(20) - (Theme.Accents.Length * (sz + gap)) + gap;
            if (draw)
                Neon.Caps(g, "Default note colour", Theme.Ui(8.2f, FontStyle.Regular),
                          new Point(x, y + S(5)), Theme.Text, 1);
            for (int i = 0; i < Theme.Accents.Length; i++)
            {
                var r = new Rectangle(sx + i * (sz + gap), y + S(3), sz, sz);
                Color c = Theme.Accents[i];
                bool on = s.DefaultAccent == i;
                if (draw)
                {
                    if (on)
                        Neon.Glow(g, (gg, p) => gg.DrawRectangle(p, r.X - 1, r.Y - 1,
                                                                 r.Width + 2, r.Height + 2), c, 2);
                    Neon.FillRound(g, r, S(3), c);
                    Neon.RoundOutline(g, r, S(3), Theme.Alpha(c, 200));
                    int idx = i;
                    Hit(Rectangle.Inflate(r, 2, 2), () =>
                    {
                        s.DefaultAccent = idx;
                        _store.SaveSettings(_app.Settings);
                        Invalidate();
                    });
                }
            }
            return y + S(26);
        }

        private int Info(Graphics g, int x, int y, int W, bool draw, string label, string value)
        {
            if (draw)
            {
                Neon.Caps(g, label, Theme.Ui(7.6f, FontStyle.Regular), new Point(x, y + S(2)),
                          Theme.TextDim, 1);
                var f = Theme.Mono(7.6f, FontStyle.Bold);
                SizeF sz = g.MeasureString(value, f);
                using (var b = new SolidBrush(Theme.Alpha(Theme.Cyan, 215)))
                    g.DrawString(value, f, b, W - S(20) - sz.Width, y + S(1));
            }
            return y + S(17);
        }

        /// <summary>
        /// A structural read-out of the editor, so a problem of any kind shows
        /// up here rather than as a note that silently refuses to be typed in.
        /// </summary>
        private int Diagnose(Graphics g, int x, int y, int W, bool draw)
        {
            NoteForm f = _app.FirstOpenNote();
            var lines = new List<string[]>();
            string verdict = "OPEN A NOTE TO RUN THE FULL CHECK";
            bool bad = false;

            if (f == null)
            {
                lines.Add(new[] { "EDITOR WINDOW", "not open", "0" });
            }
            else
            {
                var ed = f.Editor;
                bool focusable = ed.CanFocus && ed.Enabled && !ed.ReadOnly;
                bool onTop = !ed.Parent.Controls.Cast<Control>().Any(
                    c => c != ed && c.Visible && c.Bounds.IntersectsWith(ed.Bounds));
                bool ancestorsOk = true;
                for (Control c = ed; c != null; c = c.Parent)
                    if (!c.Enabled) { ancestorsOk = false; break; }

                Point mid = ed.PointToScreen(new Point(ed.Width / 2, ed.Height / 2));
                bool hitOk = Native.DeepestChildAt(f.Handle, mid) == ed.Handle;

                lines.Add(new[] { "EDITOR HANDLE", ed.IsHandleCreated ? "created" : "missing", ed.IsHandleCreated ? "1" : "0" });
                lines.Add(new[] { "EDITABLE", (!ed.ReadOnly && ed.Enabled) ? "yes" : "NO", (!ed.ReadOnly && ed.Enabled) ? "1" : "0" });
                lines.Add(new[] { "FOCUSABLE", focusable ? "yes" : "NO", focusable ? "1" : "0" });
                lines.Add(new[] { "NOT COVERED", onTop ? "clear" : "BLOCKED", onTop ? "1" : "0" });
                lines.Add(new[] { "CLICK REACHES EDITOR", hitOk ? "yes" : "NO", hitOk ? "1" : "0" });
                lines.Add(new[] { "ANCESTORS ENABLED", ancestorsOk ? "yes" : "NO", ancestorsOk ? "1" : "0" });

                bad = !focusable || !onTop || !hitOk || !ed.IsHandleCreated
                      || ed.ReadOnly || !ed.Enabled || !ancestorsOk;
                verdict = bad ? "PROBLEM FOUND - SEE RED ROWS" : "ALL CHECKS PASS";
            }

            if (draw)
            {
                var f2 = Theme.Mono(7.2f, FontStyle.Regular);
                foreach (string[] row in lines)
                {
                    bool ok = row[2] == "1" || (f == null && row[1] == "not open");
                    Color c = f == null ? Theme.TextFaint : (ok ? Theme.Neon : Theme.Danger);
                    g.DrawString(row[0], f2, new SolidBrush(Theme.TextDim), x, y);
                    g.DrawString(row[1], f2, new SolidBrush(c), W - S(20) - S(80), y);
                    y += S(15);
                }
                Neon.Caps(g, verdict, Theme.Ui(6.8f, FontStyle.Bold), new Point(x, y + S(3)),
                          bad ? Theme.Danger : (f == null ? Theme.TextFaint : Theme.Neon), 1);
                y += S(21);

                var b = new Rectangle(x, y, S(186), S(24));
                Neon.RoundOutline(g, b, S(4), Theme.Alpha(Theme.Cyan, 190));
                Neon.Caps(g, "OPEN A NOTE AND RECHECK", Theme.Ui(6.2f, FontStyle.Bold),
                          new Point(b.X + S(10), b.Y + S(7)), Theme.Cyan, 1);
                Hit(b, () =>
                {
                    if (_app.FirstOpenNote() == null)
                    {
                        if (_app.Notes.Count > 0) _app.OpenNote(_app.Notes[0].Id);
                        else _app.NewNote();
                    }
                    Invalidate();
                });
                y += S(29);
                Neon.Caps(g, "FOR A FULL KEYSTROKE TEST RUN  DNOTES.EXE --SELFTEST",
                          Theme.Ui(6.2f, FontStyle.Bold), new Point(x, y), Theme.TextFaint, 1);
                y += S(15);
            }
            else
            {
                y += lines.Count * S(15) + S(21) + S(29) + S(15);
            }
            return y;
        }

        private void X(Graphics g, Rectangle r)
        {
            bool hot = _hover >= 0 && _hover < _hits.Count && _hits[_hover] == r;
            Neon.RoundOutline(g, r, S(4), Theme.Alpha(hot ? Theme.Danger : Theme.Hairline, 220));
            if (hot) Neon.FillRound(g, r, S(4), Theme.Alpha(Theme.Danger, 42));
            using (var p = new Pen(hot ? Theme.Danger : Theme.TextDim, 1.5f))
            {
                int d = 4, cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                g.DrawLines(p, new[] { new Point(cx - d, cy - d), new Point(cx + d, cy + d) });
                g.DrawLines(p, new[] { new Point(cx + d, cy - d), new Point(cx - d, cy + d) });
            }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                if (!System.IO.Directory.Exists(path)) System.IO.Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Write("open folder: " + ex.Message); }
        }

        // ==================================================================
        // input
        // ==================================================================

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = -1;
            for (int i = 0; i < _hits.Count; i++) if (_hits[i].Contains(e.Location)) { h = i; break; }
            if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < _hits.Count; i++)
                if (_hits[i].Contains(e.Location)) { _actions[i](); return; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>About: the emblem, the wordmark and the plain facts.</summary>
    internal sealed class AboutForm : Form
    {
        public AboutForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            BackColor = Theme.Base;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.Px(430), Ui.Px(432));
            Text = "About DNotes";
            Shape.Round(this, Ui.Px(12));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Gfx.Setup(g);
            var R = ClientRectangle;
            int pad = Ui.Px(24);

            using (var b = new SolidBrush(Theme.Base)) g.FillRectangle(b, R);
            Neon.Grid(g, R, Ui.Px(20), Theme.Alpha(Theme.Grid, 120));
            Neon.Scanlines(g, R, Theme.Neon, Ui.Px(3), 8);
            Neon.RoundOutline(g, new Rectangle(0, 0, R.Width - 1, R.Height - 1), Ui.Px(12),
                              Theme.Alpha(Theme.Neon, 180));
            Neon.GlowRect(g, new Rectangle(1, 1, R.Width - 3, R.Height - 3), Theme.Neon, Ui.Px(12), 2);
            Neon.Brackets(g, R, Ui.Px(5), Ui.Px(14), Theme.Alpha(Theme.Neon, 140), 1.4f);

            int em = Ui.Px(168);
            Bitmap bmp = Assets.Emblem;
            if (bmp != null) g.DrawImage(bmp, new Rectangle(R.Width / 2 - em / 2, pad, em, em));

            int y = pad + em + Ui.Px(4);
            var big = Theme.Ui(15f, FontStyle.Bold);
            SizeF ts = g.MeasureString("DNOTES", big);
            g.DrawString("DNOTES", big, new SolidBrush(Theme.Neon), R.Width / 2f - ts.Width / 2f, y);
            y += Ui.Px(26);

            var sub = Theme.Ui(6.6f, FontStyle.Bold);
            const string tag = "CYBERPUNK EDGE NOTES  \\  WINDOWS";
            Neon.Caps(g, tag, sub,
                      new Point(R.Width / 2 - (int)Neon.CapsWidth(g, tag, sub, 2) / 2, y),
                      Theme.TextDim, 2);
            y += Ui.Px(22);

            var body = Theme.Ui(7.6f, FontStyle.Regular);
            string[] lines =
            {
                "A deck of sticky notes docked to the edge of your screen.",
                "Reach over and the pill opens; open a note and just type.",
                "",
                "Version 1.0   \\   one plain .dnote file per note",
            };
            foreach (string ln in lines)
            {
                SizeF sz = g.MeasureString(ln, body);
                using (var b = new SolidBrush(Theme.TextDim))
                    g.DrawString(ln, body, b, R.Width / 2f - sz.Width / 2f, y);
                y += Ui.Px(15);
            }

            y += Ui.Px(6);
            Neon.DashRule(g, pad + Ui.Px(30), R.Width - pad - Ui.Px(30), y, Theme.Alpha(Theme.Neon, 50));
            y += Ui.Px(10);

            Bitmap wm = Assets.Wordmark;
            if (wm != null)
            {
                int ww = Ui.Px(230);
                int wh = (int)(ww * wm.Height / (double)wm.Width);
                g.DrawImage(wm, new Rectangle(R.Width / 2 - ww / 2, y, ww, wh));
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape || keyData == Keys.Enter) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
