using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DNotes.Core
{
    /// <summary>
    /// The note editor.
    ///
    /// Two jobs beyond being a RichTextBox:
    ///
    /// 1. It draws its own caret. A stock RichTextBox paints the caret with
    ///    the system colour, which is near-black - completely invisible on a
    ///    dark note. A user cannot tell a dark-on-dark caret from a frozen
    ///    editor, so we hide the native caret and draw a blinking neon block
    ///    in the subclassed paint instead. The caret being plainly visible is
    ///    the clearest possible signal that this surface accepts typing.
    ///
    /// 2. It keeps the input path completely unobstructed. ReadOnly is forced
    ///    off, every ancestor is kept enabled, the control is Selectable and
    ///    TabStop, and nothing is ever layered on top of it. Whatever else the
    ///    app does, this control stays typeable.
    /// </summary>
    internal class NeonTextBox : RichTextBox
    {
        private const int WM_PAINT = 0x000F;

        private readonly Timer _blink;
        private bool _caretOn = true;
        private bool _caretMoved = true;
        private Rectangle _lastCaret;
        private bool _suppressCaret;
        private Color _caretColor = Theme.Neon;
        private Color _caretHot = Color.FromArgb(0xE6, 0xFF, 0xF2);

        public event EventHandler CaretStateChanged;

        /// <summary>Extra pixels reserved on the left for the accent rail.</summary>
        public int RailWidth { get; set; }

        public Color CaretColor
        {
            get { return _caretColor; }
            set { _caretColor = value; _caretMoved = true; }
        }

        public bool ShowCaretGlyph { get; set; }

        public NeonTextBox()
        {
            // ---- input guarantees -------------------------------------------
            ReadOnly = false;
            Enabled = true;
            TabStop = true;
            HideSelection = false;
            ShortcutsEnabled = true;
            // Tab indents the note instead of wandering off into the title
            // field, where a stray keystroke would edit the wrong thing.
            AcceptsTab = true;
            WordWrap = true;
            DetectUrls = true;
            AllowDrop = true;
            ScrollBars = RichTextBoxScrollBars.Vertical;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Base;
            ForeColor = Theme.Text;
            SetStyle(ControlStyles.Selectable, true);
            SetStyle(ControlStyles.Opaque, false);

            _blink = new Timer { Interval = 530 };
            _blink.Tick += delegate
            {
                if (!Focused || ReadOnly || !Enabled) return;
                _caretOn = !_caretOn;
                InvalidateCaret();
                CaretStateChanged?.Invoke(this, EventArgs.Empty);
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.HideCaret(Handle);      // we draw our own
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _blink.Stop();
            base.OnHandleDestroyed(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            _caretOn = true;
            _caretMoved = true;
            _blink.Start();
            InvalidateCaret();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            _blink.Stop();
            _caretOn = true;
            _caretMoved = true;
            Invalidate();
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            Focus();          // belt and braces: make sure we really hold focus
        }

        /// <summary>Restart the blink cycle, e.g. after the caret jumps.</summary>
        public void ResetCaretBlink()
        {
            _caretOn = true;
            _caretMoved = true;
            _blink.Stop();
            _blink.Start();
            InvalidateCaret();
        }

        /// <summary>Where the caret is, in screen coordinates. Empty if off-screen.</summary>
        public Rectangle CaretScreenRect()
        {
            Rectangle r = CaretRect();
            if (r.IsEmpty || !IsHandleCreated) return Rectangle.Empty;
            Point p = PointToScreen(new Point(0, 0));
            return new Rectangle(p.X + r.X, p.Y + r.Y, r.Width, r.Height);
        }

        /// <summary>
        /// Hold the caret solid, blink timer stopped. Lets the self-test sample
        /// the real screen pixels and confirm the caret is actually lit, without
        /// racing the blink.
        /// </summary>
        public void ForceCaretVisible()
        {
            _blink.Stop();
            _caretOn = true;
            InvalidateCaret();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // A click anywhere in the body means "I want to write here".
            ResetCaretBlink();
        }

        protected override void OnSelectionChanged(EventArgs e)
        {
            base.OnSelectionChanged(e);
            _caretMoved = true;
            CaretStateChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            _caretMoved = true;
        }

        /// <summary>
        /// Paint, then lay the caret on top. Doing it here rather than in the
        /// parent form matters: a parent cannot draw above a child control, and
        /// a sibling overlay would steal clicks from the editor.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT && !_suppressCaret)
            {
                try
                {
                    if (ShowCaretGlyph) DrawCaretGlyph();
                    DrawRail();
                }
                catch { /* never let decoration break the editor */ }
            }
        }

        private void DrawRail()
        {
            if (RailWidth <= 0) return;
            using (Graphics g = CreateGraphics())
            using (var b = new SolidBrush(Theme.Alpha(_caretColor, Focused ? 210 : 90)))
                g.FillRectangle(b, 0, 0, RailWidth, ClientSize.Height);
        }

        private Rectangle CaretRect()
        {
            int idx = SelectionStart;
            Point p;
            try { p = GetPositionFromCharIndex(idx); }
            catch { return Rectangle.Empty; }

            int h = Math.Max(12, Font.Height);
            int rail = RailWidth;
            int x = p.X + rail;
            int y = p.Y;

            // if the caret is scrolled out of view, don't paint or invalidate it
            var cr = ClientRectangle;
            if (x < rail - 2 || y < 0 || y > cr.Height) return Rectangle.Empty;

            int w = Math.Max(2, (int)Math.Round(Font.Size * 0.62f));
            return new Rectangle(x, y, w, h);
        }

        private void DrawCaretGlyph()
        {
            if (!Focused || ReadOnly || !Enabled) return;
            if (SelectionLength > 0) return;      // selection shows its own caret end
            if (!_caretOn) return;

            Rectangle r = CaretRect();
            if (r.IsEmpty) return;

            using (Graphics g = CreateGraphics())
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                using (var glow = new SolidBrush(Theme.Alpha(_caretColor, 70)))
                    g.FillRectangle(glow, r.X - 3, r.Y + 1, r.Width + 6, r.Height - 2);
                using (var b = new SolidBrush(_caretHot))
                    g.FillRectangle(b, r);
                using (var b2 = new SolidBrush(_caretColor))
                    g.FillRectangle(b2, r.X, r.Y + r.Height - 3, r.Width, 3);
            }
            _lastCaret = r;
        }

        private void InvalidateCaret()
        {
            Rectangle r = CaretRect();
            if (r.IsEmpty)
            {
                if (!_lastCaret.IsEmpty) Invalidate(_lastCaret);
                _lastCaret = Rectangle.Empty;
                return;
            }
            int pad = 6;
            var zone = new Rectangle(r.X - pad, r.Y - 1, r.Width + pad * 2, r.Height + 2);
            if (zone.IntersectsWith(ClientRectangle))
                Invalidate(zone, false);
            if (!_lastCaret.IsEmpty && _lastCaret != r) Invalidate(_lastCaret, false);
            _lastCaret = r;
            _caretMoved = false;
        }

        /// <summary>
        /// Restyle the whole document to the app body type without touching the
        /// text. Called on load and when the font size changes.
        /// </summary>
        public void ApplyBodyStyle(Font f, Color ink)
        {
            if (ReadOnly) return;
            int len = TextLength;
            int selStart = SelectionStart, selLen = SelectionLength;
            Select(0, len);
            SelectionFont = f;
            SelectionColor = ink;
            Select(selStart, selLen);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _blink != null)
            {
                _blink.Stop();
                _blink.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
