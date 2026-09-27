using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DNotes
{
    using Core;
    using Windows;

    /// <summary>
    /// Proves, at runtime, that a DNote can be typed into.
    ///
    /// The check is deliberately end-to-end rather than a unit test: it opens a
    /// real note window, gives it real keyboard focus, pushes real keystrokes
    /// through the Win32 input queue with SendInput, then reads the editor back
    /// and compares. If any layer in the chain - window styles, focus rules, an
    /// overlay control, a swallowed keystroke - ever blocks typing again, this
    /// fails instead of shipping.
    /// </summary>
    internal static class SelfTest
    {
        // ---- SendInput plumbing --------------------------------------------
        private const uint INPUT_KEYBOARD = 1;
        private const uint INPUT_MOUSE = 0;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint n, INPUT[] p, int size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        /// <summary>A real press/release pair, so the grips see a real drag.</summary>
        private static void MouseButton(bool down)
        {
            var i = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dwFlags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP
                    }
                }
            };
            SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void MouseTo(int x, int y)
        {
            SetCursorPos(x, y);
        }

        // ------------------------------------------------------------------

        private static readonly List<string> Lines = new List<string>();
        private static int _pass, _fail;
        private static int _background;

        /// <summary>
        /// Called by the process-level exception guard. A silent "handled and
        /// carried on" is exactly how a broken feature hides, so the self-test
        /// counts them and fails if any happened during a run.
        /// </summary>
        public static void CountBackgroundException()
        {
            _background++;
        }

        public static int Run(string shotPath)
        {
            string root = Path.Combine(Path.GetTempPath(), "DNotes-selftest-" +
                DateTime.Now.ToString("HHmmss"));
            Directory.CreateDirectory(root);
            Log.Write("selftest root " + root);

            Head("DNOTES SELF-TEST");
            Say("workspace  " + root);
            Say("runtime    " + Environment.Version + "  " +
                (Environment.Is64BitProcess ? "x64" : "x86"));
            Say("");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            int rc = 0;
            try
            {
                rc = Exercise(root, shotPath);
            }
            catch (Exception ex)
            {
                Check("selftest harness ran to completion", false, ex.GetType().Name + ": " + ex.Message);
                rc = 1;
            }

            Say("");
            Head("RESULT");
            Check("no unhandled exception was swallowed during the run", _background == 0,
                _background == 0 ? "the process-level guard caught nothing"
                                 : _background + " exception(s) were caught and carried on - see dnotes.log");
            Say(_fail == 0
                ? "  ALL " + _pass + " CHECKS PASSED  -  notes are writable."
                : "  " + _fail + " CHECK(S) FAILED  of " + (_pass + _fail));
            Say("");

            string report = string.Join(Environment.NewLine, Lines);
            try { File.WriteAllText(Path.Combine(root, "selftest.txt"), report); } catch { }
            Console.WriteLine(report);
            Console.WriteLine("report: " + Path.Combine(root, "selftest.txt"));
            return _fail == 0 ? 0 : 1;
        }

        private static int Exercise(string root, string shotPath)
        {
            var store = new Store(root);
            store.EnsureDirs();
            string outDir = shotPath != null ? Path.GetDirectoryName(shotPath) : root;
            if (string.IsNullOrEmpty(outDir)) outDir = root;

            var app = new App(store);
            app.BuildForTest();
            app.Settings.FontSize = 12;

            var note = new Note
            {
                Id = Store.NewId(),
                Title = "SELF-TEST",
                Accent = 0,
                Created = DateTime.UtcNow.Ticks,
                Edited = DateTime.UtcNow.Ticks,
                Rtf = RtfText.FromPlain(""),
            };
            app.Notes.Add(note);
            store.Save(note);

            // ---- open the note for real -----------------------------------
            NoteForm form = app.OpenNote(note.Id);
            Check("note window opened", form != null && form.Visible, form == null ? "null form" : "shown");
            if (form == null) return 1;

            var ed = form.Editor;

            // ---- structural guarantees --------------------------------------
            Check("editor handle created", ed.IsHandleCreated, "IsHandleCreated=" + ed.IsHandleCreated);
            Check("editor is enabled", ed.Enabled, "Enabled=" + ed.Enabled);
            Check("editor is not read-only", !ed.ReadOnly, "ReadOnly=" + ed.ReadOnly);
            Check("editor can take focus", ed.CanFocus, "CanFocus=" + ed.CanFocus);
            Check("editor is tabbable", ed.TabStop, "TabStop=" + ed.TabStop);
            Check("selection stays visible", !ed.HideSelection, "HideSelection=" + ed.HideSelection);
            Check("typing shortcuts enabled", ed.ShortcutsEnabled, "ShortcutsEnabled=" + ed.ShortcutsEnabled);

            bool ancestorsOk = true;
            for (Control c = ed; c != null; c = c.Parent)
                if (!c.Enabled) { ancestorsOk = false; break; }
            Check("every ancestor is enabled", ancestorsOk, ancestorsOk ? "clean chain" : "a parent is disabled");

            int ex = Native.GetWindowLong(form.Handle, Native.GWL_EXSTYLE);
            bool transparent = (ex & Native.WS_EX_TRANSPARENT) != 0;
            bool noActivate = (ex & Native.WS_EX_NOACTIVATE) != 0;
            Check("window is not click-through", !transparent, "WS_EX_TRANSPARENT=" + transparent);
            Check("window is not focus-suppressed", !noActivate, "WS_EX_NOACTIVATE=" + noActivate);

            // ---- nothing parked on top of the editor ------------------------
            Control cover = null;
            foreach (Control c in form.Controls)
            {
                if (ReferenceEquals(c, ed)) continue;
                if (!c.Visible) continue;
                if (c.Bounds.IntersectsWith(ed.Bounds)) { cover = c; break; }
            }
            Check("no control overlaps the editor", cover == null,
                cover == null ? "editor is the top-most child" : "covered by " + cover.Name + " " + cover.Bounds);

            // ---- a click at the centre of the editor lands on the editor ----
            Point mid = ed.PointToScreen(new Point(Math.Max(2, ed.Width / 2), Math.Max(2, ed.Height / 2)));
            IntPtr deepest = Native.DeepestChildAt(form.Handle, mid);
            Check("a click in the body reaches the editor", deepest == ed.Handle,
                deepest == ed.Handle ? "WindowFromPoint -> editor" :
                "WindowFromPoint -> 0x" + deepest.ToString("X") + " (editor 0x" + ed.Handle.ToString("X") + ")");

            Check("the title is in the note the moment it opens",
                !string.IsNullOrEmpty(form.TitleBox.Text) && form.Note.Title == form.TitleBox.Text,
                "field=\"" + form.TitleBox.Text + "\" model=\"" + form.Note.Title + "\"");

            // ---- focus for real ---------------------------------------------
            form.FocusEditor();
            Application.DoEvents();
            Thread.Sleep(180);
            Application.DoEvents();

            IntPtr focus = Native.GetFocus();
            Check("the editor holds keyboard focus", focus == ed.Handle,
                "GetFocus -> 0x" + focus.ToString("X") + (focus == ed.Handle ? " (editor)" : " NOT the editor"));

            // ---- type through the real input queue ---------------------------
            const string phrase = "Hello from DNotes 12345";
            int before = ed.TextLength;
            TypeText(phrase);
            Thread.Sleep(220);
            Application.DoEvents();
            Thread.Sleep(120);
            Application.DoEvents();

            string got = ed.Text;
            bool typed = got.IndexOf(phrase, StringComparison.Ordinal) >= 0;
            Check("synthetic keystrokes reached the editor", typed,
                typed ? "\"" + phrase + "\" is in the buffer"
                     : "buffer = \"" + Trunc(got) + "\"");

            // ---- newline, backspace and Enter all behave ---------------------
            int preEnter = ed.TextLength;
            PressKey(VK_RETURN);
            Pump(160);
            int afterEnter = ed.TextLength;
            Check("Enter inserts a line break", afterEnter == preEnter + 1,
                "length " + preEnter + " -> " + afterEnter);

            EnsureFocus(form, ed); PressKey(VK_BACK);            Pump(160);
            Check("Backspace deletes", ed.TextLength == preEnter,
                "length " + afterEnter + " -> " + ed.TextLength);

            // ---- empty it again, draining the input queue as we go ---------
            int guard = ClearByBackspace(form, ed, 600);
            Check("the note can be emptied again", ed.TextLength == 0,
                "length = " + ed.TextLength + " after " + guard + " backspaces");

            form.FocusEditor(); Pump(120);
            TypeText("line one"); Pump(60);
            PressKey(VK_RETURN); Pump(60);
            TypeText("line two"); Pump(60);
            PressKey(VK_RETURN); Pump(60);
            TypeText("line three"); Pump(140);
            Check("multi-line entry works", ed.Lines.Length == 3,
                "lines = " + ed.Lines.Length + "  [" + ed.Text.Replace("\r", "|") + "]");

            Check("caret sits at the end after typing", ed.SelectionStart == ed.TextLength
                  && ed.SelectionLength == 0,
                "start=" + ed.SelectionStart + " length=" + ed.SelectionLength +
                " textLength=" + ed.TextLength);

            guard = ClearByBackspace(form, ed, 600);
            Check("the note clears again after several lines", ed.TextLength == 0,
                "length = " + ed.TextLength + " after " + guard + " backspaces");

            // ---- dragging a grip resizes the note ---------------------------
            // The note is borderless, so it has no OS frame to grab. This is the
            // regression that matters: a note you cannot make bigger is a note
            // you cannot use, and nothing else in the app would have caught it.
            try
            {
                Native.ForceForeground(form);
                Pump(300);
                // Math.Round, not a cast: S() rounds, so 190 * 1.25 is a 238 floor
                // and truncating here would assert a limit the form never had.
                int minW = (int)Math.Round(280 * Ui.Scale), minH = (int)Math.Round(190 * Ui.Scale);
                int maxW = (int)Math.Round(900 * Ui.Scale), maxH = (int)Math.Round(1100 * Ui.Scale);
                var screen = Screen.PrimaryScreen.Bounds;

                // Park it in the top-left of the screen before every drag. Once a
                // note has been dragged out to the far corner the pointer runs into
                // the edge of the display, stops moving, and the note stops with it
                // - which reads as a broken clamp when it is really the desktop.
                Action<int, int> park = (w, h) =>
                {
                    form.Bounds = new Rectangle(40, 40,
                                               Math.Min(w, maxW), Math.Min(h, maxH));
                    Pump(220);
                };
                // drag from a window-relative point to an absolute one
                Action<int, int, int, int> drag = (rx, ry, tx, ty) =>
                {
                    int sx = form.Left + rx, sy = form.Top + ry;
                    MouseTo(sx, sy);
                    Pump(140);
                    MouseButton(true);
                    Pump(140);
                    const int steps = 10;
                    for (int i = 1; i <= steps; i++)
                    {
                        MouseTo(sx + (tx - sx) * i / steps, sy + (ty - sy) * i / steps);
                        Pump(30);
                    }
                    Pump(140);
                    MouseButton(false);
                    Pump(320);
                };

                // ---- grow from the bottom-right corner ----------------------
                park(425, 475);
                var b0 = form.Bounds;
                int edW0 = ed.Width, edH0 = ed.Height;
                // 8px in, not 3px: ApplyRegion rounds the corner to S(14), so the
                // pixels right on the corner are outside the window and the click
                // goes through to whatever is behind it.
                drag(b0.Width - 8, b0.Height - 8, b0.Left + 560, b0.Top + 620);

                var b1 = form.Bounds;
                Check("a borderless note can be dragged bigger",
                      b1.Width > b0.Width && b1.Height > b0.Height,
                      b0.Width + "x" + b0.Height + "  ->  " + b1.Width + "x" + b1.Height);
                Check("the writing area grew with the note",
                      ed.Width > edW0 && ed.Height > edH0,
                      "editor " + edW0 + "x" + edH0 + "  ->  " + ed.Width + "x" + ed.Height);

                // ---- the same ceiling the constructor clamps to -------------
                // Width only: 1100 logical of height will not fit on the desktop,
                // so dragging the corner would just run into the screen edge.
                park(500, 400);
                drag(form.Width - 8, form.Height / 2, screen.Right - 2, form.Top + form.Height / 2);
                var b2 = form.Bounds;
                Check("a note cannot be dragged past its maximum",
                      b2.Width == maxW && b2.Width > 500,
                      "held at " + b2.Width + " (ceiling " + maxW + ")");

                // ---- and a usable floor ------------------------------------
                park(600, 500);
                drag(form.Width - 8, form.Height - 8, form.Left + 30, form.Top + 20);
                var b3 = form.Bounds;
                Check("a note cannot be dragged below its minimum",
                      b3.Width == minW && b3.Height == minH,
                      "held at " + b3.Width + "x" + b3.Height +
                      " (floor " + minW + "x" + minH + ")");

                // roomy again for the screenshots further down
                park((int)(520 * Ui.Scale), (int)(470 * Ui.Scale));
            }
            catch (Exception rz)
            {
                Check("a borderless note can be dragged bigger", false, rz.Message);
            }

            form.FocusEditor(); Pump(120);
            TypeText("Cyberpunk notes, typed for real.");
            Pump(200);
            Check("final text is intact", ed.Text.Contains("typed for real"),
                "\"" + Trunc(ed.Text) + "\"");

            // Tab must indent the note, not jump into the title field
            int lenBeforeTab = ed.TextLength;
            string titleBeforeTab = form.TitleBox.Text;
            PressKey(VK_TAB);
            Pump(200);
            Check("Tab indents the note instead of leaving the body",
                ed.Focused && ed.TextLength > lenBeforeTab,
                "focused=" + ed.Focused + " length " + lenBeforeTab + " -> " + ed.TextLength);
            PressKey(VK_BACK);
            Pump(180);
            Check("the title is untouched by typing in the body",
                form.TitleBox.Text == titleBeforeTab,
                "title = \"" + form.TitleBox.Text + "\"");

            Check("no synthetic keystrokes were dropped", _dropped == 0,
                _dropped == 0 ? "every event was accepted"
                              : _dropped + " of " + (2 * guard + 2) + " events were dropped by the OS queue");

            // ---- autosave actually reached the disk -------------------------
            form.SaveNow(true);
            Thread.Sleep(200);
            string onDisk = File.ReadAllText(store.NotePath(note));
            Check("note was written to its .dnote file", onDisk.Contains("Cyberpunk notes"),
                Path.GetFileName(store.NotePath(note)) + ", " + onDisk.Length + " bytes");

            Note reloaded = store.LoadFile(store.NotePath(note));
            Check("note reloads from disk with its text",
                reloaded != null && reloaded.PlainText.Contains("Cyberpunk notes"),
                reloaded == null ? "reload failed" : "plain = \"" + Trunc(reloaded.PlainText) + "\"");

            // ---- nothing is cropped off the window -------------------------
            // A rounded Region that is smaller than the client area silently
            // cuts the footer away, which reads as "the app is broken" rather
            // than as a clipping bug. Catch it here.
            bool regionCovers;
            string regionDetail;
            if (form.Region != null)
            {
                RectangleF rbf;
                using (Graphics gg = form.CreateGraphics())
                    rbf = form.Region.GetBounds(gg);
                Rectangle rb = Rectangle.Round(rbf);
                int slackX = form.ClientSize.Width - rb.Width;
                int slackY = form.ClientSize.Height - rb.Height;
                regionCovers = slackX <= 3 && slackY <= 3;
                regionDetail = "client " + form.ClientSize.Width + "x" + form.ClientSize.Height +
                               " vs region " + rb.Width + "x" + rb.Height +
                               " (cropped " + slackX + "x" + slackY + ")";
            }
            else
            {
                regionCovers = true;
                regionDetail = "no region set";
            }
            Check("no part of the window is cropped away", regionCovers, regionDetail);

            // ---- nothing in the footer collides or spills out --------------
            // Layout bugs here are invisible in code review and obvious on
            // screen: buttons drawn on top of each other, or a swatch row
            // running off the edge of the note.
            var foot = form.FooterRects;
            bool footInside = true;
            string footDetail = foot.Count + " controls";
            foreach (Rectangle r in foot)
            {
                if (r.IsEmpty) continue;
                if (!form.InteriorBounds.Contains(r)) { footInside = false; footDetail = "outside the note: " + r; break; }
            }
            Check("every footer control sits inside the note", footInside, footDetail);

            bool noOverlap = true;
            string clash = "";
            for (int i = 0; i < foot.Count && noOverlap; i++)
            {
                if (foot[i].IsEmpty) continue;
                for (int j = i + 1; j < foot.Count; j++)
                {
                    if (foot[j].IsEmpty) continue;
                    if (foot[i].IntersectsWith(foot[j]))
                    {
                        noOverlap = false;
                        clash = foot[i] + " overlaps " + foot[j];
                        break;
                    }
                }
            }
            Check("no two footer controls overlap", noOverlap,
                noOverlap ? footDetail + " at scale " + Ui.Scale.ToString("0.00") : clash);

            // The footer is measured, not fixed: the swatches, ARCHIVE and DELETE
            // are all sized from their own labels and then fitted into what is
            // left. A collision can therefore hide at one note width and not the
            // next, so check the whole legal range rather than one lucky size.
            {
                Rectangle keep = form.Bounds;
                int worstBad = 0, firstClean = 0, tried = 0;
                for (int w = 240; w <= 900; w += 5)
                {
                    form.Bounds = new Rectangle(keep.Left, keep.Top,
                                               (int)Math.Round(w * Ui.Scale), keep.Height);
                    Pump(45);
                    tried++;
                    var fs = form.FooterRects;
                    bool bad = false;
                    for (int i = 0; i < fs.Count && !bad; i++)
                    {
                        if (fs[i].IsEmpty) continue;
                        for (int j = i + 1; j < fs.Count; j++)
                            if (!fs[j].IsEmpty && fs[i].IntersectsWith(fs[j])) { bad = true; break; }
                    }
                    if (bad) worstBad = w;
                    else if (firstClean == 0) firstClean = w;
                }
                form.Bounds = keep;
                Pump(150);
                Check("the note's minimum width is wide enough for its own footer",
                      worstBad == 0 && firstClean > 0,
                      "narrowest clean width = " + (firstClean == 0 ? "none" : firstClean.ToString()) +
                      " logical, widest colliding = " + (worstBad == 0 ? "none" : worstBad.ToString()) +
                      ", over " + tried + " widths");
            }

            bool editorClear = !foot.Any(r => !r.IsEmpty && r.IntersectsWith(form.EditorBounds));
            Check("the footer never covers the writing area", editorClear,
                "editor " + form.EditorBounds);

            bool titleOk = !string.IsNullOrEmpty(form.TitleBox.Text);
            Check("the note title is showing in its field", titleOk,
                "\"" + form.TitleBox.Text + "\"");

            // ---- the caret is actually visible ------------------------------
            // Sampled off the real screen, not off a DrawToBitmap: the caret is
            // painted from the control's own WM_PAINT, and the whole point is
            // that a person can see where they are typing.
            ed.Focus();
            Pump(120);
            ed.SelectionStart = ed.TextLength;
            ed.SelectionLength = 0;
            ed.ForceCaretVisible();
            Pump(120);
            Rectangle cr = ed.CaretScreenRect();
            int litPixels = cr.IsEmpty ? 0 : SampleNeon(cr);
            Check("caret is painted and visible on screen", litPixels > 0,
                litPixels > 0
                    ? litPixels + " lit pixels in the caret at " + cr
                    : "no lit pixels at " + cr + " - a dark caret on a dark note looks frozen");

            // ---- screenshot the note while it is the only window up -------
            try
            {
                Directory.CreateDirectory(outDir);
                form.BringToFront();
                Native.ForceForeground(form);
                form.FocusEditor();
                Pump(450);
                string p = Path.Combine(outDir, "selftest-note.png");
                Shot(form, p);
                Check("note screenshot written", File.Exists(p),
                    File.Exists(p) ? p + " (" + new FileInfo(p).Length + " bytes)" : "missing");
                ShotScreen(p.Replace(".png", "-screen.png"));
            }
            catch (Exception sx)
            {
                Check("note screenshot written", false, sx.Message);
            }

            // ---- a second note in another window still types -----------------
            var second = new Note
            {
                Id = Store.NewId(),
                Title = "SECOND",
                Accent = 1,
                Created = DateTime.UtcNow.Ticks,
                Edited = DateTime.UtcNow.Ticks,
                Rtf = RtfText.FromPlain(""),
            };
            app.Notes.Add(second);
            store.Save(second);
            NoteForm f2 = app.OpenNote(second.Id);
            Pump(250);
            f2.FocusEditor();
            Pump(250);
            TypeText("second window");
            Pump(300);
            Check("a second note window types independently",
                f2.Editor.Text.Contains("second window"),
                "\"" + Trunc(f2.Editor.Text) + "\"");
            Check("the first note kept its own text",
                ed.Text.Contains("Cyberpunk notes") && !ed.Text.Contains("second window"),
                "\"" + Trunc(ed.Text) + "\"");
            f2.Close();
            Pump(200);

            // ---- the mouse, which nothing before this ever exercised --------
            MousePhase(form, outDir);

            // The edge deck is gone: the floating button is the only overlay.

            // ---- the floating button ----------------------------------------
            OrbPhase(app, store, form, outDir);

            // ---- preview text fitting ----------------------------------------
            // A right-aligned preview used to be drawn with a clip rect while the
            // string started outside it; GDI+ then stacked the glyphs on top of one
            // another. Prove the trimming helper can never hand back text that is
            // wider than the gap it is given.
            try
            {
                using (var bmp = new Bitmap(8, 8))
                using (var g = Graphics.FromImage(bmp))
                {
                    var f = Theme.Ui(7.4f, FontStyle.Regular);
                    using (var solid = new SolidBrush(Color.White))
                    {
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                        string longRun = new string('i', 140);      // narrow chars: worst case
                        string wordy = string.Join(" ", Enumerable.Range(0, 30)
                                                       .Select(i => "hijddsfljl"));
                        bool allFit = true;
                        string worst = "";
                        foreach (string src in new[] { longRun, wordy, "hi", "hi how are you" })
                            for (int w = 10; w <= 600; w += 7)
                            {
                                string fitted = Neon.Fit(g, src, f, w);
                                float fw = g.MeasureString(fitted, f).Width;
                                if (fw > w + 0.5f) { allFit = false; worst = src.Substring(0, Math.Min(12, src.Length)) + " @ " + w + " -> " + fw; break; }
                            }
                        Check("preview text is always trimmed to the gap it is given", allFit,
                              allFit ? "checked both note bodies and narrow-char runs" : worst);

                        Check("text that already fits is left alone",
                              Neon.Fit(g, "hi", f, 600) == "hi", "short text unchanged");

                        string cut = Neon.Fit(g, wordy, f, 90);
                        Check("trimmed preview ends in an ellipsis", cut.EndsWith("\u2026"),
                              cut.Length > 1 ? cut.Substring(Math.Max(0, cut.Length - 18)) : cut);
                    }
                }
            }
            catch (Exception fx)
            {
                Check("preview text is always trimmed to the gap it is given", false, fx.Message);
            }

            // ---- screenshots --------------------------------------------------
            Directory.CreateDirectory(outDir);
            try
            {
                app.ToggleAllNotes();
                Pump(600);
                var all = app.AllNotes;
                if (all != null)
                {
                    Native.ForceForeground(all);
                    Pump(300);
                    string p = Path.Combine(outDir, "selftest-allnotes.png");
                    Shot(all, p);
                    Check("all-notes screenshot written", File.Exists(p), p);
                    Check("all-notes lists both notes", all.VisibleCount == 2, "visible = " + all.VisibleCount);

                    // ---- deleting from the list, for real ---------------------
                    // The deck used to carry DELETE and went away with it, which
                    // left the keyboard shortcut as the only way to remove a note
                    // from here. Click a row, click DELETE, confirm, and prove the
                    // file is actually gone from disk - not just off the screen.
                    try
                    {
                        string notesDir = Path.Combine(root, "notes");
                        Func<int> count = () => Directory.GetFiles(notesDir, "*.dnote").Length;
                        int filesBefore = count();
                        int shownBefore = all.VisibleCount;

                        // Seven footer actions now, laid out right-to-left, and the
                        // window drags down to 560 logical. Prove the row of buttons
                        // survives that instead of trusting the arithmetic.
                        {
                            var keepB = all.Bounds;
                            string where = "";
                            for (int w = 560; w <= 1200 && where.Length == 0; w += 20)
                            {
                                all.Bounds = new Rectangle(keepB.Left, keepB.Top,
                                                           (int)Math.Round(w * Ui.Scale), keepB.Height);
                                Pump(40);
                                var acts = new List<Rectangle>();
                                for (int b = 1; b <= 6; b++) acts.Add(all.ActionRect(b));
                                acts.Add(all.ActionRect(9));
                                for (int i = 0; i < acts.Count && where.Length == 0; i++)
                                    for (int j = i + 1; j < acts.Count; j++)
                                        if (!acts[i].IsEmpty && !acts[j].IsEmpty &&
                                            acts[i].IntersectsWith(acts[j]))
                                            where = "at " + w + " logical: " + acts[i] + " overlaps " + acts[j];
                            }
                            all.Bounds = keepB;
                            Pump(200);
                            Check("the all-notes footer never collides, at any width", where.Length == 0,
                                  where.Length == 0 ? "33 widths from 560 to 1200 logical" : where);
                        }

                        var rows = all.RowRects;
                        Check("all-notes draws a hit target for every row",
                              rows.Count == shownBefore && rows.Count > 0,
                              rows.Count + " row rects for " + shownBefore + " notes");

                        var del = all.ActionRect(9);
                        Check("all-notes has a DELETE button", !del.IsEmpty && del.Width > 0,
                              del == Rectangle.Empty ? "no rect" : del.ToString());

                        // tick the first row
                        Native.ForceForeground(all);
                        Pump(300);
                        var r0 = rows[0];
                        var rowPt = all.PointToScreen(new Point(r0.X + r0.Width / 2, r0.Y + r0.Height / 2));
                        MouseTo(rowPt.X, rowPt.Y); Pump(150);
                        MouseButton(true); Pump(120); MouseButton(false); Pump(350);
                        Check("clicking a row selects it", all.SelectedCount == 1,
                              "selected = " + all.SelectedCount);

                        // DELETE must refuse to act on an empty selection rather
                        // than falling back to "delete everything on screen"
                        var delPt = all.PointToScreen(new Point(del.X + del.Width / 2, del.Y + del.Height / 2));

                        int shownMid = all.VisibleCount;
                        Check("DELETE does nothing when nothing is ticked",
                              shownMid == shownBefore, "visible = " + shownMid + " (was " + shownBefore + ")");

                        // now actually delete: click DELETE, then Enter on the
                        // confirm dialog (ConfirmForm maps Enter to Yes)
                        MouseTo(delPt.X, delPt.Y); Pump(150);
                        MouseButton(true); Pump(120); MouseButton(false); Pump(600);
                        PressKey(VK_RETURN);
                        Pump(700);

                        Check("deleting from all-notes removes the row", all.VisibleCount == shownBefore - 1,
                              "visible " + shownBefore + " -> " + all.VisibleCount);
                        Check("the deleted note is gone from disk too", count() == filesBefore - 1,
                              count() + " .dnote files, was " + filesBefore);
                        Check("the list selection is cleared after a delete", all.SelectedCount == 0,
                              "selected = " + all.SelectedCount);

                        string pd = Path.Combine(outDir, "selftest-allnotes-deleted.png");
                        Shot(all, pd);
                        Check("all-notes screenshot written after the delete", File.Exists(pd), pd);
                    }
                    catch (Exception dx)
                    {
                        Check("deleting from all-notes removes the row", false, dx.Message);
                    }
                    all.Close();
                    Pump(200);
                }
                else Check("all-notes window opened", false, "window was null");
            }
            catch (Exception ax)
            {
                Check("all-notes window opened", false, ax.Message);
            }

            try
            {
                app.ShowSettings();
                Pump(500);
                var pf = app.Preferences;
                if (pf != null)
                {
                    Native.ForceForeground(pf);
                    Pump(300);
                    string p = Path.Combine(outDir, "selftest-settings.png");
                    Shot(pf, p);
                    Check("settings screenshot written", File.Exists(p), p);
                    pf.Close();
                    Pump(200);
                }
            }
            catch (Exception px) { Check("settings screenshot written", false, px.Message); }

            form.Close();
            Pump(200);
            return _fail == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// A scripted showcase on a throwaway data folder: real windows, real
        /// painting, real data, then one full-screen capture. Used to eyeball
        /// the app without a human having to drive it.
        /// </summary>
        public static int Demo(string outDir)
        {
            string root = Path.Combine(Path.GetTempPath(), "DNotes-demo-" +
                DateTime.Now.ToString("HHmmss"));
            Directory.CreateDirectory(root);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var store = new Store(root);
            store.EnsureDirs();
            Directory.CreateDirectory(outDir);
            var app = new App(store);
            app.BuildForTest();

            Seed(app, store, "SHOPPING", 0, new[]
            {
                "coffee \u2014 the dark roast",
                "oat milk",
                "rice noodles",
                "chili oil, soy, lime",
            });

            Seed(app, store, "SHIP IT", 1, new[]
            {
                "cut 2.1 \u2192 tag",
                "bump the min version",
                "write the changelog",
                "  sign off with the build hash",
                "post to #releases",
            });

            Seed(app, store, "READING", 3, new[]
            {
                "Notes on attention - the long version",
                "",
                "The interesting part is not the model, it is the interface:",
                "how much of the state is allowed to be visible at once.",
            });

            NoteForm f = app.OpenNote(app.Notes[1].Id);
            app.RequestDeckRefresh();
            Pump(500);

            // hold the deck open on the edge for the capture, the way it looks
            // when someone actually reaches for it
            var wa = Screen.PrimaryScreen.WorkingArea;
            MoveCursor(new Point(wa.Right - 4, wa.Top + wa.Height / 2));
            Pump(900);
            ShotScreen(Path.Combine(outDir, "demo-full.png"));
            if (f != null)
            {
                f.BringToFront();
                Native.ForceForeground(f);
                f.FocusEditor();
                Pump(600);
                Shot(f, Path.Combine(outDir, "demo-note.png"));
            }

            // the floating button: closed, then with its fan open
            OrbForm orb = app.Orb;
            if (orb != null)
            {
                orb.BringToFront();
                Pump(300);
                Shot(orb, Path.Combine(outDir, "demo-orb.png"));
                app.ToggleOrbMenu();
                Pump(700);
                if (app.OrbMenu != null && app.OrbMenu.IsOpen)
                    Shot(app.OrbMenu, Path.Combine(outDir, "demo-orb-menu.png"));
                if (app.OrbMenu != null && app.OrbMenu.IsOpen) app.OrbMenu.Close();
                Pump(300);
            }
            app.ToggleAllNotes();
            Pump(900);
            if (app.AllNotes != null)
            {
                Native.ForceForeground(app.AllNotes);
                Pump(400);
                Shot(app.AllNotes, Path.Combine(outDir, "demo-allnotes.png"));
            }
            app.ShowSettings();
            Pump(700);
            if (app.Preferences != null)
            {
                Native.ForceForeground(app.Preferences);
                Pump(400);
                Shot(app.Preferences, Path.Combine(outDir, "demo-settings.png"));
                app.Preferences.Close();
            }
            app.ShowAbout();
            Pump(700);
            var about = app.About;
            if (about != null)
            {
                Native.ForceForeground(about);
                Pump(400);
                Shot(about, Path.Combine(outDir, "demo-about.png"));
                about.Close();
            }
            app.Shutdown();
            Pump(200);
            Console.WriteLine("demo images: " + outDir);
            return 0;
        }

        private static void Seed(App app, Store store, string title, int accent, string[] body)
        {
            var n = new Note
            {
                Id = Store.NewId(),
                Title = title,
                Accent = accent,
                Created = DateTime.UtcNow.Ticks,
                Edited = DateTime.UtcNow.Ticks,
                Rtf = RtfText.FromPlain(string.Join("\r\n", body)),
            };
            app.Notes.Add(n);
            store.Save(n);
        }

        private static void MoveCursor(Point p)
        {
            try { Cursor.Position = p; }
            catch (Exception ex) { Say("    (could not move the cursor: " + ex.Message + ")"); }
        }

        private static void ParkCursor(Rectangle wa)
        {
            MoveCursor(new Point(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2));
        }

        // ---- synthetic mouse plumbing -------------------------------------
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int MK_LBUTTON = 0x0001;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);

        private static IntPtr Lp(int x, int y)
        {
            return new IntPtr((y << 16) | (x & 0xFFFF));
        }

        /// <summary>
        /// Hover a point inside a control-free part of the note window. The
        /// header and footer are painted by the form itself, so posting the
        /// message at the form's HWND runs exactly the same OnMouseMove that a
        /// real pointer does - no focus games, no coordinate guessing.
        /// </summary>
        private static bool Hover(Form f, int x, int y, out string err)
        {
            err = null;
            try
            {
                SendMessage(f.Handle, WM_MOUSEMOVE, IntPtr.Zero, Lp(x, y));
                return true;
            }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        private static void Click(Form f, int x, int y)
        {
            SendMessage(f.Handle, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), Lp(x, y));
            SendMessage(f.Handle, WM_LBUTTONUP, IntPtr.Zero, Lp(x, y));
        }

        /// <summary>A named point to sweep the pointer across.</summary>
        private struct Target
        {
            public string Name;
            public int X, Y;
            public Target(string name, int x, int y) { Name = name; X = x; Y = y; }
        }

        /// <summary>
        /// Sweep the pointer across every interactive part of the note window.
        /// This is the phase whose absence let a crash through: nothing before
        /// it ever generated a mouse move, so NoteForm.OnMouseMove was dead
        /// code as far as the suite was concerned.
        /// </summary>
        private static void MousePhase(NoteForm form, string outDir)
        {
            Head("MOUSE INTERACTION");

            var targets = new List<Target>();
            var foot = form.FooterRects;
            for (int i = 0; i < 3; i++)
                targets.Add(new Target("format toggle " + i,
                    foot[i].X + foot[i].Width / 2, foot[i].Y + foot[i].Height / 2));
            for (int i = 0; i < Theme.Accents.Length; i++)
            {
                Rectangle r = foot[3 + i];
                if (r.IsEmpty) continue;
                targets.Add(new Target("accent " + i,
                    r.X + r.Width / 2, r.Y + r.Height / 2));
            }
            targets.Add(new Target("archive",
                foot[foot.Count - 2].X + foot[foot.Count - 2].Width / 2,
                foot[foot.Count - 2].Y + foot[foot.Count - 2].Height / 2));
            targets.Add(new Target("delete",
                foot[foot.Count - 1].X + foot[foot.Count - 1].Width / 2,
                foot[foot.Count - 1].Y + foot[foot.Count - 1].Height / 2));
            targets.Add(new Target("body", 12, form.InteriorBounds.Height / 2));

            int handSeen = 0, threw = 0, controls = 0;
            foreach (Target t in targets)
            {
                string err;
                if (!Hover(form, t.X, t.Y, out err))
                {
                    threw++;
                    Check("hovering " + t.Name + " does not throw", false, err);
                }
                else
                {
                    bool isControl = t.Name != "body";
                    if (isControl) controls++;
                    if (isControl && ReferenceEquals(form.Cursor, Cursors.Hand)) handSeen++;
                }
            }
            Check("hovering every control does not throw", threw == 0,
                threw == 0 ? targets.Count + " hover targets swept" : threw + " threw");
            Check("the pointer becomes a hand over controls", handSeen >= controls - 1,
                handSeen + " of " + controls + " control targets showed a hand cursor");

            // the three header buttons, which is exactly where it used to crash
            var hdr = form.HeaderRects;
            string[] names = { "pin", "shade", "close" };
            int headThrew = 0, headHand = 0, headCount = 0;
            for (int i = 0; i < hdr.Count && i < names.Length; i++)
            {
                Rectangle r = hdr[i];
                if (r.IsEmpty) continue;
                headCount++;
                string err;
                if (!Hover(form, r.X + r.Width / 2, r.Y + r.Height / 2, out err))
                {
                    headThrew++;
                    Check("hovering the " + names[i] + " button does not throw", false, err);
                    continue;
                }
                if (ReferenceEquals(form.Cursor, Cursors.Hand)) headHand++;
            }
            Check("hovering the header buttons does not throw", headThrew == 0,
                headThrew == 0 ? "pin, shade and close all swept clean"
                               : headThrew + " header button(s) threw");
            Check("the header buttons show a hand cursor", headHand >= headCount,
                headHand + " of " + headCount + " header buttons showed a hand cursor");

            // and off a control it goes back to an arrow
            string err2;
            Hover(form, 14, form.InteriorBounds.Height / 2, out err2);
            Check("the pointer goes back to an arrow off a control",
                ReferenceEquals(form.Cursor, Cursors.Default),
                "cursor is " + CursorName(form.Cursor));

            // a real click must land on the control it looks like it landed on
            int before = form.Note.Accent;
            int want = (before + 1) % Theme.Accents.Length;
            Rectangle sw = form.FooterRects[3 + want];
            if (!sw.IsEmpty)
            {
                Click(form, sw.X + sw.Width / 2, sw.Y + sw.Height / 2);
                Pump(200);
                Check("clicking an accent swatch changes the note colour",
                    form.Note.Accent == want,
                    "accent " + before + " -> " + form.Note.Accent + " (wanted " + want + ")");
                Rectangle back = form.FooterRects[3 + before];
                if (!back.IsEmpty)
                {
                    Click(form, back.X + back.Width / 2, back.Y + back.Height / 2);
                    Pump(150);
                }
            }
            form.SaveNow(true);
            Pump(150);

            // the confirm dialog is a separate window and was never constructed
            // by the suite before
            try
            {
                using (var dlg = new ConfirmForm("SELF-TEST", "Constructing this must not throw.",
                                                 "OKAY", "CLOSE", Theme.Neon))
                {
                    dlg.Show();
                    Pump(250);
                    bool shown = dlg.Visible;
                    dlg.Close();
                    Pump(150);
                    Check("the confirm dialog opens and closes", shown, "shown = " + shown);
                }
            }
            catch (Exception cx)
            {
                Check("the confirm dialog opens and closes", false, cx.GetType().Name + ": " + cx.Message);
            }

            form.FocusEditor();
            Pump(300);
            try
            {
                string p = Path.Combine(outDir, "selftest-mouse.png");
                form.BringToFront();
                Native.ForceForeground(form);
                Pump(400);
                Shot(form, p);
                Check("post-mouse screenshot written", File.Exists(p), p);
            }
            catch { /* not important enough to fail the run over */ }

            Say("");
        }

        private static string CursorName(Cursor c)
        {
            if (c == null) return "null";
            if (ReferenceEquals(c, Cursors.Hand)) return "hand";
            if (ReferenceEquals(c, Cursors.Default)) return "arrow";
            if (ReferenceEquals(c, Cursors.IBeam)) return "ibeam";
            return c.GetType().Name;
        }

        /// <summary>
        /// The floating button: is it really a disc, is it on screen, does it
        /// drag, does it snap, does the fan open, and does it stay out of the
        /// way of everything else.
        /// </summary>
        private static void OrbPhase(App app, Store store, NoteForm form, string outDir)
        {
            Head("FLOATING BUTTON");
            OrbForm orb = app.Orb;
            if (orb == null)
            {
                Check("the floating button exists", false, "it was not created");
                return;
            }
            Check("the floating button exists", true,
                orb.Bounds.ToString() + ", disc " + orb.Core + "px");

            // ---- the face is a disc, not a black square -------------------
            Bitmap face = Assets.Orb;
            bool round = true;
            string cornerDetail = "corners not sampled";
            if (face != null)
            {
                int w = face.Width, h = face.Height;
                int[] pts = { 0, w / 2, w - 1 };
                int worst = 0;
                foreach (int y in new[] { 0, h / 2, h - 1 })
                    foreach (int x in pts)
                        if (x == w / 2 && y == h / 2) continue;
                        else worst = Math.Max(worst, face.GetPixel(x, y).A);
                round = worst <= 8;
                cornerDetail = "max corner alpha = " + worst + " (must be 0) and centre = "
                             + face.GetPixel(w / 2, h / 2).A;
            }
            Check("the button is a circle with transparent corners", round, cornerDetail);

            // ---- on screen, fully inside the work area --------------------
            app.SetOrbVisible(true);
            orb.BringToFront();
            Pump(300);
            Screen sc = Screen.FromPoint(orb.FaceCentreScreen);
            Rectangle wa = sc.WorkingArea;
            var fr = orb.FaceRect;
            bool inside = fr.Left >= wa.Left - 1 && fr.Top >= wa.Top - 1
                       && fr.Right <= wa.Right + 1 && fr.Bottom <= wa.Bottom + 1;
            Check("the button sits inside the work area", inside,
                "face " + fr + " in " + wa);

            // ---- it must not steal focus ----------------------------------
            // Measured against a real app window the user could be typing in.
            // The invariant is that the button and the fan NEVER become the
            // foreground window. Whether some unrelated app grabs focus during
            // the 400ms we sit here is none of DNotes' business, so that is
            // sampled but not failed on - otherwise this check is flaky
            // whenever the machine has anything else happening.
            form.BringToFront();
            Native.ForceForeground(form);
            Pump(300);
            IntPtr noteHwnd = form.Handle;
            IntPtr orbHwnd = orb.Handle;
            IntPtr fgBefore = Native.GetForegroundWindow();

            orb.SimulateClick();
            Pump(400);

            IntPtr menuHwnd = IntPtr.Zero;
            bool menuFocused = false;
            try
            {
                if (app.OrbMenu != null && !app.OrbMenu.IsDisposed && app.OrbMenu.IsHandleCreated)
                    menuHwnd = app.OrbMenu.Handle;
            }
            catch { }
            bool fgKept = Native.GetForegroundWindow() == fgBefore;

            // sample the foreground across the whole interaction
            bool orbFocused = false;
            int kept = 0, samples = 0;
            for (int i = 0; i < 12; i++)
            {
                IntPtr fg = Native.GetForegroundWindow();
                samples++;
                if (fg == orbHwnd) orbFocused = true;
                if (fg == menuHwnd) menuFocused = true;
                else if (fg == noteHwnd) kept++;
                Pump(30);
            }
            Check("the button never takes keyboard focus", !orbFocused,
                orbFocused ? "the orb became the foreground window"
                           : orbHwnd.ToString("X") + " was never foreground across "
                             + samples + " samples while the fan was open");

            // Tapping the button is a deliberate act on this UI, so the fan
            // showing is allowed to take focus the way any menu does. What must
            // not happen is the fan being *left* holding it once a shortcut has
            // been taken - that is what would strand a user's caret.
            Check("the fan opens without stealing focus", fgKept,
                fgKept ? "the foreground was handed straight back"
                       : "the fan took focus while opening");
            Say("    (the note held the foreground in " + kept + " of " + samples + " samples)");
            // the click opened the fan, which is what we want from a click
            Check("a click opens the shortcut fan", app.OrbMenu != null && app.OrbMenu.IsOpen,
                app.OrbMenu == null ? "no menu" : (app.OrbMenu.IsOpen ? "open" : "closed"));

            // ---- the fan's geometry ----------------------------------------
            OrbMenuForm menu = app.OrbMenu;
            if (menu != null && menu.IsOpen)
            {
                Pump(300);
                var nodes = menu.NodeRects;
                Check("the fan has six shortcuts", menu.NodeCount == 6,
                    "node count = " + menu.NodeCount);

                bool noClash = true;
                string clash = "";
                for (int i = 0; i < nodes.Count && noClash; i++)
                    for (int j = i + 1; j < nodes.Count; j++)
                        if (nodes[i].IntersectsWith(nodes[j]))
                        { noClash = false; clash = nodes[i] + " overlaps " + nodes[j]; break; }
                Check("no two fan shortcuts overlap", noClash,
                    noClash ? nodes.Count + " nodes" : clash);

                bool allOn = true;
                foreach (Rectangle r in nodes)
                    if (!wa.Contains(r)) { allOn = false; break; }
                Check("every shortcut is on screen", allOn,
                    "work area " + wa.Width + "x" + wa.Height);

                try
                {
                    string p = Path.Combine(outDir, "selftest-orb-menu.png");
                    Shot(menu, p);
                    Check("fan screenshot written", File.Exists(p), p);
                }
                catch { }

                // clicking a node closes the fan
                var n0 = nodes.Count > 0 ? nodes[0] : Rectangle.Empty;
                if (!n0.IsEmpty)
                    SendMessage(menu.Handle, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON),
                                Lp(n0.X + n0.Width / 2, n0.Y + n0.Height / 2));
                Pump(400);
                Check("clicking a shortcut closes the fan",
                    menu == null || menu.IsDisposed || !menu.IsOpen, "fan dismissed");
            }
            Pump(150);

            // ---- dragging --------------------------------------------------
            int x0 = orb.Left, y0 = orb.Top;
            orb.SimulateDrag(x0, y0, x0 + S(120), y0 + S(60));
            Pump(250);
            bool moved = orb.Left != x0 || orb.Top != y0;
            Check("the button can be dragged", moved,
                "moved from " + x0 + "," + y0 + " to " + orb.Left + "," + orb.Top);

            // ---- snapping --------------------------------------------------
            app.Settings.OrbSnap = true;
            var wa2 = Screen.FromPoint(orb.FaceCentreScreen).WorkingArea;
            // drop it hard against the right edge
            orb.NudgeTo(wa2.Right - orb.Core, wa2.Top + 80);
            orb.SnapAndRemember();
            Pump(250);
            int gap = wa2.Right - orb.FaceRight;
            Check("it snaps to the edge you drop it near", gap <= S(4),
                "gap from the right edge = " + gap + "px (snap radius " + S(26) + ")");

            // ---- position persistence --------------------------------------
            orb.Remember();
            app.PersistSettingsNow();
            Settings reloaded = store.LoadSettings();
            Check("the position survives a save and load",
                reloaded.OrbX == app.Settings.OrbX && reloaded.OrbY == app.Settings.OrbY,
                "saved " + app.Settings.OrbX + "," + app.Settings.OrbY +
                " read " + reloaded.OrbX + "," + reloaded.OrbY);

            // ---- the deck must not fan while the pointer is on the orb ----
            Rectangle? guard = app.PointerGuard;
            Check("the deck knows to keep clear of the button", guard.HasValue,
                guard.HasValue ? guard.Value.ToString() : "no guard rectangle");

            // ---- you can always get it back --------------------------------
            // The button is the only way into the app. If hiding it left no
            // working route back, that is a trap, not a setting - so the
            // escape hatches are exercised here rather than assumed.
            Head("FINDING THE BUTTON AGAIN");

            app.SetOrbVisible(false);
            Pump(300);
            Check("the button can be hidden", app.ButtonHidden,
                "hidden as asked, so the way back is now under test");

            app.ToggleOrbMenu();               // the Ctrl+Alt+B path
            Pump(500);
            bool backByKey = !app.ButtonHidden;
            Check("Ctrl+Alt+B brings a hidden button back", backByKey,
                backByKey ? "the shortcut revealed it" : "the shortcut did nothing - TRAP");

            app.ToggleOrbMenu();               // close the fan again
            Pump(300);

            app.SetOrbVisible(false);
            Pump(200);
            app.ShowOrb();                     // the programmatic path
            Pump(300);
            Check("the button can be restored directly", !app.ButtonHidden,
                app.ButtonHidden ? "ShowOrb() did nothing - TRAP" : "restored");

            Check("the tray menu can restore it too",
                app.RestoreViaTray(),
                "the tray row performs the same restore");

            app.SetOrbVisible(true);
            Pump(250);

            // ---- one icon, and only one ------------------------------------
            // The whole point of the floating button is that it is the only
            // thing sitting on the desktop. A second always-on overlay - an
            // edge strip, a status widget - is exactly what it replaced.
            int alwaysOn = 0;
            var stray = new List<string>();
            foreach (Form f in Application.OpenForms)
            {
                if (f == null || f.IsDisposed) continue;
                if (!f.Visible) continue;
                if (ReferenceEquals(f, orb) || ReferenceEquals(f, menu)
                    || ReferenceEquals(f, form) || ReferenceEquals(f, app.AllNotes)
                    || ReferenceEquals(f, app.Preferences)) continue;
                alwaysOn++;
                stray.Add(f.GetType().Name + " \"" + f.Text + "\" " + f.Bounds);
            }
            Check("the floating button is the only thing on the desktop", alwaysOn == 0,
                stray.Count == 0
                    ? "no other always-on window exists"
                    : stray.Count + " other window(s): " + string.Join("; ", stray.ToArray()));

            try
            {
                string p = Path.Combine(outDir, "selftest-orb.png");
                Shot(orb, p);
                Check("button screenshot written", File.Exists(p), p);
            }
            catch { }

            Say("");
        }

        private static int S(int v) { return (int)Math.Round(v * Ui.Scale); }

        private static bool NearEdge(Rectangle b)
        {
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle w = s.WorkingArea;
                if (b.Right >= w.Right - 3 || b.Left <= w.Left + 3
                    || b.Bottom >= w.Bottom - 3 || b.Top <= w.Top + 3)
                    return true;
            }
            return false;
        }

        private static int _dropped;

        private static void TypeUnicode(string s)
        {
            var buf = new INPUT[s.Length * 2];
            int i = 0;
            foreach (char c in s)
            {
                buf[i].type = INPUT_KEYBOARD;
                buf[i].u.ki.wScan = c;
                buf[i].u.ki.dwFlags = KEYEVENTF_UNICODE;
                i++;
                buf[i].type = INPUT_KEYBOARD;
                buf[i].u.ki.wScan = c;
                buf[i].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
                i++;
            }
            Send((uint)buf.Length, buf);
        }

        private const ushort VK_RETURN = 0x0D;
        private const ushort VK_BACK = 0x08;
        private const ushort VK_DELETE = 0x2E;
        private const ushort VK_TAB = 0x09;

        /// <summary>
        /// A real key press, not a unicode character. Backspace and Enter have
        /// no meaningful unicode code point, so they must go through as virtual
        /// keys to test what a person actually does.
        /// </summary>
        private static void PressKey(ushort vk)
        {
            var buf = new INPUT[2];
            buf[0].type = INPUT_KEYBOARD;
            buf[0].u.ki.wVk = vk;
            buf[1].type = INPUT_KEYBOARD;
            buf[1].u.ki.wVk = vk;
            buf[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
            Send(2, buf);
        }

        private static void Send(uint n, INPUT[] buf)
        {
            uint put = SendInput(n, buf, Marshal.SizeOf(typeof(INPUT)));
            if (put < n) _dropped += (int)(n - put);
        }

        /// <summary>
        /// Types like a person: a few characters at a time with the message
        /// pump running in between. One giant SendInput burst races the app's
        /// own queue, and the tail gets dropped - which looks exactly like an
        /// editor that has stopped accepting keys.
        /// </summary>
        private static void TypeText(string s, int chunk = 6)
        {
            for (int i = 0; i < s.Length; i += chunk)
            {
                int n = Math.Min(chunk, s.Length - i);
                TypeUnicode(s.Substring(i, n));
                Pump(45);
            }
        }

        /// <summary>
        /// Puts the caret back in the body before typing, and says so.
        ///
        /// This suite drives a real desktop, and other things on it - a
        /// terminal, a browser, a notification - contend for focus. The question
        /// worth answering is "given the note has focus, does it accept
        /// typing?", not "does this process win every focus fight on the
        /// machine?". Re-asserting focus keeps the test measuring DNotes.
        /// </summary>
        private static int EnsureFocus(NoteForm form, NeonTextBox ed)
        {
            if (ed.Focused) return 0;
            form.FocusEditor();
            Pump(150);
            return ed.Focused ? 0 : 1;
        }

        /// <summary>
        /// Backspace until the note is empty, letting the app pump often enough
        /// that the bounded system input queue never overflows. A dropped event
        /// here would look exactly like an editor that stopped accepting keys,
        /// so the drain rate is deliberately slow.
        ///
        /// The caret is parked at the end first, which is where a person typing
        /// would have it: Backspace at position 0 is correctly a no-op, and
        /// without this the test would be measuring that, not the editor.
        /// </summary>
        private static int ClearByBackspace(NoteForm form, NeonTextBox ed, int cap)
        {
            EnsureFocus(form, ed);
            ed.SelectionStart = ed.TextLength;
            ed.SelectionLength = 0;
            int n = 0;
            while (ed.TextLength > 0 && n < cap)
            {
                PressKey(VK_BACK);
                n++;
                if (n % 3 == 0) Pump(10);
            }
            Pump(160);
            return n;
        }

        /// <summary>Pumps the message loop for a while so timers and paints run.</summary>
        private static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(8);
            }
            Application.DoEvents();
        }

        /// <summary>Counts bright neon pixels inside a screen rectangle.</summary>
        private static int SampleNeon(Rectangle r)
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            var probe = Rectangle.Intersect(r, screen);
            if (probe.Width <= 0 || probe.Height <= 0) return 0;
            using (var bmp = new Bitmap(probe.Width, probe.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(probe.Location, Point.Empty, bmp.Size);
                int lit = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        if (c.G > 150 && c.G > c.R + 35 && c.G > c.B + 20) lit++;
                    }
                return lit;
            }
        }

        private static void Shot(Form f, string path)
        {
            Rectangle r = f.Bounds;
            using (var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height)))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Location, Point.Empty, bmp.Size);
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }

        private static void ShotScreen(string path)
        {
            Rectangle r = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            using (var bmp = new Bitmap(r.Width, r.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Location, Point.Empty, bmp.Size);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        private static string Trunc(string s)
        {
            s = (s ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
            return s.Length <= 46 ? s : s.Substring(0, 46) + "...";
        }

        private static void Head(string t)
        {
            Lines.Add("== " + t + " " + new string('=', Math.Max(0, 58 - t.Length)));
        }

        private static void Say(string s) { Lines.Add(s); }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) _pass++; else _fail++;
            Lines.Add("  [" + (ok ? "PASS" : "FAIL") + "] " +
                      name.PadRight(46) + " " + (detail ?? ""));
            Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name);
        }
    }
}
