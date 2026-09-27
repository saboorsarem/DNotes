using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DNotes.Windows
{
    using Core;

    /// <summary>
    /// One window for every note: live search across titles, bodies and tags,
    /// filters for active or archived, multi-select, and bulk export.
    /// </summary>
    internal sealed class AllNotesForm : Form
    {
        public enum Filter { All, Active, Archived }

        private const int ROW = 46;

        private readonly App _app;
        private readonly Store _store;
        private Filter _filter = Filter.All;
        private string _query = "";
        private readonly List<Note> _view = new List<Note>();
        private readonly List<Note> _sel = new List<Note>();

        // search field
        private TextBox _search;
        // filter chips
        private Rectangle _rAll, _rActive, _rArch;
        // row rects
        private readonly List<Rectangle> _rows = new List<Rectangle>();
        private int _hover = -1;
        // footer buttons
        private Rectangle _rNew, _rExport, _rArchive, _rImport, _rOpen, _rDelete;
        private int _hoverBtn = -1;
        // export menu anchor
        private Rectangle _rExportMenu;
        private int _menuOpen;

        public Filter CurrentFilter { get { return _filter; } }
        public int VisibleCount { get { return _view.Count; } }
        public int SelectedCount { get { return _sel.Count; } }

        /// <summary>The drawn note rows, for the self-test.</summary>
        public List<Rectangle> RowRects { get { return new List<Rectangle>(_rows); } }

        /// <summary>One footer action by its BtnAt id, for the self-test.</summary>
        public Rectangle ActionRect(int id)
        {
            switch (id)
            {
            case 1: return _rNew;
            case 2: return _rImport;
            case 3: return _rExport;
            case 4: return _rExportMenu;
            case 5: return _rArchive;
            case 6: return _rOpen;
            case 9: return _rDelete;
            }
            return Rectangle.Empty;
        }
        public int FirstVisibleIndex { get { return _view.Count > 0 ? _notes.IndexOf(_view[0]) : -1; } }

        private List<Note> _notes = new List<Note>();

        private int S(int v) { return (int)Math.Round(v * Ui.Scale); }

        public AllNotesForm(App app, Store store)
        {
            _app = app;
            _store = store;
            _notes = app.Notes;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            BackColor = Theme.Base;
            DoubleBuffered = true;
            KeyPreview = false;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(S(880), S(560));
            MinimumSize = new Size(S(560), S(380));
            Text = "DNotes \\ All Notes";
            KeyPreview = false;

            Shape.Round(this, S(12));
            ApplyExStyle();

            _search = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = Theme.Mono(10f, FontStyle.Regular),
                ReadOnly = false,
                Enabled = true,
                TabStop = true,
                HideSelection = false,
                MaxLength = 120,
            };
            _search.TextChanged += (s, e) => { _query = _search.Text; Rebuild(); };
            _search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { _search.Text = ""; e.SuppressKeyPress = true; }
            };
            _search.GotFocus += (s, e) => Invalidate();
            _search.LostFocus += (s, e) => Invalidate();
            Controls.Add(_search);

            Resize += (s, e) => { Shape.Round(this, S(12)); Layout(); Invalidate(); };
            Shown += (s, e) => { _search.Focus(); Rebuild(); };
            KeyDown += OnGlobalKey;
            _app.NotesReloaded += (s, e) => { if (!IsDisposed) Rebuild(); };
        }

        private void ApplyExStyle()
        {
            if (!IsHandleCreated) return;
            int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_APPWINDOW;
            ex &= ~Native.WS_EX_NOACTIVATE;
            ex &= ~Native.WS_EX_TRANSPARENT;
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);
        }

        // ==================================================================
        // data
        // ==================================================================

        public void SetFilter(Filter f)
        {
            _filter = f;
            if (IsHandleCreated) Rebuild();
        }

        public void RefreshList()
        {
            if (IsDisposed) return;
            _notes = _app.Notes;
            Rebuild();
        }

        private void Rebuild()
        {
            _notes = _app.Notes;
            var keep = new HashSet<string>();
            foreach (Note n in _sel) keep.Add(n.Id);

            _view.Clear();
            foreach (Note n in _notes)
            {
                if (_filter == Filter.Active && n.Archived) continue;
                if (_filter == Filter.Archived && !n.Archived) continue;
                if (!n.Matches(_query)) continue;
                _view.Add(n);
            }
            _sel.RemoveAll(x => !keep.Contains(x.Id) || !_view.Contains(x));
            if (IsHandleCreated) { Layout(); Invalidate(); }
        }

        // ==================================================================
        // layout
        // ==================================================================

        private void Layout()
        {
            int pad = S(16);
            int y = pad;

            // header
            y += S(26);
            y += S(10);

            // search
            _rSearchBox = new Rectangle(pad, y, ClientSize.Width - pad * 2, S(30));
            _search.SetBounds(_rSearchBox.X + S(30), _rSearchBox.Y + (S(30) - _search.Height) / 2,
                              Math.Max(S(40), _rSearchBox.Width - S(44)), _search.Height);
            y += S(30) + S(12);

            // filter chips
            int chh = S(24);
            _chipY = y;
            y += chh + S(14);

            // summary line
            y += S(20);

            // ---- rows area
            _listTop = y;
            int footH = S(46);
            _listBottom = ClientSize.Height - footH - pad;
            int per = Math.Max(1, (_listBottom - _listTop) / S(ROW));

            // footer: actions laid out right-to-left so they can never overlap
            int by = _listBottom + S(10), bh = S(28);
            // Seven actions now, and the window can be dragged down to 560 logical.
            // Size the buttons to whatever the window can actually give them rather
            // than fixing a width that would run EXPORT into IMPORT on a narrow
            // All Notes. The labels are centred, so a tighter button still reads.
            const int nBtn = 7;
            int gap = S(6);
            int aw = Math.Max(S(52), Math.Min(S(84),
                           (ClientSize.Width - pad * 2 - nBtn * gap) / nBtn));
            int right = ClientSize.Width - pad;
            _rOpen = new Rectangle(right - aw, by, aw, bh); right -= aw + gap;
            // DELETE sits next to OPEN: the deck used to carry the delete action and
            // went away with it, which left the keyboard shortcut as the only way
            // to remove a note from here. A destructive action you have to know to
            // press is an action most people never find.
            _rDelete = new Rectangle(right - aw, by, aw, bh); right -= aw + gap;
            _rArchive = new Rectangle(right - aw, by, aw, bh); right -= aw + gap;
            _rExportMenu = new Rectangle(right - aw, by, aw, bh); right -= aw + gap;
            _rExport = new Rectangle(right - aw, by, aw, bh);
            _rNew = new Rectangle(pad, by, aw, bh);
            _rImport = new Rectangle(_rNew.Right + gap, by, aw, bh);
        }

        private Rectangle _rSearchBox;
        private int _listTop, _listBottom, _chipY;

        // ==================================================================
        // painting
        // ==================================================================

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Gfx.Setup(g);
            var R = ClientRectangle;
            int pad = S(16);

            using (var b = new SolidBrush(Theme.Base)) g.FillRectangle(b, R);
            Neon.Grid(g, R, S(20), Theme.Alpha(Theme.Grid, 120));
            Neon.Scanlines(g, R, Theme.Neon, S(3), 8);
            Neon.RoundOutline(g, new Rectangle(0, 0, W(R) - 1, R.Height - 1), S(12),
                              Theme.Alpha(Theme.Neon, 165));
            Neon.GlowRect(g, new Rectangle(1, 1, W(R) - 3, R.Height - 3), Theme.Neon, S(12), 2);
            Neon.Brackets(g, R, S(5), S(14), Theme.Alpha(Theme.Neon, 140), 1.4f);

            // ---- header -----------------------------------------------------
            using (Bitmap mk = Assets.MarkTile(S(24), true, Theme.Neon))
                g.DrawImage(mk, pad, pad - S(1));
            Neon.Caps(g, "ALL NOTES", Theme.Ui(9.2f, FontStyle.Bold),
                      new Point(pad + S(33), pad + S(3)), Theme.Text, 2);
            Neon.Caps(g, "SEARCH ACROSS TITLES, BODIES AND TAGS", Theme.Ui(6.6f, FontStyle.Bold),
                      new Point(pad + S(33), pad + S(19)), Theme.TextFaint, 1);
            if (_sel.Count > 0)
            {
                string s = _sel.Count + " SELECTED";
                int sw = (int)Neon.CapsWidth(g, s, Theme.Ui(7f, FontStyle.Bold), 1);
                Neon.Caps(g, s, Theme.Ui(7f, FontStyle.Bold),
                          new Point(R.Width - pad - sw, pad + S(8)), Theme.Neon, 1);
            }

            // ---- search field -----------------------------------------------
            Neon.FillRound(g, _rSearchBox, S(6), Theme.Alpha(Theme.Surface, 230));
            Neon.RoundOutline(g, _rSearchBox, S(6),
                              _search.Focused ? Theme.Alpha(Theme.Neon, 170) : Theme.Alpha(Theme.Hairline, 200));
            var lens = Theme.Ui(9f, FontStyle.Bold);
            using (var p = new Pen(_search.Focused ? Theme.Neon : Theme.TextDim, 1.4f))
                g.DrawEllipse(p, _rSearchBox.X + S(11), _rSearchBox.Y + S(11), S(10), S(10));
            using (var p = new Pen(_search.Focused ? Theme.Neon : Theme.TextDim, 1.4f))
                g.DrawLine(p, _rSearchBox.X + S(20), _rSearchBox.Y + S(20),
                           _rSearchBox.X + S(25), _rSearchBox.Y + S(25));
            if (string.IsNullOrEmpty(_query))
                Neon.Caps(g, "TYPE TO SEARCH", Theme.Ui(6.8f, FontStyle.Bold),
                          new Point(_rSearchBox.X + S(34), _rSearchBox.Y + S(10)),
                          Theme.TextFaint, 1);
            if (!string.IsNullOrEmpty(_query))
            {
                var clear = new Rectangle(_rSearchBox.Right - S(26), _rSearchBox.Y + S(6), S(18), S(18));
                _rClear = clear;
                using (var p = new Pen(Theme.TextDim, 1.3f))
                {
                    g.DrawLines(p, new[]
                    {
                        new Point(clear.X + 4, clear.Y + 4), new Point(clear.Right - 4, clear.Bottom - 4)
                    });
                    g.DrawLines(p, new[]
                    {
                        new Point(clear.Right - 4, clear.Y + 4), new Point(clear.X + 4, clear.Bottom - 4)
                    });
                }
            }
            else _rClear = Rectangle.Empty;

            // filter chips (width driven by their own content so nothing collides)
            int cx = pad;
            cx = Chip(g, ref cx, S(24), "ALL", _filter == Filter.All, CountMatched());
            cx = Chip(g, ref cx, S(24), "ACTIVE", _filter == Filter.Active, CountState(false));
            _rArch = Rectangle.Empty;
            cx = Chip(g, ref cx, S(24), "ARCHIVED", _filter == Filter.Archived, CountState(true));
            _rAll = new Rectangle(_rAll.X, _chipY, Math.Max(1, cx - S(6) - _rAll.X), S(24));

            // ---- summary ------------------------------------------------------
            string sum = _view.Count + " SHOWN";
            if (_notes.Count != _view.Count) sum += "  /  " + _notes.Count + " TOTAL";
            Neon.Caps(g, sum, Theme.Ui(6.6f, FontStyle.Bold),
                      new Point(pad, _listTop - S(15)), Theme.TextDim, 1);
            Neon.DashRule(g, pad + S(90), R.Width - pad, _listTop - S(12), Theme.Alpha(Theme.Neon, 40));

            // ---- rows ----------------------------------------------------------
            PaintRows(g, pad, R);

            // ---- footer buttons -------------------------------------------------
            int fy = _listBottom + S(10);
            Neon.HLine(g, fy - S(6), pad, R.Width - pad, Theme.Alpha(Theme.Neon, 55));
            Btn(g, _rNew, "NEW", Theme.Neon, _hoverBtn == 1, true);
            Btn(g, _rImport, "IMPORT", Theme.TextDim, _hoverBtn == 2, false);
            Btn(g, _rExport, "EXPORT", Theme.Cyan, _hoverBtn == 3, false);
            Btn(g, _rExportMenu, "FORMAT", Theme.TextDim, _hoverBtn == 4, false);
            Btn(g, _rOpen, "OPEN", Theme.Neon, _hoverBtn == 6, _sel.Count > 0);
            Btn(g, _rDelete, "DELETE", Theme.Danger, _hoverBtn == 9, _sel.Count > 0);
            Btn(g, _rArchive, ArchiveLabel(), Theme.Cyan, _hoverBtn == 5, false);
        }

        /// <summary>Offer whichever move makes sense for what is selected.</summary>
        private string ArchiveLabel()
        {
            var s = Selection();
            foreach (Note n in s) if (!n.Archived) return "ARCHIVE";
            return s.Count > 0 ? "RESTORE" : "ARCHIVE";
        }

        private int CountState(bool archived)
        {
            int c = 0;
            foreach (Note n in _notes)
            {
                if (n.Archived != archived) continue;
                if (!n.Matches(_query)) continue;
                c++;
            }
            return c;
        }

        private static int W(Rectangle r) { return r.Width; }

        private int Chip(Graphics g, ref int x, int h, string label, bool on, int count)
        {
            var f = Theme.Ui(6.6f, FontStyle.Bold);
            int lw = (int)Neon.CapsWidth(g, label, f, 1);
            int cw = (int)g.MeasureString(count.ToString(), f).Width;
            int w = S(10) + lw + S(9) + cw + S(9);
            var r = new Rectangle(x, _chipY, w, h);
            x += w + S(6);

            if (on)
            {
                Neon.FillRound(g, r, S(5), Theme.Alpha(Theme.Neon, 34));
                Neon.RoundOutline(g, r, S(5), Theme.Alpha(Theme.Neon, 190));
            }
            else
            {
                Neon.RoundOutline(g, r, S(5), Theme.Alpha(Theme.Hairline, 200));
            }
            Neon.Caps(g, label, f, new Point(r.X + S(10), r.Y + (r.Height - S(9)) / 2),
                      on ? Theme.Neon : Theme.TextDim, 1);
            using (var b = new SolidBrush(on ? Theme.Alpha(Theme.Neon, 200) : Theme.TextFaint))
                g.DrawString(count.ToString(), f, b, r.Right - cw - S(9), r.Y + (r.Height - S(9)) / 2);
            return x;
        }

        private int CountMatched()
        {
            int c = 0;
            foreach (Note n in _notes) if (n.Matches(_query)) c++;
            return c;
        }

        private void Btn(Graphics g, Rectangle r, string label, Color tint, bool hot, bool primary)
        {
            if (primary)
            {
                Neon.FillRound(g, r, S(5), Theme.Alpha(tint, hot ? 62 : 36));
                Neon.RoundOutline(g, r, S(5), Theme.Alpha(tint, 190));
            }
            else
            {
                Neon.RoundOutline(g, r, S(5), Theme.Alpha(hot ? tint : Theme.Hairline, hot ? 190 : 190));
            }
            var f = Theme.Ui(6.9f, FontStyle.Bold);
            int tw = (int)Neon.CapsWidth(g, label, f, 1);
            Neon.Caps(g, label, f, new Point(r.X + (r.Width - tw) / 2, r.Y + (r.Height - S(9)) / 2),
                      primary ? tint : (hot ? tint : Theme.TextDim), 1);
        }

        private void PaintRows(Graphics g, int pad, Rectangle R)
        {
            _rows.Clear();
            int rh = S(ROW);
            int avail = _listBottom - _listTop;
            int per = Math.Max(1, avail / rh);

            if (_view.Count == 0)
            {
                string msg = string.IsNullOrEmpty(_query)
                    ? (_filter == Filter.Archived ? "NOTHING ARCHIVED" : "NO NOTES YET")
                    : "NO MATCH FOR \"" + _query.ToUpperInvariant() + "\"";
                var f = Theme.Ui(8f, FontStyle.Bold);
                SizeF sz = g.MeasureString(msg, f);
                Neon.Caps(g, msg, f,
                          new Point(R.Width / 2 - (int)Neon.CapsWidth(g, msg, f, 1) / 2,
                                    _listTop + S(30)), Theme.TextFaint, 1);
                Neon.DashRule(g, R.Width / 2 - S(70), R.Width / 2 + S(70),
                              _listTop + S(52), Theme.Alpha(Theme.Neon, 50));
                return;
            }

            for (int i = 0; i < Math.Min(per, _view.Count); i++)
            {
                Note n = _view[i];
                var row = new Rectangle(pad, _listTop + i * rh, R.Width - pad * 2, rh - S(5));
                _rows.Add(row);
                bool sel = _sel.Contains(n);
                bool hot = _hover == i;
                Color acc = n.AccentColor;

                if (sel)
                {
                    Neon.FillRound(g, row, S(6), Theme.Alpha(acc, 40));
                    Neon.RoundOutline(g, row, S(6), Theme.Alpha(acc, 200));
                }
                else if (hot)
                {
                    Neon.FillRound(g, row, S(6), Theme.Alpha(Theme.SurfaceHi, 200));
                    Neon.RoundOutline(g, row, S(6), Theme.Alpha(Theme.Hairline, 220));
                }
                else
                {
                    Neon.RoundOutline(g, row, S(6), Theme.Alpha(Theme.Hairline, 120));
                }

                // colour bar
                var bar = new Rectangle(row.X + S(7), row.Y + S(7), S(3), row.Height - S(14));
                using (var b = new SolidBrush(acc)) g.FillRectangle(b, bar);

                // checkbox
                var cb = new Rectangle(row.X + S(17), row.Y + (row.Height - S(12)) / 2, S(12), S(12));
                Neon.RoundOutline(g, cb, S(3), Theme.Alpha(sel ? acc : Theme.Hairline, 220));
                if (sel)
                {
                    using (var p = new Pen(Theme.ReadableInk(acc), 1.6f))
                        g.DrawLines(p, new[]
                        {
                            new Point(cb.X + 3, cb.Y + 6), new Point(cb.X + 5, cb.Y + 9),
                            new Point(cb.X + 9, cb.Y + 3)
                        });
                }

                int tx = cb.Right + S(12);
                var fTitle = Theme.Ui(9f, FontStyle.Bold);
                Font fT = n.Archived ? Theme.Ui(8.6f, FontStyle.Regular | FontStyle.Strikeout) : fTitle;
                Color tc = n.Archived ? Theme.TextDim : Theme.Text;
                g.DrawString(n.DisplayTitle, fT, new SolidBrush(tc), tx, row.Y + S(8));

                var fMeta = Theme.Ui(6.5f, FontStyle.Regular);
                string meta = n.EditedAgo.ToUpperInvariant()
                             + (n.Pinned ? "  \u2022  PINNED" : "")
                             + (string.IsNullOrEmpty(n.Tags) ? "" : "  \u2022  #" + n.Tags.ToUpperInvariant());
                using (var b = new SolidBrush(Theme.TextFaint))
                    g.DrawString(meta, fMeta, b, tx, row.Y + row.Height - S(17));

                // preview on the right. It is measured and trimmed to the free gap so it
                // can never overrun the title; drawing it clipped instead lets GDI+
                // smear the glyphs on top of each other.
                var fPrev = Theme.Ui(7.4f, FontStyle.Regular);
                int titleW = (int)g.MeasureString(n.DisplayTitle, fT).Width;
                int prevX = Math.Min(row.Right - S(14) - S(30), tx + titleW + S(24));
                int prevW = row.Right - S(14) - prevX - (n.Archived ? S(66) : 0);
                string prev = Neon.Fit(g, n.Preview, fPrev, prevW);
                if (prev.Length > 0)
                    using (var b = new SolidBrush(Theme.TextDim))
                        g.DrawString(prev, fPrev, b, row.Right - S(14) - g.MeasureString(prev, fPrev).Width,
                                      row.Y + S(11));

                // archived chip sits above the preview, hard right
                if (n.Archived)
                    Chip2(g, new Rectangle(row.Right - S(66), row.Y + S(6), S(56), S(14)),
                          "ARCHIVED", Theme.TextDim);
            }

            if (_view.Count > per)
            {
                string more = _view.Count + " NOTES  \u2022  RESIZE TO SEE MORE";
                var f = Theme.Ui(6.4f, FontStyle.Bold);
                Neon.Caps(g, more, f, new Point(pad + S(4), _listTop + per * rh + S(4)),
                          Theme.TextFaint, 1);
            }
        }

        private void Chip2(Graphics g, Rectangle r, string label, Color tint)
        {
            Neon.RoundOutline(g, r, S(3), Theme.Alpha(tint, 150));
            var f = Theme.Ui(6f, FontStyle.Bold);
            int tw = (int)Neon.CapsWidth(g, label, f, 1);
            Neon.Caps(g, label, f, new Point(r.X + (r.Width - tw) / 2, r.Y + (r.Height - S(8)) / 2),
                      tint, 1);
        }

        // ==================================================================
        // input
        // ==================================================================

        private Rectangle _rClear;

        private int RowAt(Point p)
        {
            for (int i = 0; i < _rows.Count; i++) if (_rows[i].Contains(p)) return i;
            return -1;
        }

        private int BtnAt(Point p)
        {
            if (_rNew.Contains(p)) return 1;
            if (_rImport.Contains(p)) return 2;
            if (_rExport.Contains(p)) return 3;
            if (_rExportMenu.Contains(p)) return 4;
            if (_rArchive.Contains(p)) return 5;
            if (_rOpen.Contains(p)) return 6;
            if (_rDelete.Contains(p)) return 9;
            if (!_rClear.IsEmpty && _rClear.Contains(p)) return 7;
            if (p.Y >= _chipY && p.Y <= _chipY + S(24) && p.X >= _rAll.Left && p.X < _rAll.Right)
                return 8;
            return 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hr = RowAt(e.Location);
            int hb = BtnAt(e.Location);
            if (hr != _hover || hb != _hoverBtn)
            {
                _hover = hr;
                _hoverBtn = hb;
                Cursor = (hr >= 0 || hb > 0) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1 || _hoverBtn != 0) { _hover = -1; _hoverBtn = 0; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Point p = e.Location;

            int b = BtnAt(p);
            if (b == 7) { _search.Text = ""; return; }
            if (b == 8)
            {
                // pick the chip under the pointer: nearest label boundary
                int rel = p.X - _rAll.Left;
                int total = Math.Max(1, _rAll.Width);
                int third = total / 3;
                SetFilter(rel < third ? Filter.All : (rel < third * 2 ? Filter.Active : Filter.Archived));
                return;
            }
            if (b == 1) { Close(); _app.NewNote(); return; }
            if (b == 2) { DoImport(); return; }
            if (b == 3) { DoExport("md"); return; }
            if (b == 4) { ShowFormatMenu(); return; }
            if (b == 5) { ToggleArchiveSel(); return; }
            if (b == 6) { OpenSelected(); return; }
            if (b == 9) { DeleteSel(); return; }

            int r = RowAt(p);
            if (r >= 0)
            {
                Note n = _view[r];
                bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;
                if (ctrl)
                {
                    if (_sel.Contains(n)) _sel.Remove(n); else _sel.Add(n);
                }
                else if (e.Button == MouseButtons.Right)
                {
                    if (!_sel.Contains(n)) { _sel.Clear(); _sel.Add(n); }
                    Invalidate();
                    return;
                }
                else
                {
                    _sel.Clear();
                    _sel.Add(n);
                }
                Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int r = RowAt(e.Location);
            if (r >= 0) { OpenSelected(); }
        }

        private void OnGlobalKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            if (ctrl)
            {
                switch (keyData & ~Keys.Control)
                {
                    case Keys.A:
                        _sel.Clear();
                        _sel.AddRange(_view);
                        Invalidate();
                        return true;
                }
            }
            if (keyData == Keys.F5) { Rebuild(); return true; }
            if (keyData == Keys.Delete && _sel.Count > 0) { DeleteSel(); return true; }
            if (keyData == Keys.Enter && _sel.Count > 0) { OpenSelected(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ==================================================================
        // commands
        // ==================================================================

        private List<Note> Selection()
        {
            if (_sel.Count > 0) return new List<Note>(_sel);
            return new List<Note>(_view);
        }

        private void OpenSelected()
        {
            var s = Selection();
            if (s.Count == 0) return;
            Hide();
            foreach (Note n in s) _app.OpenNote(n.Id);
        }

        private void ToggleArchiveSel()
        {
            var s = Selection();
            if (s.Count == 0) return;
            bool anyActive = false;
            foreach (Note n in s) if (!n.Archived) { anyActive = true; break; }
            foreach (Note n in s)
            {
                n.Archived = anyActive;
                n.Edited = DateTime.UtcNow.Ticks;
                _store.Save(n);
            }
            _sel.Clear();
            _app.RequestDeckRefresh();
            Rebuild();
        }

        private void DeleteSel()
        {
            // Selection() deliberately falls back to "everything on screen" so OPEN
            // can act on the whole list without the user ticking each row. Deleting
            // must never inherit that fallback: with nothing ticked, one click here
            // would offer to remove every note in the view.
            if (_sel.Count == 0) return;
            var s = new List<Note>(_sel);
            if (s.Count == 0) return;
            Dialogs.ConfirmDelete(this,
                s.Count == 1 ? s[0].DisplayTitle : s.Count + " notes",
                () =>
                {
                    foreach (Note n in s)
                    {
                        _app.CloseNote(n.Id);
                        _app.Notes.Remove(n);
                        _store.Delete(n);
                    }
                    _sel.Clear();
                    _app.RequestDeckRefresh();
                    Rebuild();
                });
        }

        private void DoExport(string kind)
        {
            var s = Selection();
            if (s.Count == 0) return;
            _app.Export(s, kind);
        }

        private void ShowFormatMenu()
        {
            var m = new ContextMenuStrip
            {
                RenderMode = ToolStripRenderMode.Professional,
                Font = Theme.Ui(9f, FontStyle.Regular),
                BackColor = Theme.SurfaceHi,
                ForeColor = Theme.Text,
                ShowImageMargin = false,
            };
            m.Renderer = new NeonMenuRenderer();
            string[][] kinds =
            {
                new[] { "MARKDOWN (.md)", "md" },
                new[] { "PLAIN TEXT (.txt)", "txt" },
                new[] { "SINGLE DOCUMENT", "html" },
                new[] { "DNOTES ARCHIVE", "archive" },
            };
            foreach (string[] k in kinds)
            {
                var it = new ToolStripMenuItem(k[0]);
                it.ForeColor = Theme.Text;
                it.BackColor = Theme.SurfaceHi;
                string kind = k[1];
                it.Click += (s, e) => DoExport(kind);
                m.Items.Add(it);
            }
            m.Show(this, new Point(_rExportMenu.Left, _rExportMenu.Bottom + 2));
        }

        private void DoImport()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "DNotes archive (*.dnotes)|*.dnotes|Text (*.txt)|*.txt|Markdown (*.md)|*.md|All files (*.*)|*.*";
                dlg.Title = "Import into DNotes";
                if (dlg.ShowDialog(this) == DialogResult.OK) _app.Import(dlg.FileName);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _app.NotesReloaded -= null;
            base.OnFormClosed(e);
        }
    }
}
