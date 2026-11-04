using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;

namespace SailwindPlayerModel
{
    /// <summary>
    /// The looks saved from the Character screen, in a text file of their own beside the mod's config: one per line,
    /// the name, a tab, then the look in the same "slot=variant" form the config uses. There is no limit on how many.
    /// Not config entries, so they do not fill the F1 window, and a crew can share a uniform by copying a line.
    ///
    /// A WRITE DOES NOT DROP WHAT IS IN THE FILE. It is read when the screen opens and again before every write, and a
    /// line that appeared in between is taken in rather than written over, so one pasted in while the screen is open
    /// survives. A note or a line this version cannot read is kept as it is, after the outfits. A file that cannot be
    /// read, because something has it locked or it is not UTF-8, is never written: the write is refused instead, since
    /// writing from a list that never loaded would replace every outfit in it. Edit the file with the screen closed:
    /// with it open, a line removed comes back at the next write and a line changed becomes two. A name longer than
    /// MaxNameLength is cut to fit.
    /// </summary>
    internal static class OutfitStore
    {
        internal sealed class Outfit
        {
            public string Name;
            public string Look;
        }

        private const string FileName = "com.diamondminer99.playermodel.outfits.txt";
        private const string Header = "# Saved outfits from the Character screen: a name, a tab, then the look. Copy a line to share one.";
        public const int MaxNameLength = 24;

        public static readonly List<Outfit> Outfits = new List<Outfit>();
        private static readonly List<string> _kept = new List<string>();
        // The file's lines as of the last read or write. A line on disk that is not here was put there by someone else.
        private static readonly HashSet<string> _seen = new HashSet<string>();

        /// <summary>The file could not be read when the screen opened, so the list may be short.</summary>
        public static bool LoadFailed { get; private set; }

        private static string FilePath { get { return Path.Combine(Paths.ConfigPath, FileName); } }

        public static void Load()
        {
            Outfits.Clear();
            _kept.Clear();
            _seen.Clear();
            LoadFailed = false;
            try
            {
                foreach (string line in ReadFile()) TakeIn(line);
            }
            catch (System.Exception e)
            {
                LoadFailed = true;
                Plugin.Log.LogWarning("[Character] Could not read the saved outfits: " + e.Message);
            }
        }

        /// <summary>
        /// Write the list out, first taking in anything added to the file since it was read. Through a temporary file, so
        /// a failed write leaves the last good file in place. False if nothing was written.
        /// </summary>
        public static bool Save()
        {
            string path = FilePath, temp = path + ".tmp";
            try
            {
                foreach (string line in ReadFile())
                    if (!_seen.Contains(line)) TakeIn(line);
                LoadFailed = false;

                var lines = new List<string> { Header };
                foreach (var o in Outfits) lines.Add(o.Name + "\t" + o.Look);
                lines.AddRange(_kept);
                File.WriteAllLines(temp, lines, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);

                _seen.Clear();
                foreach (string line in lines) _seen.Add(line);
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Character] Could not save the outfits: " + e.Message);
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
                return false;
            }
        }

        /// <summary>The file's lines, or none if it does not exist. Throws on bytes that are not valid text.</summary>
        private static List<string> ReadFile()
        {
            var lines = new List<string>();
            string path = FilePath;
            if (!File.Exists(path)) return lines;
            // Strict, so a file saved in another encoding fails here rather than coming back with its accented letters
            // replaced, and then being written that way. A byte order mark still selects UTF-16.
            using (var reader = new StreamReader(new MemoryStream(File.ReadAllBytes(path)), new UTF8Encoding(false, true), true))
            {
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
            }
            return lines;
        }

        private static void TakeIn(string line)
        {
            _seen.Add(line);
            if (line.Trim().Length == 0 || line == Header) return;
            int tab = line[0] == '#' ? -1 : line.IndexOf('\t');
            string name = tab > 0 ? CleanName(line.Substring(0, tab)) : "";
            string look = tab > 0 ? line.Substring(tab + 1).Trim() : "";
            if (name.Length == 0 || look.IndexOf('=') <= 0) _kept.Add(line);
            else Outfits.Add(new Outfit { Name = name, Look = look });
        }

        /// <summary>A name that fits on one line of the file and one row of the list. Empty if nothing is left of it.</summary>
        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (!char.IsControl(c)) sb.Append(c);
            string clean = sb.ToString().Trim();
            if (clean.Length > MaxNameLength) clean = clean.Substring(0, MaxNameLength).TrimEnd();
            // A line starting with # is a note, so a name cannot.
            return clean.TrimStart('#', ' ');
        }

        /// <summary>The first of "Outfit 1", "Outfit 2" and so on that is not taken.</summary>
        public static string NextName()
        {
            for (int n = 1; ; n++)
            {
                string name = "Outfit " + n;
                bool taken = false;
                foreach (var o in Outfits)
                    if (o.Name == name) { taken = true; break; }
                if (!taken) return name;
            }
        }
    }
}
