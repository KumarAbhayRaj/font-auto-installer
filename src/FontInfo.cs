using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FontAutoInstaller
{
    // Reads the family / full name straight from the font file's "name" table,
    // and spots variable fonts by their "fvar" table or by the file name.
    sealed class FontInfo
    {
        public readonly string FilePath;
        public readonly string Family;
        public readonly string FullName;
        public readonly string Style;
        public readonly bool IsOtf;
        public readonly bool IsVariable;
        public readonly bool IsItalic;
        public readonly string FamilyKey;
        public readonly string NameKey; // "Bloka-BlackItalic" and "Bloka Black Italic" both become "blokablackitalic"

        // Letters and digits only, lowercase. Foundries name the OTF and TTF of one font inconsistently.
        public static string Normalize(string name)
        {
            return new string((name ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        static readonly Regex ItalicWord = new Regex(@"italic|oblique", RegexOptions.IgnoreCase);

        static readonly Regex StyleWords = new Regex(
            @"\b(thin|hairline|extra ?light|ultra ?light|light|book|regular|normal|medium|semi ?bold|demi ?bold|bold|extra ?bold|ultra ?bold|black|heavy|italic|oblique|variable|vf)\b",
            RegexOptions.IgnoreCase);

        FontInfo(string path, string family, string fullName, string style, bool hasTypoFamily, bool hasFvar)
        {
            Style = style;
            IsItalic = ItalicWord.IsMatch(style) || ItalicWord.IsMatch(Path.GetFileNameWithoutExtension(path));
            FilePath = path;
            Family = family;
            FullName = fullName;
            IsOtf = string.Equals(Path.GetExtension(path), ".otf", StringComparison.OrdinalIgnoreCase);

            string file = Path.GetFileName(path);
            IsVariable = hasFvar
                || file.IndexOf("variable", StringComparison.OrdinalIgnoreCase) >= 0
                || file.Contains("["); // Google Fonts style: Inter[wght].ttf

            // Without a typographic family name (ID 16), name ID 1 can be "Inter Bold"; strip style words.
            string key = hasTypoFamily ? Regex.Replace(family, @"\b(variable|vf)\b", "", RegexOptions.IgnoreCase)
                                       : StyleWords.Replace(family, "");
            FamilyKey = Normalize(key);
            NameKey = Normalize(fullName);
        }

        public static FontInfo TryRead(string path)
        {
            try
            {
                byte[] d = File.ReadAllBytes(path);
                if (d.Length < 12) return null;

                uint version = U32(d, 0);
                if (version != 0x00010000 && version != 0x4F54544F /* OTTO */ && version != 0x74727565 /* true */)
                    return null;

                int numTables = U16(d, 4);
                int nameTable = -1;
                bool hasFvar = false;
                for (int i = 0; i < numTables; i++)
                {
                    int rec = 12 + 16 * i;
                    if (rec + 16 > d.Length) return null;
                    string tag = Encoding.ASCII.GetString(d, rec, 4);
                    if (tag == "name") nameTable = (int)U32(d, rec + 8);
                    else if (tag == "fvar") hasFvar = true;
                }
                if (nameTable < 0 || nameTable + 6 > d.Length) return null;

                Dictionary<int, string> names = ReadNames(d, nameTable);
                string typoFamily = Get(names, 16);
                string family = typoFamily ?? Get(names, 1) ?? Path.GetFileNameWithoutExtension(path);
                string style = Get(names, 17) ?? Get(names, 2) ?? "Regular";
                string full = Get(names, 4) ?? (family + " " + style);

                return new FontInfo(path, family, full, style, typoFamily != null, hasFvar);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static Dictionary<int, string> ReadNames(byte[] d, int table)
        {
            var best = new Dictionary<int, string>();
            var score = new Dictionary<int, int>();
            int count = U16(d, table + 2);
            int strings = table + U16(d, table + 4);

            for (int i = 0; i < count; i++)
            {
                int r = table + 6 + 12 * i;
                if (r + 12 > d.Length) break;
                int platform = U16(d, r), lang = U16(d, r + 4), id = U16(d, r + 6);
                int len = U16(d, r + 8), off = U16(d, r + 10);
                if (id != 1 && id != 2 && id != 4 && id != 16 && id != 17) continue;

                // Prefer Windows English, then any Windows/Unicode, then Mac Roman.
                int s = platform == 3 ? (lang == 0x409 ? 4 : 3) : platform == 0 ? 2 : (platform == 1 && lang == 0) ? 1 : 0;
                int current;
                if (s == 0 || (score.TryGetValue(id, out current) && current >= s)) continue;

                int start = strings + off;
                if (start + len > d.Length) continue;
                Encoding enc = platform == 1 ? Encoding.GetEncoding(28591) : Encoding.BigEndianUnicode;
                string value = enc.GetString(d, start, len).Trim('\0', ' ');
                if (value.Length == 0) continue;

                best[id] = value;
                score[id] = s;
            }
            return best;
        }

        static string Get(Dictionary<int, string> names, int id)
        {
            string v;
            return names.TryGetValue(id, out v) ? v : null;
        }

        static int U16(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }
        static uint U32(byte[] b, int o) { return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3]; }
    }

    static class FontPicker
    {
        // Rules, per font family, judged separately for upright and italic styles:
        //   1. If a variable version exists, install only the variable files.
        //      (An upright-only variable file never hides the static italics.)
        //   2. If the same font exists as OTF and TTF, install the OTF.
        public static List<FontInfo> PickBest(IEnumerable<FontInfo> fonts)
        {
            var chosen = new List<FontInfo>();
            foreach (var family in fonts.GroupBy(f => f.FamilyKey + (f.IsItalic ? "|italic" : "")))
            {
                List<FontInfo> pool = family.Where(f => f.IsVariable).ToList();
                if (pool.Count == 0) pool = family.ToList();

                foreach (var sameFont in pool.GroupBy(f => f.NameKey))
                    chosen.Add(sameFont.FirstOrDefault(f => f.IsOtf) ?? sameFont.First());
            }
            return chosen;
        }
    }
}
