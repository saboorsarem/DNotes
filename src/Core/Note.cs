using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;

namespace DNotes.Core
{
    /// <summary>One sticky note. Body is stored as RTF so formatting survives.</summary>
    internal sealed class Note
    {
        public string Id;
        public string Title = "";
        public string Rtf = "";
        public int Accent;
        public bool Archived;
        public bool Pinned;
        public long Created;
        public long Edited;

        public string Tags = "";

        // window geometry, remembered per note
        public int X = -32000, Y = -32000, W = 340, H = 380;

        [NonSerialized] public bool Dirty;

        public Color AccentColor
        {
            get
            {
                if (Accent < 0 || Accent >= Theme.Accents.Length) return Theme.Accents[0];
                return Theme.Accents[Accent];
            }
            set
            {
                int best = 0;
                double bd = double.MaxValue;
                for (int i = 0; i < Theme.Accents.Length; i++)
                {
                    double d = Dist(Theme.Accents[i], value);
                    if (d < bd) { bd = d; best = i; }
                }
                Accent = best;
            }
        }

        private static double Dist(Color a, Color b)
        {
            double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            return dr * dr + dg * dg + db * db;
        }

        public string DisplayTitle
        {
            get
            {
                string t = (Title ?? "").Trim();
                if (t.Length == 0) return "UNTITLED";
                return t.Length > 60 ? t.Substring(0, 59) + "\u2026" : t;
            }
        }

        /// <summary>Plain text, used for search, previews and export.</summary>
        public string PlainText
        {
            get { return RtfText.ToPlain(Rtf); }
        }

        public string Preview
        {
            get
            {
                string p = PlainText.Replace("\r", " ").Replace("\n", " ");
                p = Regex.Replace(p, @"\s+", " ").Trim();
                if (p.Length == 0) return "(empty)";
                return p.Length > 140 ? p.Substring(0, 139) + "\u2026" : p;
            }
        }

        public bool Matches(string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            string q = query.Trim().ToLowerInvariant();
            if (q.Length == 0) return true;
            if ((Title ?? "").ToLowerInvariant().Contains(q)) return true;
            if ((Tags ?? "").ToLowerInvariant().Contains(q)) return true;
            return PlainText.ToLowerInvariant().Contains(q);
        }

        public DateTime EditedUtc { get { return new DateTime(Edited, DateTimeKind.Utc).ToLocalTime(); } }
        public DateTime CreatedUtc { get { return new DateTime(Created, DateTimeKind.Utc).ToLocalTime(); } }

        public string EditedAgo
        {
            get
            {
                TimeSpan d = DateTime.Now - EditedUtc;
                if (d.TotalSeconds < 60) return "now";
                if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m ago";
                if (d.TotalHours < 24) return (int)d.TotalHours + "h ago";
                if (d.TotalDays < 7) return (int)d.TotalDays + "d ago";
                if (d.TotalDays < 365) return EditedUtc.ToString("d MMM", CultureInfo.InvariantCulture);
                return EditedUtc.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            }
        }

        public string Markdown
        {
            get
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(Title)) sb.AppendLine("# " + Title.Trim());
                if (!string.IsNullOrEmpty(Tags)) sb.AppendLine("_" + Tags.Trim() + "_");
                sb.AppendLine();
                sb.AppendLine(PlainText.TrimEnd());
                return sb.ToString();
            }
        }
    }

    /// <summary>Minimal RTF helpers: plain text in, RTF out and back.</summary>
    internal static class RtfText
    {
        private static readonly RichTextBoxAccessor _acc = new RichTextBoxAccessor();

        public static string ToPlain(string rtf)
        {
            if (string.IsNullOrEmpty(rtf)) return "";
            try { return _acc.Plain(rtf); }
            catch { return StripRtf(rtf); }
        }

        /// <summary>Builds a fresh RTF document around <paramref name="plain"/>.</summary>
        public static string FromPlain(string plain)
        {
            try { return _acc.Rtf(plain ?? "", Theme.Mono(11.5f, FontStyle.Regular), Theme.Text); }
            catch { return "{\\rtf1\\ansi " + (plain ?? "").Replace("\n", "\\line ") + "}"; }
        }

        /// <summary>Re-tones an existing RTF document to the app body style.</summary>
        public static string Restyle(string rtf, Color ink)
        {
            try { return _acc.Restyle(rtf, Theme.Mono(11.5f, FontStyle.Regular), ink); }
            catch { return rtf; }
        }

        private static string StripRtf(string s)
        {
            var sb = new StringBuilder();
            bool skip = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\')
                {
                    skip = true;
                    continue;
                }
                if (c == '{') { skip = true; continue; }
                if (c == '}') { skip = false; continue; }
                if (!skip) sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Does RTF round-tripping on a hidden offscreen RichTextBox. There is
    /// deliberately no shortcut here - letting a real RichTextBox parse the
    /// document is what guarantees a note written by the user reopens exactly
    /// as they left it, formatting and all.
    /// </summary>
    internal sealed class RichTextBoxAccessor
    {
        [ThreadStatic] private static System.Windows.Forms.RichTextBox _box;

        private static System.Windows.Forms.RichTextBox Box
        {
            get
            {
                if (_box == null || !_box.IsDisposed)
                {
                    _box = new System.Windows.Forms.RichTextBox();
                    _box.CreateControl();
                }
                return _box;
            }
        }

        public string Plain(string rtf)
        {
            System.Windows.Forms.RichTextBox b = Box;
            b.Rtf = rtf;
            return b.Text;
        }

        public string Rtf(string plain, Font f, Color ink)
        {
            System.Windows.Forms.RichTextBox b = Box;
            b.Rtf = "";
            b.Select(0, 0);
            b.SelectionFont = f;
            b.SelectionColor = ink;
            b.Text = plain;
            b.Select(0, 0);
            b.SelectionFont = f;
            b.SelectionColor = ink;
            return b.Rtf;
        }

        public string Restyle(string rtf, Font f, Color ink)
        {
            System.Windows.Forms.RichTextBox b = Box;
            b.Rtf = rtf;
            b.Select(0, b.TextLength);
            b.SelectionFont = f;
            b.SelectionColor = ink;
            b.Select(0, 0);
            return b.Rtf;
        }
    }
}
