using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;

namespace DNotes.Core
{
    /// <summary>User settings, persisted next to the notes.</summary>
    internal sealed class Settings
    {
        public bool AutoStart = true;
        public bool CollapseOnFocusLoss;   // off by default: surprising if you glance away
        public bool ConfirmDelete = true;
        public int DefaultAccent = 0;
        public int FontSize = 12;            // points
        public int HotkeyNew = HotkeyDefaults.New;
        public int HotkeyAll = HotkeyDefaults.All;
        public int HotkeyArchive = HotkeyDefaults.Archive;
        public int HotkeyButton = HotkeyDefaults.Button;
        public int HotkeyHide = HotkeyDefaults.Hide;

        // ---- floating button ------------------------------------------------
        public bool OrbShow = true;          // the AssistiveTouch-style orb
        public bool OrbSnap = true;          // snap to a screen edge when dropped
        public int OrbX = -99999;            // -99999 = not placed yet
        public int OrbY = -99999;

        public Settings Clone()
        {
            return (Settings)MemberwiseClone();
        }
    }

    /// <summary>Ctrl+Alt+<key> shaped ids, matching the Mac app's Opt+Cmd set.</summary>
    internal static class HotkeyDefaults
    {
        public const int New = 1;
        public const int All = 2;
        public const int Archive = 3;
        public const int Button = 4;   // opens the floating button's shortcut fan
        public const int Hide = 5;
    }

    /// <summary>
    /// Persistence. One JSON file per note under %APPDATA%\DNotes\notes, so a
    /// note can be read, backed up or carried away on its own - the same promise
    /// the Mac app makes with its .hmnote files. Writes go through a temp file
    /// and a replace, so a crash mid-save cannot truncate an existing note.
    /// </summary>
    internal sealed class Store
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public string Root { get; private set; }
        public string NotesDir { get { return Path.Combine(Root, "notes"); } }
        public string SettingsPath { get { return Path.Combine(Root, "settings.json"); } }
        public string TrashDir { get { return Path.Combine(Root, "trash"); } }

        public event EventHandler NotesChanged;

        public Store()
        {
            Root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DNotes");
        }

        /// <summary>Test hook: point the store somewhere disposable.</summary>
        public Store(string root)
        {
            Root = root;
        }

