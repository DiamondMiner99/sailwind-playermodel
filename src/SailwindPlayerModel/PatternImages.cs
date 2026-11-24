using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Pictures players add as clothing patterns: PNG or JPG files in BepInEx/config/SailwindPlayerModel/Patterns. Each
    /// one is listed in the Pattern dropdown under its file name and tiled over the cloth or trim it is picked for, in
    /// its own colors, with its transparent parts showing the color underneath (BodyPattern.cginc).
    ///
    /// A look names its picture ("img:Name" in the pattern key), not the picture itself, so the look string stays short.
    /// A body whose look names a picture that is not in the folder here, a crewmate's for example, shows that cloth
    /// plain. Two crewmates with a file of the same name each see their own.
    ///
    /// The folder is read again whenever the character screen opens, so a picture dropped in while the game runs shows
    /// up there. Reading it only lists the files; a picture is decoded the first time a look or the Pattern list needs
    /// it. A file that cannot be used (not a picture, more than MaxSide on a side, larger than MaxBytes) is passed over
    /// with one log line and not read again until it changes.
    /// </summary>
    internal static class PatternImages
    {
        public const int MaxSide = 2048;
        public const int MaxNameLength = 32;
        private const int MaxImages = 16;
        private const long MaxBytes = 32L * 1024 * 1024;

        private sealed class Entry
        {
            public string Path;
            public DateTime Written;
            public long Bytes;
            public Texture2D Texture;   // null until first needed
            public float Aspect = 1f;
            public bool Failed;
        }

        private static readonly Dictionary<string, Entry> _byName = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly List<string> _names = new List<string>();
        // Textures of pictures since replaced or removed, still on bodies until those are drawn again (ReleaseReplaced).
        private static readonly List<Texture2D> _replaced = new List<Texture2D>();
        private static bool _scanned, _namesDirty = true;

        /// <summary>BepInEx/config/SailwindPlayerModel/Patterns.</summary>
        public static string Folder { get { return Path.Combine(Path.Combine(Paths.ConfigPath, "SailwindPlayerModel"), "Patterns"); } }

        /// <summary>
        /// The pictures found, by name, sorted, leaving out any already found unusable. Scans the folder the first time.
        /// </summary>
        public static IList<string> Names
        {
            get
            {
                if (!_scanned) Rescan();
                if (_namesDirty)
                {
                    _namesDirty = false;
                    _names.Clear();
                    foreach (var kv in _byName) if (!kv.Value.Failed) _names.Add(kv.Key);
                    _names.Sort(StringComparer.OrdinalIgnoreCase);
                }
                return _names;
            }
        }

        /// <summary>
        /// Decode every listed picture that has not been yet, so Names then lists only usable ones and stays that way until
        /// the next Rescan. For the Pattern list, which shows them all.
        /// </summary>
        public static void LoadListed()
        {
            if (!_scanned) Rescan();
            foreach (var kv in _byName) Ensure(kv.Key, kv.Value);
        }

        /// <summary>The picture with this name, and its width over its height. False when there is none here.</summary>
        public static bool TryGet(string name, out Texture2D texture, out float aspect)
        {
            texture = null;
            aspect = 1f;
            if (string.IsNullOrEmpty(name)) return false;
            if (!_scanned) Rescan();
            Entry e;
            if (!_byName.TryGetValue(name, out e) || !Ensure(name, e)) return false;
            texture = e.Texture;
            aspect = e.Aspect;
            return true;
        }

        /// <summary>
        /// The name a file is listed and saved under: its name without the extension, with anything but letters, digits,
        /// spaces, dashes and underscores made an underscore (the look string uses ; = and , between its fields), at
        /// most MaxNameLength long. Null when nothing is left.
        /// </summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var sb = new StringBuilder(Math.Min(raw.Length, MaxNameLength));
            foreach (char ch in raw)
            {
                if (sb.Length >= MaxNameLength) break;
                bool ok = char.IsLetterOrDigit(ch) || ch == ' ' || ch == '-' || ch == '_';
                sb.Append(ok ? ch : '_');
            }
            string s = sb.ToString().Trim();
            return s.Length > 0 ? s : null;
        }

        /// <summary>
        /// List the folder: new and changed files are taken in (decoded when first needed), removed ones dropped. Makes the
        /// folder the first time, so players can find where pictures go. True when the list or any picture changed; the
        /// caller then draws again the bodies that use pictures (PlayerAppearance.RecolorPictureBodies) and calls
        /// ReleaseReplaced.
        /// </summary>
        public static bool Rescan()
        {
            _scanned = true;
            bool changed = false;
            try
            {
                string folder = Folder;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                var files = new List<string>();
                foreach (var f in Directory.GetFiles(folder))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") files.Add(f);
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);

                var seen = new Dictionary<string, string>(StringComparer.Ordinal);   // name -> the file it came from
                int listed = 0;   // usable or not yet tried; a file already found unusable takes no slot
                foreach (var file in files)
                {
                    string name = Clean(Path.GetFileNameWithoutExtension(file));
                    if (name == null) continue;
                    string first;
                    if (seen.TryGetValue(name, out first))
                    {
                        Plugin.Log.LogWarning("[Patterns] " + Path.GetFileName(first) + " and " + Path.GetFileName(file) + " are both listed as \""
                            + name + "\"; only " + Path.GetFileName(first) + " is used.");
                        continue;
                    }

                    var info = new FileInfo(file);
                    Entry e;
                    bool unchanged = _byName.TryGetValue(name, out e) && e.Path == file && e.Written == info.LastWriteTimeUtc && e.Bytes == info.Length;
                    if (unchanged && e.Failed)
                    {
                        seen[name] = file;   // kept so it is not read again until it changes
                        continue;
                    }
                    if (listed >= MaxImages)
                    {
                        Plugin.Log.LogWarning("[Patterns] More than " + MaxImages + " pictures in the patterns folder; the rest are not listed.");
                        break;
                    }
                    seen[name] = file;
                    listed++;
                    if (unchanged) continue;
                    if (e != null && e.Texture != null) _replaced.Add(e.Texture);
                    _byName[name] = new Entry { Path = file, Written = info.LastWriteTimeUtc, Bytes = info.Length };
                    changed = true;
                }

                var gone = new List<string>();
                foreach (var name in _byName.Keys) if (!seen.ContainsKey(name)) gone.Add(name);
                foreach (var name in gone)
                {
                    if (_byName[name].Texture != null) _replaced.Add(_byName[name].Texture);
                    _byName.Remove(name);
                    changed = true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Patterns] Could not read the patterns folder: " + e.Message);
            }
            if (changed) _namesDirty = true;
            return changed;
        }

        /// <summary>
        /// Free the textures of pictures that were replaced or removed. Call once every body that showed them has been
        /// drawn again; a body still holding one would turn to a flat color.
        /// </summary>
        public static void ReleaseReplaced()
        {
            foreach (var t in _replaced) if (t != null) UnityEngine.Object.Destroy(t);
            _replaced.Clear();
        }

        private static bool Ensure(string name, Entry e)
        {
            if (e.Texture != null) return true;
            if (e.Failed) return false;
            e.Texture = Load(e.Path, name, out e.Aspect);
            if (e.Texture == null)
            {
                e.Failed = true;
                _namesDirty = true;
                return false;
            }
            return true;
        }

        private static Texture2D Load(string file, string name, out float aspect)
        {
            aspect = 1f;
            string shown = Path.GetFileName(file);
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxBytes)
                {
                    Plugin.Log.LogWarning("[Patterns] " + shown + " is larger than " + (MaxBytes / (1024 * 1024)) + " MB and is not used.");
                    return null;
                }
                byte[] data = File.ReadAllBytes(file);
                // Checked from the file's header before decoding, so a huge picture is never unpacked into memory.
                int w, h;
                if (TryReadSize(data, out w, out h) && (w > MaxSide || h > MaxSide))
                {
                    Plugin.Log.LogWarning("[Patterns] " + shown + " is " + w + " by " + h + "; pictures can be at most " + MaxSide + " on a side.");
                    return null;
                }
                // Color pictures: sRGB, so the game's linear lighting reads them as painted. Mipmapped and tiled.
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
                if (!ImageConversion.LoadImage(tex, data, false))
                {
                    UnityEngine.Object.Destroy(tex);
                    Plugin.Log.LogWarning("[Patterns] " + shown + " is not a picture the game can read.");
                    return null;
                }
                if (tex.width > MaxSide || tex.height > MaxSide)
                {
                    Plugin.Log.LogWarning("[Patterns] " + shown + " is " + tex.width + " by " + tex.height + "; pictures can be at most " + MaxSide + " on a side.");
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                tex.name = "Pattern " + name;
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.filterMode = FilterMode.Trilinear;
                tex.anisoLevel = 4;
                tex.Apply(true, true);   // mipmaps, then free the CPU copy
                aspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;
                Plugin.Log.LogInfo("[Patterns] Loaded " + name + " (" + tex.width + " by " + tex.height + ").");
                return tex;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Patterns] Could not load " + shown + ": " + e.Message);
                return null;
            }
        }

        /// <summary>A PNG's or JPG's width and height from its header, without decoding it. False when not found.</summary>
        private static bool TryReadSize(byte[] d, out int w, out int h)
        {
            w = h = 0;
            if (d == null) return false;
            // PNG: the signature, then the IHDR chunk with the width and height as big-endian 32-bit numbers.
            if (d.Length >= 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
            {
                w = (d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19];
                h = (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23];
                return w > 0 && h > 0;
            }
            // JPG: walk the segments to a start-of-frame marker, which holds the height then the width.
            if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < d.Length)
                {
                    if (d[i] != 0xFF) return false;
                    byte marker = d[i + 1];
                    if (marker == 0xFF) { i++; continue; }
                    int len = (d[i + 2] << 8) | d[i + 3];
                    bool frame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                    if (frame)
                    {
                        h = (d[i + 5] << 8) | d[i + 6];
                        w = (d[i + 7] << 8) | d[i + 8];
                        return w > 0 && h > 0;
                    }
                    if (len < 2) return false;
                    i += 2 + len;
                }
            }
            return false;
        }
    }
}
