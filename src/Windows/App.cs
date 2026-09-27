using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DNotes.Windows
{
    using Core;

    /// <summary>A hidden message-only window that receives WM_HOTKEY.</summary>
    internal sealed class HotkeyWindow : NativeWindow
    {
        public event Action<int> Hotkey;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "DNotesHotkeys",
                Style = 0,
                ExStyle = Native.WS_EX_TOOLWINDOW,
                Parent = new IntPtr(-3) /* HWND_MESSAGE */
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                Action<int> h = Hotkey;
                if (h != null) h(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
    }

    /// <summary>
    /// Owns the whole app: the note collection, the edge deck, every open note
    /// window, the tray icon, the global hotkeys and the settings.
    /// </summary>
    internal sealed class App : IDisposable
    {
        private readonly Store _store;
        private Settings _settings;
        private List<Note> _notes = new List<Note>();
        private readonly Dictionary<string, NoteForm> _open = new Dictionary<string, NoteForm>();
        private AllNotesForm _all;
        private SettingsForm _prefs;
        private NotifyIcon _tray;
        private HotkeyWindow _hk;

        private readonly System.Windows.Forms.Timer _saveDebounce;
        private bool _hidden;
        private bool _quitting;

        public Settings Settings { get { return _settings; } }
        public Store Store { get { return _store; } }
        public List<Note> Notes { get { return _notes; } }
        public bool ActivateOnOpen { get; set; }
        public int OpenNoteCount { get { return _open.Count; } }
        public bool Hidden { get { return _hidden; } }

        public event EventHandler NotesReloaded;

        /// <summary>
        /// Set once the tray exists so the process-level exception guard can
        /// surface a problem without holding a reference to the App. No-op
        /// before that, so it is always safe to call.
        /// </summary>
        public static Action<string> ProblemReporter;

        public static void ReportProblem(string message)
        {
            Action<string> r = ProblemReporter;
            if (r != null)
            {
                try { r(message); return; }
                catch { }
            }
            try { Log.Write("problem (no reporter): " + message); } catch { }
        }

        public App(Store store)
        {
            _store = store;
            _settings = store.LoadSettings();
            ActivateOnOpen = true;

            _saveDebounce = new System.Windows.Forms.Timer { Interval = 400 };
            _saveDebounce.Tick += (s, e) => { _saveDebounce.Stop(); PersistAll(); };
        }

        // ==================================================================
        // lifecycle
        // ==================================================================

        public void Start()
        {
            _store.EnsureDirs();
            LoadNotes();

            if (_notes.Count == 0) SeedSample();

            Application.Run(new AppContext(this));
        }

        /// <summary>Used by the self-test: build the UI without entering the loop.</summary>
        public void BuildForTest()
        {
            BuildUi();
        }

        public string ScreenshotPath { get; set; }

        /// <summary>The first open note window, if any - used by diagnostics.</summary>
        public NoteForm FirstOpenNote()
        {
            foreach (NoteForm f in _open.Values)
                if (f != null && !f.IsDisposed) return f;
            return null;
        }

        public AllNotesForm AllNotes { get { return _all; } }
        public SettingsForm Preferences { get { return _prefs; } }
        public AboutForm About { get { return _about; } }

        public void ReapplyFonts()
        {
            foreach (NoteForm f in new List<NoteForm>(_open.Values))
                if (f != null && !f.IsDisposed) f.ReapplyFont();
        }

        private void SeedSample()
        {
            var welcome = new Note
            {
                Id = Store.NewId(),
                Title = "WELCOME TO DNOTES",
                Accent = 0,
                Created = DateTime.UtcNow.Ticks,
                Edited = DateTime.UtcNow.Ticks,
            };
            welcome.Rtf = RtfText.FromPlain(
                                "This is a DNote, and you can write on it." + "\n\n" +
                "  THE BUTTON" + "\n" +
                "  One floating button holds everything. Click it for the" + "\n" +
                "  shortcut fan, right-click for the full list, drag it" + "\n" +
                "  anywhere you like." + "\n\n" +
                "  WRITING" + "\n" +
                "  Just type. It saves to disk a quarter second after you stop.\n" +
                "  Ctrl+B / I / U  format      Ctrl+F  find\n" +
                "  Ctrl+1..6      change the neon colour\n" +
                "  Ctrl+Tab       jump to the next note\n" +
                "  Esc            tuck the note away\n\n" +
                "  CYAN = shortcuts, GREEN = your notes.");
            _notes.Insert(0, welcome);
            _store.Save(welcome);
        }

        /// <summary>Hidden message loop, so the app lives in the tray.</summary>
        private sealed class AppContext : ApplicationContext
        {
            private readonly App _a;
            public AppContext(App a)
            {
                _a = a;
                a.BuildUi();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _a.Shutdown();
                base.Dispose(disposing);
            }
        }

        private void BuildUi()
        {
            Ui.InitScale(new Control());

            _tray = new NotifyIcon
            {
                Icon = Assets.TrayIcon(32),
                Text = "DNotes",
                Visible = true,
            };
            _tray.DoubleClick += (s, e) => ToggleAllNotes();
            _tray.ContextMenuStrip = BuildTrayMenu();
            ProblemReporter = text =>
            {
                try
                {
                    _tray.BalloonTipTitle = "DNotes";
                    _tray.BalloonTipText = text;
                    _tray.BalloonTipIcon = ToolTipIcon.Warning;
                    _tray.ShowBalloonTip(6000);
                }
                catch { }
            };

            _hk = new HotkeyWindow();
            _hk.Hotkey += OnHotkey;
            RegisterHotkeys();

            BuildOrb();

            if (_settings.AutoStart) Autostart.Enable();
            else Autostart.Disable();

            if (!string.IsNullOrEmpty(ScreenshotPath)) ArmScreenshot();
        }

        // ==================================================================
        // the floating button
        // ==================================================================

        private OrbForm _orb;
        private OrbMenuForm _orbMenu;
        private ContextMenuStrip _orbMenuFlat;
        private ToolStripMenuItem _trayHideItem, _trayOrbItem;

        public OrbForm Orb { get { return _orb; } }
        public OrbMenuForm OrbMenu { get { return _orbMenu; } }

        /// <summary>Where the button or its fan currently sit, for the self-test.</summary>
        public Rectangle? PointerGuard
        {
            get
            {
                if (_orbMenu != null && !_orbMenu.IsDisposed && _orbMenu.Visible)
                    return _orbMenu.Bounds;
                if (_orb != null && !_orb.IsDisposed && _orb.Visible) return _orb.Bounds;
                return null;
            }
        }

        private void BuildOrb()
        {
            if (_orb != null) { _orb.Dispose(); _orb = null; }
            _orb = new OrbForm(this, _store);
            _orb.OrbClicked += (s, e) => ToggleOrbMenu();
            _orb.OrbRightClicked += (s, e) => ShowOrbFlatMenu();
            _orb.PlaceInitial();
            _orb.Show();
            _orb.Refresh();
            _orb.Visible = _settings.OrbShow;
        }

        /// <summary>
        /// Open the button's shortcut fan - and if the button is hidden, bring
        /// it back first.
        ///
        /// This shortcut must never be a dead key. With the edge deck gone the
        /// button is the only way into the app, so if hiding it left Ctrl+Alt+B
        /// doing nothing there would be no way back in at all short of the tray.
        /// </summary>
        public void ToggleOrbMenu()
        {
            if (_orbMenu != null && !_orbMenu.IsDisposed && _orbMenu.Visible) { _orbMenu.Close(); return; }
            if (_orb == null || _orb.IsDisposed) return;

            bool wasHidden = !_orb.Visible;
            if (wasHidden) { _hidden = false; SetOrbVisible(true); }

            if (_orbMenu == null || _orbMenu.IsDisposed)
                _orbMenu = new OrbMenuForm(this, _orb, OrbItems());
            _orbMenu.OpenNear();

            if (wasHidden)
                Balloon("DNOTES", "The button was hidden. It is back, with its menu open.");
        }

        /// <summary>True when the button is not on screen for any reason.</summary>
        public bool ButtonHidden
        {
            get { return _orb == null || _orb.IsDisposed || !_orb.Visible; }
        }

        /// <summary>Put the button back, whatever hid it.</summary>
        public void ShowOrb()
        {
            _hidden = false;
            SetOrbVisible(true);
            if (_orb != null && !_orb.IsDisposed) { _orb.BringToFront(); _orb.Refresh(); }
        }

        /// <summary>
        /// What the tray's "show the button" row does, exercised by the
        /// self-test so that row is known to work rather than hoped to.
        /// </summary>
        public bool RestoreViaTray()
        {
            if (!ButtonHidden) return true;
            ShowOrb();
            return !ButtonHidden;
        }

        private List<OrbMenuForm.Item> OrbItems()
        {
            return new List<OrbMenuForm.Item>
            {
                new OrbMenuForm.Item("NEW",     "plus",    Theme.Accents[0], () => NewNote()),
                                new OrbMenuForm.Item("NOTES",   "grid",    Theme.Accents[1], () => ToggleAllNotes()),
                new OrbMenuForm.Item("ARCHIVE", "box",     Theme.Accents[3], () => ToggleAllNotes(AllNotesForm.Filter.Archived)),
                new OrbMenuForm.Item("HIDE",    "eye",     Theme.Accents[2], () => ToggleHidden()),
                new OrbMenuForm.Item("SETTINGS","sliders", Theme.Accents[5], () => ShowSettings()),
                new OrbMenuForm.Item("QUIT",    "power",   Theme.Danger,      () => Quit()),
            };
        }

        /// <summary>The fast path: a plain list, for when the fan is too much.</summary>
        public void ShowOrbFlatMenu()
        {
            if (_orbMenuFlat == null)
            {
                _orbMenuFlat = new ContextMenuStrip
                {
                    RenderMode = ToolStripRenderMode.Professional,
                    Font = Theme.Ui(9f, FontStyle.Regular),
                    BackColor = Theme.SurfaceHi,
                    ForeColor = Theme.Text,
                    ShowImageMargin = false,
                };
                _orbMenuFlat.Renderer = new NeonMenuRenderer();
                Item(_orbMenuFlat, "NEW NOTE", "Ctrl+Alt+N", () => NewNote());
                Item(_orbMenuFlat, "ALL NOTES", "Ctrl+Alt+L", () => ToggleAllNotes());
                Item(_orbMenuFlat, "ARCHIVE", "Ctrl+Alt+A", () => ToggleAllNotes(AllNotesForm.Filter.Archived));
                _orbMenuFlat.Items.Add(new ToolStripSeparator());
                Item(_orbMenuFlat, "HIDE EVERYTHING", "Ctrl+Alt+H", () => ToggleHidden());
                Item(_orbMenuFlat, "BUTTON MENU", "Ctrl+Alt+B", () => ToggleOrbMenu());
                Item(_orbMenuFlat, "SETTINGS", "", () => ShowSettings());
                Item(_orbMenuFlat, "ABOUT", "", () => ShowAbout());
                _orbMenuFlat.Items.Add(new ToolStripSeparator());
                Item(_orbMenuFlat, "QUIT DNOTES", "", () => Quit());
            }
            if (_orb != null) _orbMenuFlat.Show(_orb, new Point(4, _orb.Height - 6));
            else _orbMenuFlat.Show(Cursor.Position);
        }

        public void SetOrbVisible(bool on)
        {
            _settings.OrbShow = on;
            if (_orb != null && !_orb.IsDisposed) _orb.Visible = on;
            if (!on && _orbMenu != null && !_orbMenu.IsDisposed) _orbMenu.Close();
            _hidden = !on;
            RefreshTrayLabels();
            if (_tray != null) _store.SaveSettings(_settings);
        }

        public void ResetOrbPosition()
        {
            if (_orb != null && !_orb.IsDisposed) _orb.ResetPosition();
        }

        /// <summary>
        /// Diagnostic capture. DNotes is DPI aware, so it is the only process
        /// here that can photograph its own windows in the right coordinate
        /// space; a non-aware capture comes back scaled and misses the edges.
        /// </summary>
        private void ArmScreenshot()
        {
            var t = new System.Windows.Forms.Timer { Interval = 4500 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                try
                {
                    System.IO.Directory.CreateDirectory(ScreenshotPath);
                    Rectangle b = Screen.PrimaryScreen.Bounds;
                    using (var bmp = new Bitmap(b.Width, b.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(b.Location, Point.Empty, bmp.Size);
                        bmp.Save(System.IO.Path.Combine(ScreenshotPath, "live-screen.png"),
                                 System.Drawing.Imaging.ImageFormat.Png);
                    }
                    if (_orb != null && !_orb.IsDisposed)
                    {
                        Rectangle d = _orb.Bounds;
                        using (var bmp = new Bitmap(Math.Max(1, d.Width), Math.Max(1, d.Height)))
                        {
                            using (Graphics g = Graphics.FromImage(bmp))
                                g.CopyFromScreen(d.Location, Point.Empty, bmp.Size);
                            bmp.Save(System.IO.Path.Combine(ScreenshotPath, "live-orb.png"),
                                     System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                }
                catch (Exception ex) { Log.Write("screenshot: " + ex.Message); }
                Quit();
            };
            t.Start();
        }

        public void Shutdown()
        {
            if (_quitting) return;
            _quitting = true;
            try
            {
                foreach (NoteForm f in new List<NoteForm>(_open.Values)) f.SaveNow(true);
                PersistAll();
                if (_orb != null) { _orb.Remember(); PersistAll(); }
                if (_orbMenu != null && !_orbMenu.IsDisposed) _orbMenu.Close();
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                if (_hk != null) { UnregisterHotkeys(); _hk.Dispose(); }
                if (_orb != null) { _orb.Visible = false; _orb.Dispose(); _orb = null; }
            }
            catch (Exception ex) { Log.Write("shutdown: " + ex.Message); }
        }

        public void Dispose() { Shutdown(); }

        // ==================================================================
        // hotkeys
        // ==================================================================

        private static uint Mods()
        {
            return Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        }

        private void RegisterHotkeys()
        {
            UnregisterHotkeys();
            Native.RegisterHotKey(_hk.Handle, _settings.HotkeyNew, Mods(), (uint)Keys.N);
            Native.RegisterHotKey(_hk.Handle, _settings.HotkeyAll, Mods(), (uint)Keys.L);
            Native.RegisterHotKey(_hk.Handle, _settings.HotkeyArchive, Mods(), (uint)Keys.A);
            Native.RegisterHotKey(_hk.Handle, _settings.HotkeyButton, Mods(), (uint)Keys.B);
            Native.RegisterHotKey(_hk.Handle, _settings.HotkeyHide, Mods(), (uint)Keys.H);
        }

        private void UnregisterHotkeys()
        {
            if (_hk == null || _hk.Handle == IntPtr.Zero) return;
            Native.UnregisterHotKey(_hk.Handle, _settings.HotkeyNew);
            Native.UnregisterHotKey(_hk.Handle, _settings.HotkeyAll);
            Native.UnregisterHotKey(_hk.Handle, _settings.HotkeyArchive);
            Native.UnregisterHotKey(_hk.Handle, _settings.HotkeyButton);
            Native.UnregisterHotKey(_hk.Handle, _settings.HotkeyHide);
        }

        private void OnHotkey(int id)
        {
            try
            {
                if (id == _settings.HotkeyNew) NewNote();
                else if (id == _settings.HotkeyAll) ToggleAllNotes();
                else if (id == _settings.HotkeyArchive) ToggleAllNotes(AllNotesForm.Filter.Archived);
                else if (id == _settings.HotkeyButton) ToggleOrbMenu();
                else if (id == _settings.HotkeyHide) ToggleHidden();
            }
            catch (Exception ex) { Log.Write("hotkey " + id + ": " + ex.Message); }
        }

        public void ToggleHidden()
        {
            _hidden = !_hidden;
            SetOrbVisible(!_hidden);
            if (_hidden)
            {
                foreach (NoteForm f in new List<NoteForm>(_open.Values)) f.Hide();
                if (_all != null && !_all.IsDisposed) _all.Hide();
            }
            else
            {
                SetOrbVisible(true);
            }
            Balloon("DNOTES", _hidden
                ? "Everything hidden.\nCtrl+Alt+B brings the button straight back."
                : "The button is back, along with your notes.");
        }

        // ==================================================================
        // notes
        // ==================================================================

        public void LoadNotes()
        {
            _notes = _store.LoadAll();
            NotesReloaded?.Invoke(this, EventArgs.Empty);
        }

        public List<Note> ActiveNotes()
        {
            var l = new List<Note>();
            foreach (Note n in _notes) if (!n.Archived) l.Add(n);
            return l;
        }

        public Note Find(string id)
        {
            foreach (Note n in _notes) if (n.Id == id) return n;
            return null;
        }

        public Note NewNote()
        {
            var n = new Note
            {
                Id = Store.NewId(),
                Title = "",
                Accent = _settings.DefaultAccent,
                Created = DateTime.UtcNow.Ticks,
                Edited = DateTime.UtcNow.Ticks,
                Rtf = RtfText.FromPlain(""),
            };
            _notes.Insert(0, n);
            _store.Save(n);
            RequestDeckRefresh();
            OpenNote(n.Id);
            return n;
        }

        public NoteForm OpenNote(string id)
        {
            Note n = Find(id);
            if (n == null) return null;

            NoteForm existing;
            if (_open.TryGetValue(id, out existing) && existing != null && !existing.IsDisposed)
            {
                existing.Show();
                if (_hidden) _hidden = false;
                existing.BringToFront();
                if (ActivateOnOpen)
                {
                    Native.ForceForeground(existing);
                    existing.FocusEditor();
                }
                return existing;
            }

            var f = new NoteForm(this, _store, n, ActivateOnOpen);
            f.NoteClosed += (s, e) =>
            {
                var nf = s as NoteForm;
                if (nf != null) _open.Remove(nf.NoteId);
            };
            _open[id] = f;
            f.Show();
            if (ActivateOnOpen) Native.ForceForeground(f);
            return f;
        }

        public void CloseNote(string id)
        {
            NoteForm f;
            if (_open.TryGetValue(id, out f) && f != null && !f.IsDisposed) f.Close();
        }

        public void CloseAllNotes()
        {
            foreach (NoteForm f in new List<NoteForm>(_open.Values))
                if (f != null && !f.IsDisposed) f.Close();
        }

        public void CollapseNote(NoteForm f)
        {
            if (f == null || f.IsDisposed) return;
            f.SaveNow(true);
            f.Hide();
        }

        public void CollapseOrToggle(NoteForm f)
        {
            if (f == null || f.IsDisposed) return;
            if (f.Visible) CollapseNote(f);
            else OpenNote(f.NoteId);
        }

        public void FocusAdjacentNote(NoteForm from, int dir)
        {
            if (from == null) return;
            var list = ActiveNotes();
            if (list.Count == 0) return;
            int i = list.FindIndex(x => x.Id == from.NoteId);
            if (i < 0) { OpenNote(list[0].Id); return; }
            int j = (i + dir + list.Count) % list.Count;
            OpenNote(list[j].Id);
        }

        /// <summary>Anything that changes the note list flushes settings and refreshes.</summary>
        public void RequestDeckRefresh()
        {
            _saveDebounce.Stop();
            _saveDebounce.Start();
            NotesReloaded?.Invoke(this, EventArgs.Empty);
        }
        private void PersistAll()
        {
            try { _store.SaveSettings(_settings); }
            catch (Exception ex) { Log.Write("persist settings: " + ex.Message); }
            if (_all != null && !_all.IsDisposed) _all.RefreshList();
        }

        /// <summary>Flush settings to disk now. Used by the self-test.</summary>
        public void PersistSettingsNow() { PersistAll(); }

        // ---- archive / delete from the tray & menus -------------------------
        public void ArchiveAll()
        {
            int c = 0;
            foreach (Note n in new List<Note>(ActiveNotes())) { n.Archived = true; n.Edited = DateTime.UtcNow.Ticks; _store.Save(n); c++; }
            CloseAllNotes();
            RequestDeckRefresh();
            Balloon("DNOTES", c + " note" + (c == 1 ? "" : "s") + " archived.");
        }

        public void UnarchiveAll()
        {
            int c = 0;
            foreach (Note n in new List<Note>(_notes))
                if (n.Archived) { n.Archived = false; n.Edited = DateTime.UtcNow.Ticks; _store.Save(n); c++; }
            RequestDeckRefresh();
            Balloon("DNOTES", c + " note" + (c == 1 ? "" : "s") + " restored.");
        }

        // ==================================================================
        // windows
        // ==================================================================

        public void ToggleAllNotes()
        {
            ToggleAllNotes(AllNotesForm.Filter.All);
        }

        public void ToggleAllNotes(AllNotesForm.Filter f)
        {
            if (_all != null && !_all.IsDisposed)
            {
                if (_all.Visible && _all.CurrentFilter == f) { _all.Hide(); return; }
                _all.SetFilter(f);
                _all.Show();
                Native.ForceForeground(_all);
                return;
            }
            _all = new AllNotesForm(this, _store);
            _all.FormClosed += (s, e) => { _all = null; };
            _all.SetFilter(f);
            _all.Show();
            Native.ForceForeground(_all);
        }

        public void ShowSettings()
        {
            if (_prefs != null && !_prefs.IsDisposed) { _prefs.Show(); Native.ForceForeground(_prefs); return; }
            _prefs = new SettingsForm(this, _store);
            _prefs.FormClosed += (s, e) => { _prefs = null; };
            _prefs.Show();
            Native.ForceForeground(_prefs);
        }

        private AboutForm _about;

        public void ShowAbout()
        {
            if (_about != null && !_about.IsDisposed)
            {
                _about.Show();
                Native.ForceForeground(_about);
                return;
            }
            _about = new AboutForm();
            _about.FormClosed += (s, e) => { _about = null; };
            _about.Show();
            Native.ForceForeground(_about);
        }

        private ContextMenuStrip BuildTrayMenu()
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
            Item(m, "NEW NOTE", "Ctrl+Alt+N", () => NewNote());
            Item(m, "ALL NOTES", "Ctrl+Alt+L", () => ToggleAllNotes());
            Item(m, "ARCHIVE", "Ctrl+Alt+A", () => ToggleAllNotes(AllNotesForm.Filter.Archived));
            m.Items.Add(new ToolStripSeparator());
                        _trayHideItem = MakeItem(m, _hidden ? "SHOW EVERYTHING" : "HIDE EVERYTHING", "Ctrl+Alt+H", () => ToggleHidden());
            _trayOrbItem = MakeItem(m, "BUTTON MENU", "Ctrl+Alt+B", () => ToggleOrbMenu());
            Item(m, "SETTINGS", "", () => ShowSettings());
            Item(m, "ABOUT", "", () => ShowAbout());
            m.Items.Add(new ToolStripSeparator());
            Item(m, "QUIT DNOTES", "", () => Quit());
            return m;
        }

        private static ToolStripMenuItem MakeItem(ContextMenuStrip m, string text, string key, Action a)
        {
            var it = new ToolStripMenuItem(text);
            it.ForeColor = Theme.Text;
            it.BackColor = Theme.SurfaceHi;
            if (!string.IsNullOrEmpty(key)) it.ShortcutKeyDisplayString = key;
            it.Click += (s, e) => a();
            m.Items.Add(it);
            return it;
        }

        /// <summary>Keep the tray rows honest about where the button is.</summary>
        private void RefreshTrayLabels()
        {
            if (_trayHideItem != null)
                _trayHideItem.Text = _hidden ? "SHOW EVERYTHING" : "HIDE EVERYTHING";
            if (_trayOrbItem != null)
                _trayOrbItem.Text = ButtonHidden ? "SHOW THE BUTTON" : "BUTTON MENU";
        }

        private static void Item(ContextMenuStrip m, string text, string key, Action a)
        {
            MakeItem(m, text, key, a);
        }

        public void Balloon(string title, string text)
        {
            try
            {
                if (_tray == null) return;
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = ToolTipIcon.None;
                _tray.ShowBalloonTip(2600);
            }
            catch { }
        }

        public void Quit()
        {
            _quitting = true;
            Shutdown();
            Application.ExitThread();
        }

        // ==================================================================
        // import / export
        // ==================================================================

        public void Export(IEnumerable<Note> notes, string kind)
        {
            var list = new List<Note>(notes);
            if (list.Count == 0) return;

            using (var dlg = new SaveFileDialog())
            {
                switch (kind)
                {
                    case "md":
                        dlg.Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*";
                        dlg.FileName = list.Count == 1 ? Safe(list[0].DisplayTitle) + ".md" : "dnotes.md";
                        dlg.Title = "Export as Markdown";
                        break;
                    case "txt":
                        dlg.Filter = "Plain text (*.txt)|*.txt|All files (*.*)|*.*";
                        dlg.FileName = list.Count == 1 ? Safe(list[0].DisplayTitle) + ".txt" : "dnotes.txt";
                        dlg.Title = "Export as plain text";
                        break;
                    case "archive":
                        dlg.Filter = "DNotes archive (*.dnotes)|*.dnotes|All files (*.*)|*.*";
                        dlg.FileName = "dnotes-archive.dnotes";
                        dlg.Title = "Export DNotes archive";
                        break;
                    default:
                        dlg.Filter = "HTML (*.html)|*.html";
                        dlg.FileName = "dnotes.html";
                        dlg.Title = "Export as one document";
                        break;
                }
                if (dlg.ShowDialog() != DialogResult.OK) return;
                string path = dlg.FileName;

                try
                {
                    switch (kind)
                    {
                        case "md":
                            if (list.Count == 1) File.WriteAllText(path, list[0].Markdown, Encoding.UTF8);
                            else
                            {
                                string dir = Path.Combine(Path.GetDirectoryName(path), "dnotes-md");
                                Directory.CreateDirectory(dir);
                                foreach (Note n in list)
                                    File.WriteAllText(Path.Combine(dir, Safe(n.DisplayTitle) + ".md"),
                                        n.Markdown, Encoding.UTF8);
                            }
                            break;
                        case "txt":
                            if (list.Count == 1) File.WriteAllText(path, list[0].PlainText, Encoding.UTF8);
                            else
                            {
                                var sb = new StringBuilder();
                                foreach (Note n in list)
                                {
                                    sb.AppendLine("=== " + n.DisplayTitle + " ===");
                                    sb.AppendLine(n.PlainText);
                                    sb.AppendLine();
                                }
                                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                            }
                            break;
                        case "archive":
                            File.WriteAllText(path, BuildArchive(list), Encoding.UTF8);
                            break;
                        default:
                            File.WriteAllText(path, BuildHtml(list), Encoding.UTF8);
                            break;
                    }
                    Balloon("EXPORTED", Path.GetFileName(path));
                }
                catch (Exception ex)
                {
                    Dialogs.Info(null, "EXPORT FAILED", ex.Message);
                }
            }
        }

        private static string BuildArchive(IEnumerable<Note> notes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("DNOTES-ARCHIVE 1");
            foreach (Note n in notes)
            {
                sb.AppendLine("--- NOTE");
                sb.AppendLine("id: " + n.Id);
                sb.AppendLine("title: " + (n.Title ?? "").Replace("\n", " "));
                sb.AppendLine("accent: " + n.Accent);
                sb.AppendLine("archived: " + (n.Archived ? 1 : 0));
                sb.AppendLine("created: " + n.Created);
                sb.AppendLine("edited: " + n.Edited);
                sb.AppendLine("--- BODY");
                sb.AppendLine(n.Rtf ?? "");
                sb.AppendLine("--- END");
            }
            return sb.ToString();
        }

        private static string BuildHtml(IEnumerable<Note> notes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!doctype html><meta charset=\"utf-8\"><title>DNotes</title>");
            sb.AppendLine("<style>body{background:#080d12;color:#d2f5e4;font:14px/1.6 Consolas,monospace;padding:32px;max-width:820px;margin:auto}");
            sb.AppendLine("h1{color:#2bff7a;font-size:20px;border-bottom:1px solid #142c21;padding-bottom:8px}");
            sb.AppendLine("h2{color:#2bff7a;font-size:15px;margin-top:28px}</style>");
            foreach (Note n in notes)
            {
                sb.AppendLine("<h2>" + Html(n.DisplayTitle) + "</h2><pre>" +
                              Html(n.PlainText) + "</pre>");
            }
            return sb.ToString();
        }

        private static string Html(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            string r = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(r) ? "note" : r;
        }

        public void Import(string path)
        {
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (!text.StartsWith("DNOTES-ARCHIVE"))
                {
                    // treat as a plain note
                    var n = new Note
                    {
                        Id = Store.NewId(),
                        Title = Path.GetFileNameWithoutExtension(path),
                        Rtf = RtfText.FromPlain(text),
                        Created = DateTime.UtcNow.Ticks,
                        Edited = DateTime.UtcNow.Ticks,
                    };
                    _notes.Insert(0, n);
                    _store.Save(n);
                }
                else
                {
                    string[] chunks = text.Split(new[] { "--- NOTE" }, StringSplitOptions.None);
                    int c = 0;
                    for (int i = 1; i < chunks.Length; i++)
                    {
                        string ch = chunks[i];
                        int bodyAt = ch.IndexOf("--- BODY", StringComparison.Ordinal);
                        if (bodyAt < 0) continue;
                        string head = ch.Substring(0, bodyAt);
                        string body = ch.Substring(bodyAt + 8);
                        int endAt = body.IndexOf("--- END", StringComparison.Ordinal);
                        if (endAt >= 0) body = body.Substring(0, endAt);

                        var n = new Note { Id = Store.NewId() };
                        foreach (string line in head.Split('\n'))
                        {
                            int ci = line.IndexOf(':');
                            if (ci < 0) continue;
                            string k = line.Substring(0, ci).Trim();
                            string v = line.Substring(ci + 1).Trim();
                            if (k == "title") n.Title = v;
                            else if (k == "accent") { int a; if (int.TryParse(v, out a)) n.Accent = a; }
                            else if (k == "archived") n.Archived = v == "1";
                            else if (k == "created") { long t; if (long.TryParse(v, out t)) n.Created = t; }
                            else if (k == "edited") { long t; if (long.TryParse(v, out t)) n.Edited = t; }
                        }
                        if (n.Edited == 0) n.Edited = DateTime.UtcNow.Ticks;
                        n.Rtf = body.Trim();
                        _notes.Insert(0, n);
                        _store.Save(n);
                        c++;
                    }
                    Balloon("IMPORTED", c + " note" + (c == 1 ? "" : "s") + " restored.");
                }
                RequestDeckRefresh();
            }
            catch (Exception ex)
            {
                Dialogs.Info(null, "IMPORT FAILED", ex.Message);
            }
        }

        public string DataFolder { get { return _store.Root; } }

        public string LastError { get; private set; }
    }

    /// <summary>Run-at-login toggle, written to the per-user Run key.</summary>
    internal static class Autostart
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private static string Exe
        {
            get
            {
                string p = Application.ExecutablePath;
                return "\"" + p + "\"";
            }
        }

        public static void Enable()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key))
                    k.SetValue("DNotes", Exe);
            }
            catch (Exception ex) { Log.Write("autostart on: " + ex.Message); }
        }

        public static void Apply(bool on)
        {
            if (on) Enable(); else Disable();
        }

        public static void Disable()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key, true))
                    if (k != null) k.DeleteValue("DNotes", false);
            }
            catch (Exception ex) { Log.Write("autostart off: " + ex.Message); }
        }

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key))
                        return k != null && k.GetValue("DNotes") != null;
                }
                catch { return false; }
            }
        }
    }
}
