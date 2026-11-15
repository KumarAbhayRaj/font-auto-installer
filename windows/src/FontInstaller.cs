using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace FontAutoInstaller
{
    enum InstallResult { Installed, AlreadyInstalled, Failed }

    // Per-user install (same as right-click > Install): no admin rights needed.
    static class FontInstaller
    {
        const string FontsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";

        public static readonly string UserFontDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Fonts");

        // Normalized full names of every font already installed (system wide and for this user).
        public static HashSet<string> InstalledNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddNames(Registry.LocalMachine, names);
            AddNames(Registry.CurrentUser, names);
            return names;
        }

        static void AddNames(RegistryKey hive, HashSet<string> names)
        {
            using (RegistryKey key = hive.OpenSubKey(FontsKey))
            {
                if (key == null) return;
                foreach (string value in key.GetValueNames())
                {
                    string n = value;
                    int paren = n.LastIndexOf(" (", StringComparison.Ordinal);
                    if (paren > 0) n = n.Substring(0, paren); // drop " (TrueType)"
                    foreach (string part in n.Split(new[] { " & " }, StringSplitOptions.RemoveEmptyEntries))
                        names.Add(FontInfo.Normalize(part));
                }
            }
        }

        // dest: where the font file now lives (null when it was already registered under another file).
        public static InstallResult Install(FontInfo font, HashSet<string> installed, out string dest)
        {
            dest = null;
            if (installed.Contains(font.NameKey)) return InstallResult.AlreadyInstalled;

            bool copied = false;
            try
            {
                Directory.CreateDirectory(UserFontDir);
                bool sameFileThere;
                dest = FreeDestination(font.FilePath, out sameFileThere);
                if (sameFileThere) return InstallResult.AlreadyInstalled;

                File.Copy(font.FilePath, dest);
                copied = true;
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(FontsKey))
                    key.SetValue(font.FullName + (font.IsOtf ? " (OpenType)" : " (TrueType)"), dest);
                NativeMethods.AddFontResource(dest);

                installed.Add(font.NameKey);
                return InstallResult.Installed;
            }
            catch (Exception ex)
            {
                Log.Write("  Install failed for " + font.FullName + ": " + ex.Message);
                if (copied) { try { File.Delete(dest); } catch (Exception) { } }
                dest = null;
                return InstallResult.Failed;
            }
        }

        // Same file name already there: identical size means same font; otherwise pick "name_2.ttf".
        static string FreeDestination(string source, out bool sameFileThere)
        {
            string name = Path.GetFileNameWithoutExtension(source);
            string ext = Path.GetExtension(source);
            string target = Path.Combine(UserFontDir, name + ext);
            sameFileThere = File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length;
            if (!File.Exists(target) || sameFileThere) return target;

            for (int i = 2; ; i++)
            {
                target = Path.Combine(UserFontDir, name + "_" + i + ext);
                if (!File.Exists(target)) return target;
            }
        }
    }
}
