using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DNotes.Windows
{
    using Core;

    /// <summary>
    /// One expanded, writable sticky note.
    ///
    /// LAYOUT CONTRACT - the reason typing always works:
    /// the form paints its own chrome (background, grid, scanlines, brackets,
    /// header, footer, rails) and places exactly two child controls on top:
    /// the title field and the editor. There is no overlay, no transparent
    /// "effects" panel and no third control anywhere near the editor, because
    /// any of those would swallow the mouse and make the note look alive while
    /// refusing to accept a word. Decoration lives in OnPaint, which is behind
    /// the editor; hit-testing therefore stays completely ordinary Win32.
    /// </summary>
    internal sealed class NoteForm : Form
    {
        // ---- metrics (unscaled; run through S()) ---------------------------
        private const int M_PAD = 11;         // outer margin
        private const int M_RAIL = 3;         // accent rail left of the text
        private const int M_HEAD = 46;        // header height
        private const int M_FOOT = 40;        // footer height
        private const int M_MARK = 22;        // logo square in the header
        private const int M_BTN = 22;         // small square button
        private const int M_FIND = 76;        // footer height while find is open
        private const int M_FOOT2 = 62;       // footer height when the palette needs its own row

        // resize grips on a borderless window: the shell has no OS frame, so the
        // note has to grow by hand or it is stuck at whatever size it was born.
        private const int M_EDGE = 6;         // drag band along each side
        private const int M_CORNER = 18;      // square target for the diagonals
        // 280 is not a round guess: the footer's own measured width (3 format
        // buttons + ARCHIVE + DELETE) runs out of room below 275 logical, and a
        // note narrower than that stacked the buttons on top of each other.
        private const int M_MINW = 280;       // same limits as the constructor
        private const int M_MINH = 190;
        private const int M_MAXW = 900;
        private const int M_MAXH = 1100;
        private const int M_DEFW = 380;       // new notes open at a usable size
        private const int M_DEFH = 430;

        private const int RD_L = 1, RD_R = 2, RD_T = 4, RD_B = 8;


        private readonly App _app;
        private Note _note;
        private readonly Store _store;

        private readonly NeonTextBox _editor;
        private readonly TextBox _title;

        // header buttons
        private Rectangle _rPin, _rShade, _rClose;
        // footer: format toggles, swatches, actions
        private readonly Rectangle[] _rFmt = new Rectangle[3];
        private readonly Rectangle[] _rAccent = new Rectangle[Theme.Accents.Length];
        private Rectangle _rArchive, _rDelete, _rSave;
        private Rectangle _rFindBox, _rFindNext, _rFindPrev, _rFindClose;
        private int _rFindLabel;

        // state
        private bool _loading;
        private string _status = "READY";
        private DateTime _statusAt = DateTime.Now;
        private bool _statusAlert;
        private bool _pinned;
        private int _fmtState;                  // bit0 bold, bit1 italic, bit2 underline
        private bool _findOpen;
        private int _findFrom;
        private Point _dragOrigin;
        private bool _dragging;
        private Rectangle _dragBounds;

        // resize
        private int _resizeDir;                // RD_* while a grip is being dragged
        private Point _resizeOrigin;
        private Rectangle _resizeStart;


        public Note Note { get { return _note; } }
        public string NoteId { get { return _note == null ? null : _note.Id; } }
        public bool FindOpen { get { return _findOpen; } }

        public event EventHandler NoteClosed;
        public event EventHandler NoteChanged;

        // ==================================================================
        public NoteForm(App app, Store store, Note note, bool activate)
        {
            _app = app;
            _store = store;
            _note = note;
            _pinned = note.Pinned;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Base;
            DoubleBuffered = true;
            KeyPreview = false;            // keys go straight to the editor
            AutoScaleMode = AutoScaleMode.None;
            MinimumSize = new Size(S(M_MINW), S(M_MINH));
            Text = "DNotes";

            ApplyRegion();
            ApplyExStyle();

            // ---- the two child controls, and only these two ----------------
            _title = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Base,
                ForeColor = Theme.Text,
                Font = Theme.Ui(10.5f, FontStyle.Bold),
                ReadOnly = false,
                Enabled = true,
                TabStop = true,
                MaxLength = 90,
                HideSelection = false,
                TextAlign = HorizontalAlignment.Left,
            };
            _title.TextChanged += (s, e) =>
            {
                if (_loading) return;
                _note.Title = _title.Text;
                Touch();
            };
            _title.KeyDown += OnTitleKeyDown;
            _title.GotFocus += (s, e) => { SetStatus("EDITING TITLE", false); Invalidate(); LayoutChildren(); };
            _title.LostFocus += (s, e) => { SaveNow(true); Invalidate(); };

            _editor = new NeonTextBox
            {
                RailWidth = 0,
                ShowCaretGlyph = true,
                BackColor = Theme.Base,
                ForeColor = Theme.Text,
                Font = BodyFont(),
                BorderStyle = BorderStyle.None,
                ReadOnly = false,
                Enabled = true,
                TabStop = true,
                HideSelection = false,
                DetectUrls = true,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Margin = new Padding(0),
            };
            _editor.TextChanged += (s, e) =>
            {
                if (_loading) return;
                Touch();
                SetStatus("EDITING", true);
            };
            _editor.GotFocus += (s, e) => { _editor.ResetCaretBlink(); SetStatus("READY", false); };
            _editor.CaretStateChanged += (s, e) => { if (IsHandleCreated) Invalidate(); };
            _editor.MouseDown += (s, e) => SetStatus("READY", false);

            Controls.Add(_editor);
            Controls.Add(_title);

            _editor.ContextMenuStrip = BuildEditorMenu();

            // Wire the layout events BEFORE anything is sized or placed. If the
            // Resize hook is attached after the first SetBounds it never sees
            // that change, and the rounded region stays at the form's default
            // size - which silently crops the footer off the window.
            Resize += (s, e) => { ApplyRegion(); LayoutChildren(); Invalidate(); };
            Move += (s, e) => { if (!_dragging) RememberGeometry(); };
            Shown += OnFirstShown;
            FormClosing += OnFormClosing;

            ApplyGeometry(note);
            LoadNote(note);
            ApplyRegion();
            LayoutChildren();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Second chance once the real window exists: the region is only
            // meaningful after handle creation.
            ApplyRegion();
            LayoutChildren();
            Invalidate();
        }

        private Font BodyFont()
        {
            return Theme.Mono(_app.Settings.FontSize, FontStyle.Regular);
        }

        // ==================================================================
        // setup
        // ==================================================================

        private void ApplyExStyle()
        {
            if (!IsHandleCreated) return;
            int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_APPWINDOW;
            ex &= ~Native.WS_EX_TRANSPARENT;     // must stay clickable
            ex &= ~Native.WS_EX_NOACTIVATE;      // must stay typeable
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);
        }

        private void ApplyRegion()
        {
            int r = S(14);
            Shape.Round(this, r);
        }

        private void ApplyGeometry(Note n)
        {
            // W and H are stored in logical units so a note keeps its apparent
            // size when the display scale changes. Every internal metric is
            // scaled by S(), so the window itself has to be scaled too -
            // otherwise the chrome overflows a window sized for another DPI
            // and the footer ends up drawn on top of itself.
            int w = n.W > 0 ? S(n.W) : S(M_DEFW);
            int h = n.H > 0 ? S(n.H) : S(M_DEFH);
            w = Math.Max(S(M_MINW), Math.Min(w, S(M_MAXW)));
            h = Math.Max(S(M_MINH), Math.Min(h, S(M_MAXH)));
            Size = new Size(w, h);

            if (n.X < -30000 || n.Y < -30000)
            {
                // first open: cascade so two new notes do not land on top of
                // each other
                var wa = Screen.FromControl(this).WorkingArea;
                int idx = _app.OpenNoteCount % 8;
                n.X = wa.Right - w - S(46) - idx * S(26);
                n.Y = Math.Max(wa.Top + S(8),
                               Math.Min(wa.Bottom - h - S(8),
                                        wa.Top + S(40) + idx * S(24)));
            }
            Location = new Point(n.X, n.Y);
        }

        private void RememberGeometry()
        {
            if (WindowState == FormWindowState.Minimized) return;
            _note.X = Left;
            _note.Y = Top;
            _note.W = Ui.Logical(Width);
            _note.H = Ui.Logical(Height);
        }

        private void LoadNote(Note n)
        {
            _loading = true;
            try
            {
                _title.Text = n.Title ?? "";
                _editor.Font = BodyFont();
                if (string.IsNullOrEmpty(n.Rtf)) _editor.Text = "";
                else
                {
                    try { _editor.Rtf = n.Rtf; }
                    catch { _editor.Text = n.PlainText; }
                }
                _editor.ApplyBodyStyle(BodyFont(), Theme.Text);
                _editor.SelectionStart = 0;
                _editor.SelectionLength = 0;
            }
            finally { _loading = false; }
            SetStatus(n.Edited == 0 ? "NEW" : "SAVED", false);
            LayoutChildren();
        }

        private void OnFirstShown(object sender, EventArgs e)
        {            if (_app.ActivateOnOpen)
            {
                // Make the window take real keyboard focus before the caret is
                // placed, otherwise the caret blinks in a window that cannot
                // receive a single keystroke.
                Native.ForceForeground(this);
                _editor.Focus();
                Native.SetFocus(_editor.Handle);
                _editor.SelectionStart = _editor.TextLength;
                _editor.SelectionLength = 0;
                _editor.ResetCaretBlink();
            }
            else
            {
                _editor.SelectionStart = _editor.TextLength;
                _editor.SelectionLength = 0;
            }
        }

        // ==================================================================
        // layout
        // ==================================================================

        private int S(int v) { return (int)Math.Round(v * Ui.Scale); }

        /// <summary>Button width that exactly fits a letter-spaced label.</summary>
        private int FitWidth(Graphics g, Font f, string label)
        {
            return (int)Math.Ceiling(Neon.CapsWidth(g, label, f, 1));
        }

        /// <summary>Every clickable rect in the footer, for the self-test.</summary>
        public List<Rectangle> FooterRects
        {
            get
            {
                var l = new List<Rectangle>();
                for (int i = 0; i < 3; i++) l.Add(_rFmt[i]);
                for (int i = 0; i < _rAccent.Length; i++)
                    if (!_rAccent[i].IsEmpty) l.Add(_rAccent[i]);
                if (!_rArchive.IsEmpty) l.Add(_rArchive);
                if (!_rDelete.IsEmpty) l.Add(_rDelete);
                if (!_rSave.IsEmpty) l.Add(_rSave);
                return l;
            }
        }

        /// <summary>The writing area, for the self-test.</summary>
        public Rectangle EditorBounds { get { return _editor.Bounds; } }

        /// <summary>The interior of the window, for the self-test.</summary>
        public Rectangle InteriorBounds { get { return ContentRect; } }

        /// <summary>Pin, shade, close - in that order. For the self-test.</summary>
        public List<Rectangle> HeaderRects
        {
            get { return new List<Rectangle> { _rPin, _rShade, _rClose }; }
        }

        private int HeadH { get { return S(M_HEAD); } }
        private const int P_SW = 15;      // swatch size
        private const int P_GAP = 5;

        /// <summary>
        /// Footer height. Grows to two rows on a narrow note rather than
        /// hiding colours off the end of the palette: all six accents stay
        /// reachable whatever size the window happens to be.
        /// </summary>
        private int FootH
        {
            get
            {
                if (_findOpen) return S(M_FIND);
                if (PaletteNeedsOwnRow()) return S(M_FOOT2);
                return S(M_FOOT);
            }
        }

        private bool PaletteNeedsOwnRow()
        {
            return PaletteRoom(S(M_PAD)) < Theme.Accents.Length * (S(P_SW) + S(P_GAP));
        }

        /// <summary>Width left for the swatches once the buttons have their say.</summary>
        private int PaletteRoom(int pad)
        {
            int bh = S(22);
            int fmtRight = pad + 3 * (bh + S(3));
            int arcW = S(70), delW = S(70);
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    var f = Theme.Ui(7f, FontStyle.Bold);
                    arcW = FitWidth(g, f, _note.Archived ? "RESTORE" : "ARCHIVE") + S(24);
                    delW = FitWidth(g, f, "DELETE") + S(24);
                }
            }
            catch { }
            int right = ClientSize.Width - pad - delW - S(5) - arcW - S(9);
            return Math.Max(0, right - (fmtRight + S(10)));
        }

        private Rectangle ContentRect
        {
            get { return new Rectangle(S(M_PAD), S(M_PAD), Width - S(M_PAD) * 2, Height - S(M_PAD) * 2); }
        }

        private void LayoutChildren()
        {
            int pad = S(M_PAD), rail = S(M_RAIL);
            Rectangle c = ContentRect;
            int head = HeadH;
            int foot = FootH;

            // ---- header -----------------------------------------------------
            int markY = c.Top + (head - S(M_MARK)) / 2;
            int titleX = c.Left + S(M_MARK) + S(9);
            int btnZone = S(M_BTN) * 3 + S(6);
            _rClose = new Rectangle(c.Right - S(M_BTN), markY, S(M_BTN), S(M_BTN));
            _rPin = new Rectangle(_rClose.X - S(M_BTN) - S(4), markY, S(M_BTN), S(M_BTN));
            _rShade = new Rectangle(_rPin.X - S(M_BTN) - S(4), markY, S(M_BTN), S(M_BTN));

            int titleW = _rShade.Left - S(10) - titleX;
            // Leave room for the status readout. The title field is a child
            // control and paints after the form, so anything drawn under it in
            // the header would simply be hidden.
            try
            {
                using (Graphics gg = CreateGraphics())
                {
                    float sw = Neon.CapsWidth(gg, _status, Theme.Ui(7.2f, FontStyle.Bold), 1);
                    titleW = _rShade.Left - S(10) - (int)sw - S(22) - titleX;
                }
            }
            catch { }
            if (titleW < S(40)) titleW = S(40);
            _title.SetBounds(titleX, c.Top + (head - _title.Height) / 2, titleW, _title.Height);
            // blend into the header instead of reading as a grey input box
            _title.BackColor = _title.Focused ? Theme.SurfaceHi : Theme.Base;

            // ---- the editor: the only thing in this band, full size ----------
            int edTop = c.Top + head + S(7);
            int edH = c.Height - head - foot - S(14);
            if (edH < S(30)) edH = S(30);
            _editor.SetBounds(c.Left + rail, edTop, c.Width - rail, edH);

            // ---- footer -----------------------------------------------------
            int fy = c.Bottom - foot;
            if (_findOpen)
            {
                int bh = S(26);
                int by = fy + S(8);
                _rFindClose = new Rectangle(c.Right - bh, by, bh, bh);
                _rFindNext = new Rectangle(_rFindClose.X - bh - S(4), by, bh, bh);
                _rFindPrev = new Rectangle(_rFindNext.X - bh - S(4), by, bh, bh);
                _rFindLabel = _rFindPrev.Left - S(4);
                _rFindBox = new Rectangle(c.Left, by,
                                          Math.Max(S(60), _rFindLabel - c.Left - S(8)), bh);
            }
            else
            {
                int by = fy + S(10), bh = S(22);
                bool twoRows = PaletteNeedsOwnRow();

                // Row 1: format toggles left, the two text actions right. Both
                // are sized from their own measured labels so they can never
                // overflow or collide.
                int delW = S(70), arcW = S(70);
                try
                {
                    using (Graphics g = CreateGraphics())
                    {
                        var f = Theme.Ui(7f, FontStyle.Bold);
                        delW = FitWidth(g, f, "DELETE") + S(24);
                        arcW = FitWidth(g, f, _note.Archived ? "RESTORE" : "ARCHIVE") + S(24);
                    }
                }
                catch { }

                int right = c.Right;
                _rDelete = new Rectangle(right - delW, by, delW, bh);
                right = _rDelete.X - S(5);
                _rArchive = new Rectangle(right - arcW, by, arcW, bh);
                _rSave = Rectangle.Empty;

                for (int i = 0; i < 3; i++)
                    _rFmt[i] = new Rectangle(c.Left + i * (bh + S(3)), by, bh, bh);

                int sw = S(P_SW), gap = S(P_GAP), step = sw + gap;
                for (int i = 0; i < _rAccent.Length; i++) _rAccent[i] = Rectangle.Empty;

                if (twoRows)
                {
                    // palette gets the whole of row 2, centred
                    int total = Theme.Accents.Length * step - gap;
                    int sx = c.Left + Math.Max(0, (c.Width - total) / 2);
                    for (int i = 0; i < _rAccent.Length; i++)
                        _rAccent[i] = new Rectangle(sx + i * step, by + bh + S(9), sw, sw);
                    _swatchesShown = Theme.Accents.Length;
                    _moreLabelX = 0;
                }
                else
                {
                    int room = PaletteRoom(c.Left);
                    int fit = Math.Min(_rAccent.Length, room / step);
                    // The palette has to stop short of the archive/delete buttons.
                    // Right aligning it to the content edge instead ran the
                    // swatches underneath them on any note wide enough to show
                    // all six, because fit only ever measured the gap, it never
                    // positioned against it.
                    int limit = c.Right - delW - S(5) - arcW - S(9);
                    int total = fit * step - gap;
                    int floor = c.Left + 3 * (bh + S(3)) + S(10);
                    int sx = Math.Max(floor, limit - total);
                    for (int i = 0; i < fit; i++)
                        _rAccent[i] = new Rectangle(sx + i * step, by + (bh - sw) / 2, sw, sw);
                    _swatchesShown = fit;
                    _moreLabelX = sx - S(16);
                }
            }
        }

        // ==================================================================
        // painting
        // ==================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // Decoration is cosmetic. If any of it ever throws we still want a
            // clean, opaque, readable note - never a half-painted window that
            // looks broken and never a hole where the editor should be.
            try { PaintChrome(e, g); }
            catch (Exception ex)
            {
                Log.Write("note paint: " + ex);
                using (var b = new SolidBrush(Theme.Base)) g.FillRectangle(b, ClientRectangle);
                Neon.RoundOutline(g, new Rectangle(0, 0, Width - 1, Height - 1), S(14),
                                  Theme.Alpha(_note.AccentColor, 140), 1f);
            }
        }

        private void PaintChrome(PaintEventArgs e, Graphics g)
        {
            Gfx.Setup(g);
            Gfx.Setup(g);
            Rectangle c = ContentRect;
            Color accent = _note.AccentColor;

            // ---- shell -------------------------------------------------------
            using (var b = new SolidBrush(Theme.Base)) g.FillRectangle(b, ClientRectangle);
            using (var b = new SolidBrush(Theme.Alpha(accent, 10)))
                g.FillRectangle(b, ClientRectangle);

            Neon.Grid(g, ClientRectangle, S(18), Theme.Alpha(Theme.Grid, 150));
            Neon.Scanlines(g, ClientRectangle, Theme.Neon, S(3), 9);

            // inner panel behind the writing area, one shade up
            var inner = new Rectangle(c.Left, c.Top + HeadH, c.Width,
                                      c.Height - HeadH - FootH);
            Neon.FillRound(g, new Rectangle(inner.X, inner.Y, inner.Width, inner.Height + S(4)),
                           S(6), Theme.Alpha(Theme.Surface, 205));

            // ---- rules --------------------------------------------------------
            int headY = c.Top + HeadH + S(3);
            int footY = c.Bottom - FootH - S(3);
            Neon.HLine(g, headY, c.Left, c.Right, Theme.Alpha(accent, 70));
            Neon.HLine(g, footY, c.Left, c.Right, Theme.Alpha(accent, 70));
            Neon.DashRule(g, c.Left, c.Right, headY + S(2), Theme.Alpha(accent, 42));

            // ---- accent rail down the left -----------------------------------
            int rail = S(M_RAIL);
            Neon.Glow(g, (gg, p) => gg.DrawLine(p, c.Left + rail / 2f, c.Top, c.Left + rail / 2f, c.Bottom),
                     accent, 2);

            // ---- corner brackets ---------------------------------------------
            Neon.Brackets(g, new Rectangle(0, 0, Width, Height), S(4), S(13),
                          Theme.Alpha(accent, 150), 1.5f);

            // ---- frame ---------------------------------------------------------
            Neon.RoundOutline(g, new Rectangle(0, 0, Width - 1, Height - 1), S(14),
                              Theme.Alpha(accent, 120), 1f);
            Neon.GlowRect(g, new Rectangle(1, 1, Width - 3, Height - 3), accent, S(14), 2);

            // The shell has no OS frame, so the grip hint is the only thing that
            // tells you a note can be resized. Without it the note looks stuck.
            PaintGrip(g, accent);


            PaintHeader(g, c, accent);
            PaintFooter(g, c, accent);

            // the title field sits on the header background; underline it when
            // it has focus so it still reads as editable
            if (_title.Focused)
            {
                Neon.HLine(g, _title.Bottom + S(2), _title.Left, _title.Right,
                           Theme.Alpha(accent, 200));
            }

            // ---- outside dimming so the note reads as floating ----------------
            using (var p = new Pen(Theme.Alpha(Color.Black, 150), 1f))
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        /// <summary>Three diagonal strokes in the corner that resizes the note.</summary>
        private void PaintGrip(Graphics g, Color accent)
        {
            int x = Width - S(8), y = Height - S(8);
            using (var p = new Pen(Theme.Alpha(accent, 165), 1.2f))
                for (int i = 0; i < 3; i++)
                    g.DrawLine(p, x - S(3) - i * S(4), y, x, y - S(3) - i * S(4));
        }

        private void PaintHeader(Graphics g, Rectangle c, Color accent)
        {
            int markY = c.Top + (HeadH - S(M_MARK)) / 2;

            // logo, with a bloom so it reads as lit
            using (Bitmap bmp = Assets.MarkTile(S(M_MARK), true, accent))
                g.DrawImage(bmp, c.Left, markY);

            // status, right of the title
            string st = _status;
            var sf = Theme.Ui(7.2f, FontStyle.Bold);
            double age = (DateTime.Now - _statusAt).TotalSeconds;
            bool flash = _statusAlert && age < 1.1 && ((int)(age * 8) % 2 == 0);
            Color stc = _statusAlert ? Theme.Neon : Theme.TextDim;
            if (flash) stc = Theme.Alpha(Theme.Neon, 90);

            int px = _rShade.Left - S(10);
            Neon.Caps(g, st, sf, new Point(px - (int)Neon.CapsWidth(g, st, sf) - S(13), markY + S(2)),
                      stc, 1);

            // status pip
            var pip = new Rectangle(px - S(9), markY + S(6), S(5), S(5));
            using (var b = new SolidBrush(stc)) g.FillEllipse(b, pip);
            if (!_statusAlert && age < 1.4)
                Neon.Glow(g, (gg, p) => gg.DrawEllipse(p, pip.X, pip.Y, pip.Width, pip.Height),
                          accent, 2);

            PaintIconButton(g, _rPin, _pinned ? "PIN_ON" : "PIN", accent, _rPin.Contains(_hot));
            PaintIconButton(g, _rShade, "SHADE", accent, _rShade.Contains(_hot));
            PaintCloseButton(g, _rClose, accent, _rClose.Contains(_hot));
        }

        private void PaintFooter(Graphics g, Rectangle c, Color accent)
        {
            int fy = c.Bottom - FootH;

            if (_findOpen)
            {
                // find bar occupies the footer; the editor is never covered
                Neon.FillRound(g, new Rectangle(c.Left, fy + S(6), c.Width, FootH - S(10)), S(6),
                               Theme.Alpha(Theme.SurfaceHi, 220));
                Neon.RoundOutline(g, new Rectangle(c.Left, fy + S(6), c.Width, FootH - S(10)), S(6),
                                  Theme.Alpha(accent, 90));

                int cx = _rFindBox.Left + S(8);
                int ty = _rFindBox.Top + (_rFindBox.Height - S(13)) / 2;
                if (_editor.SelectionLength > 0)
                    Neon.Caps(g, "FIND", Theme.Ui(7.2f, FontStyle.Bold),
                              new Point(cx, ty), Theme.Alpha(accent, 200), 1);
                using (var br = new SolidBrush(Theme.Neon))
                    g.DrawArc(new Pen(br, 1.4f), _rFindBox.Left + S(6),
                              _rFindBox.Top + (_rFindBox.Height - S(10)) / 2, S(10), S(10),
                              -60, 220);
                using (Pen pen = new Pen(Theme.Alpha(accent, 150)))
                    g.DrawLine(pen, _rFindBox.Left + S(9), _rFindBox.Top + _rFindBox.Height / 2 + S(2),
                               _rFindBox.Left + S(13), _rFindBox.Top + _rFindBox.Height / 2 + S(6));

                // results readout
                string res = _findHits > 0
                    ? _findHits + (_findHits == 1 ? " HIT" : " HITS")
                    : "NO HIT";
                int rx = _rFindBox.Right - S(8) - (int)Neon.CapsWidth(g, res, Theme.Ui(7.2f, FontStyle.Bold), 1);
                Neon.Caps(g, res, Theme.Ui(7.2f, FontStyle.Bold),
                          new Point(rx, _rFindBox.Top + (_rFindBox.Height - S(10)) / 2),
                          _findHits > 0 ? Theme.Neon : Theme.Danger, 1);

                PaintTextButton(g, _rFindPrev, "PREV", accent, false, false, _rFindPrev == _hotFoot);
                PaintTextButton(g, _rFindNext, "NEXT", accent, true, false, _rFindNext == _hotFoot);
                PaintCloseButton(g, _rFindClose, accent, _rFindClose == _hotFoot);
                return;
            }

            int by = _rFmt[0].Top, bh = _rFmt[0].Height;
            var ff = Theme.Ui(8.6f, FontStyle.Bold);

            // format toggles
            string[] glyphs = { "B", "I", "U" };
            for (int i = 0; i < 3; i++)
            {
                bool on = (_fmtState & (1 << i)) != 0;
                bool hot = _rFmt[i] == _hotFoot;
                if (on)
                {
                    Neon.FillRound(g, _rFmt[i], S(4), Theme.Alpha(accent, 46));
                    Neon.RoundOutline(g, _rFmt[i], S(4), Theme.Alpha(accent, 150));
                }
                else if (hot)
                {
                    Neon.FillRound(g, _rFmt[i], S(4), Theme.Alpha(accent, 22));
                    Neon.RoundOutline(g, _rFmt[i], S(4), Theme.Alpha(accent, 120));
                }
                else
                {
                    Neon.RoundOutline(g, _rFmt[i], S(4), Theme.Alpha(Theme.Hairline, 190));
                }
                FontStyle st = FontStyle.Bold;
                if (i == 1) st = FontStyle.Bold | FontStyle.Italic;
                if (i == 2) st = FontStyle.Underline | FontStyle.Bold;
                // NB: Theme hands out shared cached fonts. Never wrap one in a
                // using - disposing it would poison the cache for the whole app.
                var sf = Theme.Ui(8.6f, st);
                using (var b = new SolidBrush(on ? Theme.Neon : Theme.TextDim))
                {
                    SizeF sz = g.MeasureString(glyphs[i], sf);
                    g.DrawString(glyphs[i], sf, b,
                        _rFmt[i].X + (_rFmt[i].Width - sz.Width) / 2f,
                        _rFmt[i].Y + (_rFmt[i].Height - sz.Height) / 2f);
                }
            }

            // accent swatches
            for (int i = 0; i < _rAccent.Length; i++)
            {
                Rectangle r = _rAccent[i];
                if (r.IsEmpty) continue;
                bool sel = i == _note.Accent;
                bool hot = r == _hotFoot;
                Color col = Theme.Accents[i];
                if (sel)
                {
                    Neon.Glow(g, (gg, p) => gg.DrawRectangle(p, r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2),
                              col, 2);
                }
                Neon.FillRound(g, r, S(3), sel ? col : Theme.Alpha(col, hot ? 235 : 150));
                Neon.RoundOutline(g, Rectangle.Inflate(r, -1, -1), S(3),
                                  sel ? Theme.ReadableInk(col)
                                       : Theme.Alpha(hot ? col : Theme.Hairline, hot ? 255 : 120), 1f);
                if (sel)
                {
                    using (var b = new SolidBrush(Theme.ReadableInk(col)))
                        g.FillRectangle(b, r.X + r.Width / 2 - 1, r.Y + r.Height / 2 - 1, 2, 2);
                }
            }
            if (_swatchesShown > 0 && _swatchesShown < _rAccent.Length && _moreLabelX > 0)
            {
                // not enough room for the full palette: say so rather than clip
                Neon.Caps(g, "+" + (_rAccent.Length - _swatchesShown), Theme.Ui(6f, FontStyle.Bold),
                          new Point(_moreLabelX, _rFmt[0].Y + S(6)), Theme.TextFaint, 1);
            }

            // actions
            PaintTextButton(g, _rArchive, _note.Archived ? "RESTORE" : "ARCHIVE", accent,
                            false, false, _rArchive == _hotFoot);
            PaintTextButton(g, _rDelete, "DELETE", Theme.Danger,
                            false, true, _rDelete == _hotFoot);
        }

        private void PaintTextButton(Graphics g, Rectangle r, string label, Color tint,
                                     bool filled, bool danger = false, bool hot = false)
        {
            if (r.IsEmpty) return;
            if (filled || hot)
            {
                Neon.FillRound(g, r, S(4), Theme.Alpha(tint, hot ? (danger ? 60 : 48) : 40));
                Neon.RoundOutline(g, r, S(4),
                                  Theme.Alpha(tint, hot ? 230 : 170));
            }
            else
            {
                Neon.RoundOutline(g, r, S(4),
                                  Theme.Alpha(hot ? tint : Theme.Hairline, hot ? 230 : 200));
            }
            var f = Theme.Ui(7.2f, FontStyle.Bold);
            int tw = (int)Neon.CapsWidth(g, label, f, 1);
            Color ink = danger ? Theme.Alpha(Theme.Danger, 210)
                     : (filled || hot) ? tint : Theme.TextDim;
            Neon.Caps(g, label, f,
                      new Point(r.X + (r.Width - tw) / 2, r.Y + (r.Height - S(9)) / 2),
                      ink, 1);
        }

        private void PaintIconButton(Graphics g, Rectangle r, string kind, Color accent, bool hot)
        {
            if (r.IsEmpty) return;
            Neon.RoundOutline(g, r, S(4), Theme.Alpha(Theme.Hairline, hot ? 240 : 170));
            if (hot) Neon.FillRound(g, r, S(4), Theme.Alpha(accent, 34));

            Color c = hot ? Theme.Neon : Theme.TextDim;
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            if (kind.StartsWith("PIN"))
            {
                bool on = _pinned;
                Color pc = on ? accent : c;
                using (var p = new Pen(pc, 1.3f))
                {
                    g.DrawLine(p, cx - 3, cy - 4, cx + 3, cy + 2);
                    g.DrawLine(p, cx + 3, cy - 4, cx - 3, cy + 2);
                }
                if (on)
                {
                    using (var b = new SolidBrush(pc)) g.FillEllipse(b, cx - 5, cy - 5, 3, 3);
                }
            }
            else if (kind == "SHADE")
            {
                using (var p = new Pen(c, 1.3f))
                {
                    g.DrawArc(p, cx - 4, cy - 4, 8, 8, 180, 180);
                    g.DrawLine(p, cx - 4, cy + 4, cx + 4, cy + 4);
                }
            }
        }

        private void PaintCloseButton(Graphics g, Rectangle r, Color accent, bool hot)
        {
            if (r.IsEmpty) return;
            Neon.RoundOutline(g, r, S(4), Theme.Alpha(Theme.Hairline, hot ? 240 : 170));
            if (hot) Neon.FillRound(g, r, S(4), Theme.Alpha(Theme.Danger, 46));
            Color c = hot ? Theme.Danger : Theme.TextDim;
            using (var p = new Pen(c, 1.5f))
            {
                int d = 4;
                g.DrawLines(p, new[]
                {
                    new Point(r.X + r.Width/2 - d, r.Y + r.Height/2 - d),
                    new Point(r.X + r.Width/2 + d, r.Y + r.Height/2 + d)
                });
                g.DrawLines(p, new[]
                {
                    new Point(r.X + r.Width/2 + d, r.Y + r.Height/2 - d),
                    new Point(r.X + r.Width/2 - d, r.Y + r.Height/2 + d)
                });
            }
        }

        private Rectangle _hot = Rectangle.Empty;
        private Rectangle _hotFoot = Rectangle.Empty;
        private int _swatchesShown;
        private int _moreLabelX;

        // ==================================================================
        // mouse
        // ==================================================================

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle before = _hot;
            Rectangle beforeFoot = _hotFoot;
            _hot = HitHeader(e.Location);
            _hotFoot = HitFooter(e.Location);
            if (before != _hot || beforeFoot != _hotFoot) Invalidate();

            if (_dragging)
            {
                Location = new Point(
                    _dragBounds.X + (e.X - _dragOrigin.X),
                    _dragBounds.Y + (e.Y - _dragOrigin.Y));
            }
            else if (_resizeDir != 0)
            {
                ApplyResize(e.Location);
            }
            else
            {
                int rd = HitResize(e.Location);
                if (rd != 0)
                {
                    // a grip always wins over the header's drag cursor
                    Cursor = ResizeCursor(rd);
                    return;
                }

                // Cursors.Hand is the shared, framework-owned cursor. Building a
                // Cursor from a raw LoadCursor handle throws, because that handle
                // is a shared system resource the Cursor class is not allowed to
                // own and destroy - and it leaked a handle per mouse-move besides.
                bool interactive = _hot != Rectangle.Empty || _hotFoot != Rectangle.Empty;
                Cursor = interactive ? Cursors.Hand : Cursors.Default;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot != Rectangle.Empty || _hotFoot != Rectangle.Empty)
            {
                _hot = Rectangle.Empty;
                _hotFoot = Rectangle.Empty;
                Invalidate();
            }
            _dragging = false;
            _resizeDir = 0;
        }

        /// <summary>
        /// Grows or shrinks the note from the grip the drag started on. The
        /// limits are the same ones the constructor clamps to, so a dragged note
        /// and a reopened note can never disagree about how big they may get.
        /// </summary>
        private void ApplyResize(Point at)
        {
            int dx = at.X - _resizeOrigin.X;
            int dy = at.Y - _resizeOrigin.Y;
            int l = _resizeStart.Left, t = _resizeStart.Top;
            int w = _resizeStart.Width, h = _resizeStart.Height;

            if ((_resizeDir & RD_L) != 0) { l += dx; w -= dx; }
            if ((_resizeDir & RD_R) != 0) { w += dx; }
            if ((_resizeDir & RD_T) != 0) { t += dy; h -= dy; }
            if ((_resizeDir & RD_B) != 0) { h += dy; }

            int minW = S(M_MINW), minH = S(M_MINH), maxW = S(M_MAXW), maxH = S(M_MAXH);
            if (w < minW) { if ((_resizeDir & RD_L) != 0) l = _resizeStart.Right - minW; w = minW; }
            if (w > maxW) { if ((_resizeDir & RD_L) != 0) l = _resizeStart.Right - maxW; w = maxW; }
            if (h < minH) { if ((_resizeDir & RD_T) != 0) t = _resizeStart.Bottom - minH; h = minH; }
            if (h > maxH) { if ((_resizeDir & RD_T) != 0) t = _resizeStart.Bottom - maxH; h = maxH; }

            Bounds = new Rectangle(l, t, w, h);
        }


        private Rectangle HitHeader(Point p)
        {
            if (_rClose.Contains(p)) return _rClose;
            if (_rPin.Contains(p)) return _rPin;
            if (_rShade.Contains(p)) return _rShade;
            return Rectangle.Empty;
        }

        /// <summary>
        /// Which resize grip is under the pointer, if any. Corners are tested
        /// first and get a square target, so the diagonal grabs are as easy to
        /// hit as the plain sides. Everything here lives inside the M_PAD outer
        /// margin, so a grip can never sit on top of the title or the editor.
        ///
        /// M_CORNER has to clear the S(14) corner radius that ApplyRegion cuts
        /// the window to. The pixels in the very corner are outside the region
        /// and the click goes straight through them, so a grip that stopped at
        /// the radius would sit on dead space the pointer can never reach.
        /// </summary>
        private int HitResize(Point p)
        {
            int e = S(M_EDGE), c = S(M_CORNER);
            if (p.X <= c && p.Y <= c) return RD_L | RD_T;
            if (p.X >= Width - c && p.Y <= c) return RD_R | RD_T;
            if (p.X <= c && p.Y >= Height - c) return RD_L | RD_B;
            if (p.X >= Width - c && p.Y >= Height - c) return RD_R | RD_B;
            if (p.X <= e) return RD_L;
            if (p.X >= Width - e) return RD_R;
            if (p.Y <= e) return RD_T;
            if (p.Y >= Height - e) return RD_B;
            return 0;
        }

        private static Cursor ResizeCursor(int dir)
        {
            switch (dir)
            {
                case RD_L:
                case RD_R: return Cursors.SizeWE;
                case RD_T:
                case RD_B: return Cursors.SizeNS;
                case RD_L | RD_T:
                case RD_R | RD_B: return Cursors.SizeNWSE;
                default: return Cursors.SizeNESW;
            }
        }


        /// <summary>Which footer control is under the pointer, if any.</summary>
        private Rectangle HitFooter(Point p)
        {
            if (_findOpen)
            {
                if (_rFindClose.Contains(p)) return _rFindClose;
                if (_rFindNext.Contains(p)) return _rFindNext;
                if (_rFindPrev.Contains(p)) return _rFindPrev;
                return Rectangle.Empty;
            }
            for (int i = 0; i < 3; i++) if (_rFmt[i].Contains(p)) return _rFmt[i];
            for (int i = 0; i < _rAccent.Length; i++)
                if (!_rAccent[i].IsEmpty && _rAccent[i].Contains(p)) return _rAccent[i];
            if (_rArchive.Contains(p)) return _rArchive;
            if (_rDelete.Contains(p)) return _rDelete;
            return Rectangle.Empty;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();                  // clicking the chrome should not strand focus
            if (e.Button != MouseButtons.Left) return;

            // a grip on the border outranks everything else in the shell
            int rd = HitResize(e.Location);
            if (rd != 0)
            {
                _resizeDir = rd;
                _resizeOrigin = e.Location;
                _resizeStart = Bounds;
                Capture = true;
                return;
            }


            // header buttons
            Rectangle h = HitHeader(e.Location);
            if (h == _rClose) { CloseAndSave(); return; }
            if (h == _rPin) { TogglePin(); return; }
            if (h == _rShade) { Shade(); return; }

            // footer hit test
            if (_findOpen)
            {
                if (_rFindClose.Contains(e.Location)) { CloseFind(); return; }
                if (_rFindNext.Contains(e.Location)) { FindNext(1); return; }
                if (_rFindPrev.Contains(e.Location)) { FindNext(-1); return; }
            }
            else
            {
                for (int i = 0; i < 3; i++)
                    if (_rFmt[i].Contains(e.Location)) { ToggleFormat(i); return; }
                for (int i = 0; i < _rAccent.Length; i++)
                    if (_rAccent[i].Contains(e.Location)) { SetAccent(i); return; }
                if (_rSave.Contains(e.Location)) { SaveNow(true); return; }
                if (_rArchive.Contains(e.Location)) { ToggleArchive(); return; }
                if (_rDelete.Contains(e.Location)) { Delete(); return; }
            }

            // anything else in the header drags the window
            Rectangle c = ContentRect;
            if (e.Y < c.Top + HeadH && !_title.Bounds.Contains(e.Location))
            {
                _dragging = true;
                _dragOrigin = e.Location;
                _dragBounds = Bounds;
                Capture = true;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging) { _dragging = false; Capture = false; RememberGeometry(); _app.RequestDeckRefresh(); }
            if (_resizeDir != 0)
            {
                _resizeDir = 0;
                Capture = false;
                RememberGeometry();
                _app.RequestDeckRefresh();
                Cursor = Cursors.Default;
            }
        }

        // ==================================================================
        // keyboard
        // ==================================================================

        /// <summary>
        /// Every shortcut is a Ctrl or Ctrl+Alt chord, and anything else is
        /// handed straight back to the editor. This override is the reason a
        /// stray shortcut can never cost you a keystroke.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            bool shift = (keyData & Keys.Shift) == Keys.Shift;

            if (ctrl)
            {
                switch (keyData & ~(Keys.Control | Keys.Shift))
                {
                    case Keys.B: ToggleFormat(0); return true;
                    case Keys.I: ToggleFormat(1); return true;
                    case Keys.U: ToggleFormat(2); return true;
                    case Keys.F: ToggleFind(); return true;
                    case Keys.S: SaveNow(true); return true;
                    case Keys.N: CloseFind(); _title.Focus(); _title.SelectAll(); return true;
                    case Keys.Tab:
                        _app.FocusAdjacentNote(this, shift ? -1 : 1);
                        return true;
                    case Keys.D1: SetAccent(0); return true;
                    case Keys.D2: SetAccent(1); return true;
                    case Keys.D3: SetAccent(2); return true;
                    case Keys.D4: SetAccent(3); return true;
                    case Keys.D5: SetAccent(4); return true;
                    case Keys.D6: SetAccent(5); return true;
                }
            }

            if (keyData == Keys.Escape)
            {
                if (_findOpen) { CloseFind(); return true; }
                _app.CollapseNote(this);
                return true;
            }

            // Tab indents the body. Handled explicitly rather than relying on
            // AcceptsTab, which RichTextBox does not honour consistently, and
            // it keeps Tab from ever landing focus in the title field.
            if (keyData == Keys.Tab && _editor.Focused)
            {
                int at = _editor.SelectionStart;
                int lineStart = _editor.GetFirstCharIndexFromLine(_editor.GetLineFromCharIndex(at));
                string indent = "    ";
                int n = 0;
                while (lineStart + n < _editor.TextLength)
                {
                    char ch = _editor.Text[lineStart + n];
                    if (ch == ' ') n++;
                    else if (ch == '\t') n += 4;
                    else break;
                }
                _editor.Select(lineStart + n, 0);
                _editor.SelectedText = indent;
                _editor.SelectionLength = 0;
                return true;
            }

            if (keyData == Keys.Enter && ctrl)
            {
                _app.CollapseOrToggle(this);
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnTitleKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _editor.Focus();
                _editor.SelectionStart = _editor.TextLength;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _editor.Focus();
                _editor.SelectionStart = _editor.TextLength;
                _editor.SelectionLength = 0;
            }
        }

        // ==================================================================
        // commands
        // ==================================================================

        private void ToggleFormat(int which)
        {
            if (_editor.SelectionLength == 0)
            {
                // no selection: flip the flag that applies to the next character
                _fmtState ^= (1 << which);
            }
            else
            {
                FontStyle want = which == 0 ? FontStyle.Bold
                              : which == 1 ? FontStyle.Italic
                                           : FontStyle.Underline;
                Font cur = _editor.SelectionFont ?? _editor.Font;
                bool has = (cur.Style & want) == want;
                FontStyle ns = has ? cur.Style & ~want : cur.Style | want;
                _editor.SelectionFont = new Font(cur, ns);
            }
            Invalidate();
        }

        private void SetAccent(int i)
        {
            if (i < 0 || i >= Theme.Accents.Length) return;
            _note.Accent = i;
            Touch();
            SaveNow(true);
            ApplyRegion();
            Invalidate();
            _editor.CaretColor = _note.AccentColor;
            _app.RequestDeckRefresh();
            SetStatus("ACCENT " + Theme.AccentNames[i], false);
        }

        private void CycleAccent(int dir)
        {
            SetAccent((_note.Accent + dir + Theme.Accents.Length) % Theme.Accents.Length);
        }

        private void TogglePin()
        {
            _pinned = !_pinned;
            _note.Pinned = _pinned;
            Touch();
            SaveNow(true);
            Invalidate();
            SetStatus(_pinned ? "PINNED" : "UNPINNED", false);
        }

        private void ToggleArchive()
        {
            _note.Archived = !_note.Archived;
            Touch();
            SaveNow(true);
            Invalidate();
            _app.RequestDeckRefresh();
            SetStatus(_note.Archived ? "ARCHIVED" : "RESTORED", false);
        }

        private void Delete()
        {
            if (_app.Settings.ConfirmDelete)
            {
                Dialogs.ConfirmDelete(this, _note.DisplayTitle, () => DoDelete());
            }
            else DoDelete();
        }

        private void DoDelete()
        {
            SaveNow(true);
            Close();
        }

        private void Shade()
        {
            // collapse the note down to its header strip, keeping it alive
            if (Height <= HeadH + S(M_PAD) * 2 + S(30)) { Restore(); return; }
            _shadedFrom = Height;
            Height = HeadH + S(M_PAD) * 2 + S(6);
            ApplyRegion();
            LayoutChildren();
            Invalidate();
        }

        private int _shadedFrom;

        public void Restore()
        {
            if (_shadedFrom <= 0) return;
            Height = _shadedFrom;
            _shadedFrom = 0;
            ApplyRegion();
            LayoutChildren();
            Invalidate();
            _editor.Focus();
        }

        public void ShadeTo(int h)
        {
            Height = h;
            ApplyRegion();
            LayoutChildren();
            Invalidate();
        }

        public int ShadedFrom { get { return _shadedFrom; } }

        private ContextMenuStrip BuildEditorMenu()
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
            AddItem(m, "CUT", () => _editor.Cut());
            AddItem(m, "COPY", () => _editor.Copy());
            AddItem(m, "PASTE", () => _editor.Paste());
            AddItem(m, "SELECT ALL", () => { _editor.Focus(); _editor.SelectAll(); });
            m.Items.Add(new ToolStripSeparator());
            AddItem(m, "BOLD", () => ToggleFormat(0));
            AddItem(m, "ITALIC", () => ToggleFormat(1));
            AddItem(m, "UNDERLINE", () => ToggleFormat(2));
            m.Items.Add(new ToolStripSeparator());
            AddItem(m, "FIND", () => ToggleFind());
            AddItem(m, "NEXT ACCENT", () => CycleAccent(1));
            m.Items.Add(new ToolStripSeparator());
            AddItem(m, "ARCHIVE", () => ToggleArchive());
            AddItem(m, "DELETE", () => Delete());
            return m;
        }

        private static void AddItem(ContextMenuStrip m, string text, Action a)
        {
            var it = new ToolStripMenuItem(text.ToUpperInvariant());
            it.ForeColor = Theme.Text;
            it.BackColor = Theme.SurfaceHi;
            it.Click += (s, e) => a();
            m.Items.Add(it);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            // A sticky note should always be ready to be typed into the moment
            // it is in front. Windows will happily drop the caret on the title
            // field or on nothing at all after an activation, a resize or a
            // shade/restore, and then keystrokes go somewhere useless. Only the
            // title and the find box are allowed to hold focus instead.
            if (_title.Focused) return;
            if (_findBoxHost != null && _findBoxHost.Focused) return;
            if (_editor.IsHandleCreated && !_editor.Focused) _editor.Focus();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // Optional: tuck the note down to its header when attention moves
            // elsewhere. Off by default, because it is surprising if you are
            // editing one note and glance at another window.
            if (_app.Settings.CollapseOnFocusLoss && !_pinned && !_findOpen
                && !_loading && Height > HeadH + S(M_PAD) * 2 + S(30))
            {
                Shade();
            }
        }

        // ==================================================================
        // find
        // ==================================================================

        private int _findHits;

        private void ToggleFind()
        {
            if (_findOpen) { CloseFind(); return; }
            _findOpen = true;
            _findHits = 0;
            LayoutChildren();
            Invalidate();
            FocusFind();
        }

        private void FocusFind()
        {
            // the find field is drawn by the footer; typing goes to a real box
            // placed in the footer band (never over the editor)
            if (_findBoxHost == null || _findBoxHost.IsDisposed)
            {
                _findBoxHost = new TextBox
                {
                    BorderStyle = BorderStyle.None,
                    BackColor = Theme.Surface,
                    ForeColor = Theme.Text,
                    Font = Theme.Mono(9.5f, FontStyle.Regular),
                    ReadOnly = false,
                    Enabled = true,
                    TabStop = true,
                    HideSelection = false,
                };
                _findBoxHost.TextChanged += (s, e) => { _findHits = 0; _findFrom = 0; };
                Controls.Add(_findBoxHost);
            }
            _findBoxHost.SetBounds(_rFindBox.X + S(20), _rFindBox.Y + S(5),
                                   Math.Max(S(20), _rFindBox.Width - S(26)), _findBoxHost.Height);
            _findBoxHost.BringToFront();
            _findBoxHost.Focus();
            Invalidate();
        }

        private TextBox _findBoxHost;

        private void CloseFind()
        {
            if (!_findOpen) return;
            _findOpen = false;
            if (_findBoxHost != null) { _findBoxHost.Visible = false; }
            _editor.Focus();
            LayoutChildren();
            Invalidate();
        }

        private void FindNext(int dir)
        {
            if (!_findOpen) { ToggleFind(); return; }
            string q = _findBoxHost == null ? null : _findBoxHost.Text;
            if (string.IsNullOrEmpty(q))
            {
                _findHits = 0;
                Invalidate();
                return;
            }
            string hay = _editor.Text;
            int at;
            if (dir > 0)
            {
                at = hay.IndexOf(q, Math.Min(_findFrom, Math.Max(0, hay.Length)), StringComparison.OrdinalIgnoreCase);
                if (at < 0) at = hay.IndexOf(q, 0, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                int lim = Math.Max(0, _findFrom - 1);
                at = hay.LastIndexOf(q, Math.Min(lim, hay.Length - 1), StringComparison.OrdinalIgnoreCase);
                if (at < 0) at = hay.LastIndexOf(q, hay.Length - 1, StringComparison.OrdinalIgnoreCase);
            }
            if (at < 0)
            {
                _findHits = 0;
                Invalidate();
                SetStatus("NO MATCH", true);
                return;
            }
            _findFrom = at + 1;
            _editor.Focus();
            _editor.Select(at, q.Length);
            _editor.ScrollToCaret();
            _findHits = CountHits(hay, q);
            Invalidate();
            SetStatus("FOUND", false);
        }

        private static int CountHits(string hay, string q)
        {
            int c = 0, i = 0;
            while (i <= hay.Length - q.Length)
            {
                int at = hay.IndexOf(q, i, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                c++; i = at + 1;
            }
            return c;
        }

        /// <summary>Number keys drive find while the find bar is open.</summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
        }

        // ==================================================================
        // persistence
        // ==================================================================

        private Timer _saveTimer;

        private void Touch()
        {
            _note.Edited = DateTime.UtcNow.Ticks;
            _note.Dirty = true;
            if (_saveTimer == null)
            {
                _saveTimer = new Timer { Interval = 250 };   // 250ms, as the Mac app
                _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); SaveNow(false); };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void SaveNow(bool announce)
        {
            if (_saveTimer != null) _saveTimer.Stop();
            if (_loading) return;
            if (!_note.Dirty && !announce) return;

            _note.Rtf = _editor.Rtf;
            _note.Title = _title.Text;
            _note.Edited = DateTime.UtcNow.Ticks;
            RememberGeometry();
            try { _store.Save(_note); }
            catch (Exception ex) { Log.Write("save note: " + ex.Message); SetStatus("SAVE FAILED", true); return; }

            SetStatus(announce ? "SAVED" : "SAVED", false);
            _app.RequestDeckRefresh();
            NoteChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SetStatus(string s, bool alert)
        {
            _status = s;
            _statusAt = DateTime.Now;
            _statusAlert = alert;
            Invalidate();
        }

        private void CloseAndSave()
        {
            SaveNow(true);
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            SaveNow(true);
            if (_saveTimer != null) { _saveTimer.Stop(); _saveTimer.Dispose(); _saveTimer = null; }
            if (_editor.ContextMenuStrip != null) _editor.ContextMenuStrip.Dispose();
            NoteClosed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Rebuilds the body font when the user changes the size.</summary>
        public void ReapplyFont()
        {
            _editor.Font = BodyFont();
            _editor.ApplyBodyStyle(BodyFont(), Theme.Text);
            _editor.CaretColor = _note.AccentColor;
            LayoutChildren();
            Invalidate();
        }

        /// <summary>
        /// Put real keyboard focus in the body. Used whenever the note is
        /// brought forward, and by the self-test, so "the window is open" and
        /// "you can type in it" can never drift apart.
        /// </summary>
        public void FocusEditor()
        {
            if (!Visible) Show();
            if (_shadedFrom > 0) Restore();
            Native.ForceForeground(this);
            _editor.Focus();
            if (_editor.IsHandleCreated) Native.SetFocus(_editor.Handle);
            _editor.SelectionStart = Math.Min(_editor.SelectionStart, _editor.TextLength);
            _editor.SelectionLength = 0;
            _editor.ResetCaretBlink();
            SetStatus("READY", false);
        }

        /// <summary>Exposed for the self-test and the diagnostics panel.</summary>
        public NeonTextBox Editor { get { return _editor; } }
        public TextBox TitleBox { get { return _title; } }
    }
}
