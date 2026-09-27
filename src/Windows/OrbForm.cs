using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DNotes.Windows
{
    using Core;

    /// <summary>
    /// The floating button: a soft-edged neon disc carrying the DANISHYAR mark,
    /// parked anywhere on the desktop.
    ///
    /// Drawn through <see cref="Layered"/> so it has real per-pixel alpha. That
    /// buys two things at once - the halo fades instead of ending in a hard
    /// clipped circle, and because the window is layered, the fully transparent
    /// corners pass clicks straight through to whatever is underneath, so the
    /// orb only ever intercepts a click on its 72 pixels.
    ///
    /// It is draggable, snaps to the nearest screen edge when you let go, and
    /// carries WS_EX_NOACTIVATE so it never steals focus from what you are
    /// typing in.
    /// </summary>
    internal sealed class OrbForm : Form
    {
        private const int CORE = 72;         // the emblem disc, and the whole window

        private readonly App _app;
        private readonly Store _store;
        private Bitmap _face;
        private int _core, _win, _gutter;
        private bool _hover, _pressed, _dragging;
        private Point _dragOffset;
        private Point _pressStart;

        public event EventHandler OrbClicked;
        public event EventHandler OrbRightClicked;

        /// <summary>True once the pointer is over the disc.</summary>
        public bool Hover { get { return _hover; } }
        public int Core { get { return _core; } }
        public Rectangle FaceRect
        {
            get
            {
                return new Rectangle(Left + _gutter, Top + _gutter, _core, _core);
            }
        }

        private int S(int v) { return (int)Math.Round(v * Ui.Scale); }

        public OrbForm(App app, Store store)
        {
            _app = app;
            _store = store;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            Text = "DNotes Button";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _core = S(CORE);
            // No gutter: the window is exactly the emblem, so the only thing on
            // screen is your logo and the transparent corners around it.
            _gutter = 0;
            _win = _core;
            Size = new Size(_win, _win);

            Bitmap f = Assets.Orb;
            if (f != null) _face = Resize(f, _core);
        }

        private static Bitmap Resize(Bitmap src, int size)
        {
            if (src == null) return null;
            if (src.Width == size) return new Bitmap(src);
            var b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, size, size));
            }
            return b;
        }
        /// <summary>Never take focus off whatever the user is typing in.</summary>
        protected override bool ShowWithoutActivation { get { return true; } }

        /// <summary>
        /// Ask for the style at handle creation rather than patching it on
        /// afterwards. WS_EX_NOACTIVATE only reliably prevents activation if it
        /// is present when the window is born; setting it later left a first
        /// Show() free to steal focus from whatever the user was typing in.
        /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW
                            | Native.WS_EX_NOACTIVATE;
                cp.ExStyle &= ~Native.WS_EX_TRANSPARENT;   // the disc must stay clickable
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
        }

        public void PlaceInitial()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int x = _app.Settings.OrbX, y = _app.Settings.OrbY;
            if (x < -9999 || y < -9999)
            {
                // first run: bottom right, tucked in from the corner
                x = wa.Right - _core - S(56);
                y = wa.Bottom - _core - S(56);
            }
            Location = Clamp(new Point(x, y));
        }

        private Point Clamp(Point p)
        {
            Screen s = Screen.FromPoint(new Point(p.X, p.Y));
            Rectangle wa = s.WorkingArea;
            int x = p.X, y = p.Y;
            if (x < wa.Left - _gutter) x = wa.Left;
            if (y < wa.Top - _gutter) y = wa.Top;
            if (x + _core + _gutter > wa.Right) x = wa.Right - _core - _gutter;
            if (y + _core + _gutter > wa.Bottom) y = wa.Bottom - _core - _gutter;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;
            return new Point(x, y);
        }

        // ==================================================================
        // painting: compose into a bitmap, then push it as a layered window
        // ==================================================================

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e) { /* all done in Push() */ }

        /// <summary>
        /// The whole button is the emblem. No halo, no outer ring, no gutter -
        /// the window is exactly the size of the disc, so it covers the least
        /// desktop possible and the artwork is what you see.
        /// </summary>
        private void Render()
        {
            using (var bmp = new Bitmap(_win, _win, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    if (_face != null)
                    {
                        // press sinks it by a pixel, hover lifts it a hair:
                        // feedback without adding a single drawn element
                        int lift = _pressed ? 1 : (_hover ? -1 : 0);
                        int grow = _pressed ? -2 : (_hover ? 2 : 0);
                        var r = new Rectangle(grow, grow + lift, _core + grow * 2, _core + grow * 2);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(_face, r);
                    }
                }
                Layered.Push(this, bmp);
            }
        }

        public void Refresh() { if (IsHandleCreated) Render(); }

        // ==================================================================
        // input
        // ==================================================================

        private bool OverFace(Point p)
        {
            int dx = p.X - (_gutter + _core / 2);
            int dy = p.Y - (_gutter + _core / 2);
            int r = _core / 2;
            return dx * dx + dy * dy <= r * r;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                Location = new Point(_dragOffset.X + e.X, _dragOffset.Y + e.Y);
                return;
            }
            bool h = OverFace(e.Location);
            if (h != _hover)
            {
                _hover = h;
                Cursor = h ? Cursors.Hand : Cursors.Default;
                Render();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover || _pressed) { _hover = _pressed = false; Cursor = Cursors.Default; Render(); }
            _dragging = false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                if (OrbRightClicked != null) OrbRightClicked(this, EventArgs.Empty);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (!OverFace(e.Location)) return;

            // a press scales the disc in slightly - a real press, not just a repaint
            _pressed = true;
            _dragging = true;
            _dragOffset = new Point(Left - e.X, Top - e.Y);
            _pressStart = new Point(Left, Top);
            Capture = true;
            Render();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            bool wasDragging = _dragging;
            _dragging = false;
            _pressed = false;
            if (Capture) Capture = false;
            Render();

            if (!wasDragging) return;

            // a press that never actually moved the orb is a click, not a drag
            bool moved = Math.Abs(Left - _pressStart.X) > 2 || Math.Abs(Top - _pressStart.Y) > 2;
            if (moved) { SnapAndRemember(); return; }
            if (OrbClicked != null) OrbClicked(this, EventArgs.Empty);
        }

        /// <summary>Nudges the orb onto the nearest edge, if snapping is on.</summary>
        public void SnapAndRemember()
        {
            Rectangle wa = Screen.FromPoint(new Point(Left + _win / 2, Top + _win / 2)).WorkingArea;
            if (_app.Settings.OrbSnap)
            {
                int tol = S(26);
                int left = Left + _gutter, right = Left + _gutter + _core;
                int top = Top + _gutter, bot = Top + _gutter + _core;
                int x = Left, y = Top;
                int dl = Math.Abs(left - wa.Left), dr = Math.Abs(right - wa.Right);
                int dt = Math.Abs(top - wa.Top), db = Math.Abs(bot - wa.Bottom);
                int best = dl; int edge = 0;                      // 0 left 1 right 2 top 3 bottom
                if (dr < best) { best = dr; edge = 1; }
                if (dt < best) { best = dt; edge = 2; }
                if (db < best) { best = db; edge = 3; }
                if (best <= tol)
                {
                    if (edge == 0) x = wa.Left - _gutter;
                    else if (edge == 1) x = wa.Right - _core - _gutter;
                    else if (edge == 2) y = wa.Top - _gutter;
                    else y = wa.Bottom - _core - _gutter;
                }
                Location = Clamp(new Point(x, y));
            }
            else
            {
                Location = Clamp(Location);
            }
            Remember();
        }

        public void Remember()
        {
            _app.Settings.OrbX = Left;
            _app.Settings.OrbY = Top;
        }

        public void ResetPosition()
        {
            _app.Settings.OrbX = -99999;
            _app.Settings.OrbY = -99999;
            PlaceInitial();
            Remember();
        }

        // ==================================================================
        // hooks for the self-test
        // ==================================================================

        /// <summary>Centre of the disc in client coordinates.</summary>
        public Point FaceCentre
        {
            get { return new Point(_gutter + _core / 2, _gutter + _core / 2); }
        }

        /// <summary>Centre of the disc in screen coordinates.</summary>
        public Point FaceCentreScreen
        {
            get { return new Point(Left + _gutter + _core / 2, Top + _gutter + _core / 2); }
        }

        /// <summary>Right edge of the disc in screen coordinates.</summary>
        public int FaceRight
        {
            get { return Left + _gutter + _core; }
        }

        /// <summary>A press and release in the middle of the disc: a click.</summary>
        public void SimulateClick()
        {
            Point c = FaceCentre;
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, c.X, c.Y, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, c.X, c.Y, 0));
        }

        /// <summary>Press at one point, move, release - i.e. a drag.</summary>
        public void SimulateDrag(int fromX, int fromY, int toX, int toY)
        {
            Point c = FaceCentre;
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, c.X, c.Y, 0));
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, toX - fromX, toY - fromY, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, toX - fromX, toY - fromY, 0));
        }

        /// <summary>Places the disc with its top-left at the given point.</summary>
        public void NudgeTo(int faceX, int faceY)
        {
            Location = new Point(faceX - _gutter, faceY - _gutter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_face != null) { _face.Dispose(); _face = null; }
            }
            base.Dispose(disposing);
        }
    }
}