        public void EnsureDirs()
        {
            try
            {
                if (!Directory.Exists(Root)) Directory.CreateDirectory(Root);
                if (!Directory.Exists(NotesDir)) Directory.CreateDirectory(NotesDir);
                if (!Directory.Exists(TrashDir)) Directory.CreateDirectory(TrashDir);
            }
            catch (Exception ex)
            {
                Log.Write("could not create data dirs: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // notes
        // ------------------------------------------------------------------

        private static string SafeFileName(string id)
        {
            if (string.IsNullOrEmpty(id)) id = NewId();
            var sb = new StringBuilder(id.Length);
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || (c >= '0' && c <= '9') || c == '-' || c == '_';
                sb.Append(ok ? c : '_');
            }
            return sb.ToString();
        }

        public static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        public string NotePath(Note n)
        {
            return Path.Combine(NotesDir, SafeFileName(n.Id) + ".dnote");
        }

        public List<Note> LoadAll()
        {
            var list = new List<Note>();
            try
            {
                if (!Directory.Exists(NotesDir)) return list;
                foreach (string f in Directory.GetFiles(NotesDir, "*.dnote"))
                {
                    Note n = LoadFile(f);
                    if (n != null) list.Add(n);
                }
            }
            catch (Exception ex)
            {
                Log.Write("load all: " + ex.Message);
            }
            list.Sort((a, b) => b.Edited.CompareTo(a.Edited));
            return list;
        }

        public Note LoadFile(string path)
        {
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                var d = Json.Deserialize<Dictionary<string, object>>(text);
                if (d == null) return null;
                var n = new Note();
                n.Id = Str(d, "Id", Path.GetFileNameWithoutExtension(path));
                n.Title = Str(d, "Title", "");
                n.Rtf = Str(d, "Rtf", "");
                n.Tags = Str(d, "Tags", "");
                n.Accent = Num(d, "Accent", 0);
                n.Archived = Bool(d, "Archived", false);
                n.Pinned = Bool(d, "Pinned", false);
                n.Created = Long(d, "Created", DateTime.UtcNow.Ticks);
                n.Edited = Long(d, "Edited", n.Created);
                n.X = Num(d, "X", -32000);
                n.Y = Num(d, "Y", -32000);
                n.W = Num(d, "W", 340);
                n.H = Num(d, "H", 380);
                if (string.IsNullOrEmpty(n.Rtf)) n.Rtf = RtfText.FromPlain("");
                return n;
            }
            catch (Exception ex)
            {
                Log.Write("read " + Path.GetFileName(path) + ": " + ex.Message);
                return null;
            }
        }

        public void Save(Note n)
        {
            if (string.IsNullOrEmpty(n.Id)) n.Id = NewId();
            EnsureDirs();
            AtomicWrite(NotePath(n), Serialize(n));
            n.Dirty = false;
            Raise();
        }

        public void SaveAll(IEnumerable<Note> notes)
        {
            foreach (Note n in notes) Save(n);
        }

        public void Delete(Note n)
        {
            try
            {
                string p = NotePath(n);
                if (File.Exists(p))
                {
                    // keep a copy in trash so a mis-click is recoverable
                    EnsureDirs();
                    File.Copy(p, Path.Combine(TrashDir,
                        Path.GetFileNameWithoutExtension(p) + "-" +
                        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".dnote"), true);
                    File.Delete(p);
                }
            }
            catch (Exception ex) { Log.Write("delete: " + ex.Message); }
            Raise();
        }

        private static string Serialize(Note n)
        {
            var d = new Dictionary<string, object>();
            d["Id"] = n.Id;
            d["Title"] = n.Title ?? "";
            d["Rtf"] = n.Rtf ?? "";
            d["Tags"] = n.Tags ?? "";
            d["Accent"] = n.Accent;
            d["Archived"] = n.Archived;
            d["Pinned"] = n.Pinned;
            d["Created"] = n.Created;
            d["Edited"] = n.Edited;
            d["X"] = n.X; d["Y"] = n.Y; d["W"] = n.W; d["H"] = n.H;
            d["v"] = 1;
            return Json.Serialize(d);
        }

        // ------------------------------------------------------------------
        // settings
        // ------------------------------------------------------------------

        public Settings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new Settings();
                string text = File.ReadAllText(SettingsPath, Encoding.UTF8);
                var d = Json.Deserialize<Dictionary<string, object>>(text);
                if (d == null) return new Settings();
                var s = new Settings();                s.AutoStart = Bool(d, "AutoStart", true);                s.CollapseOnFocusLoss = Bool(d, "CollapseOnFocusLoss", s.CollapseOnFocusLoss);
                s.ConfirmDelete = Bool(d, "ConfirmDelete", true);
                s.DefaultAccent = Num(d, "DefaultAccent", 0);
                s.FontSize = Clamp(Num(d, "FontSize", 12), 8, 28);                s.HotkeyNew = Num(d, "HotkeyNew", HotkeyDefaults.New);
                s.HotkeyAll = Num(d, "HotkeyAll", HotkeyDefaults.All);
                s.HotkeyArchive = Num(d, "HotkeyArchive", HotkeyDefaults.Archive);
                s.HotkeyButton = Num(d, "HotkeyButton", HotkeyDefaults.Button);
                s.HotkeyHide = Num(d, "HotkeyHide", HotkeyDefaults.Hide);
                s.OrbShow = Bool(d, "OrbShow", s.OrbShow);
                s.OrbSnap = Bool(d, "OrbSnap", s.OrbSnap);
                s.OrbX = Num(d, "OrbX", -99999);
                s.OrbY = Num(d, "OrbY", -99999);
                return s;
            }
            catch (Exception ex)
            {
                Log.Write("settings: " + ex.Message);
                return new Settings();
            }
        }

        public void SaveSettings(Settings s)
        {
            try
            {
                var d = new Dictionary<string, object>();                d["AutoStart"] = s.AutoStart;                d["CollapseOnFocusLoss"] = s.CollapseOnFocusLoss;
                d["ConfirmDelete"] = s.ConfirmDelete;
                d["DefaultAccent"] = s.DefaultAccent;
                d["FontSize"] = s.FontSize;                d["HotkeyNew"] = s.HotkeyNew;
                d["HotkeyAll"] = s.HotkeyAll;
                d["HotkeyArchive"] = s.HotkeyArchive;
                d["HotkeyButton"] = s.HotkeyButton;
                d["HotkeyHide"] = s.HotkeyHide;
                d["OrbShow"] = s.OrbShow;
                d["OrbSnap"] = s.OrbSnap;
                d["OrbX"] = s.OrbX;
                d["OrbY"] = s.OrbY;
                d["v"] = 1;
                AtomicWrite(SettingsPath, Json.Serialize(d));
            }
            catch (Exception ex) { Log.Write("save settings: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private void Raise()
        {
            EventHandler h = NotesChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private void AtomicWrite(string path, string content)
        {
            EnsureDirs();
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tmp, path, null, true);
            else File.Move(tmp, path);
        }

        private static string Str(Dictionary<string, object> d, string k, string def)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null) return o.ToString();
            return def;
        }

        private static int Num(Dictionary<string, object> d, string k, int def)
        {
            object o;
            if (d == null || !d.TryGetValue(k, out o) || o == null) return def;
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        private static long Long(Dictionary<string, object> d, string k, long def)
        {
            object o;
            if (d == null || !d.TryGetValue(k, out o) || o == null) return def;
            try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        private static bool Bool(Dictionary<string, object> d, string k, bool def)
        {
            object o;
            if (d == null || !d.TryGetValue(k, out o) || o == null) return def;
            try { return Convert.ToBoolean(o, CultureInfo.InvariantCulture); }
            catch { return def; }
        }
    }

    /// <summary>Appends to %APPDATA%\DNotes\dnotes.log. Silent if it cannot.</summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    if (_path == null)
                        _path = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "DNotes", "dnotes.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.AppendAllText(_path,
                        DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                        "  " + msg + Environment.NewLine);
                }
            }
            catch { /* logging must never throw */ }
        }
    }
}
