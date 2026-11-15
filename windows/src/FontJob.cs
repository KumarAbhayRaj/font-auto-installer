using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FontAutoInstaller
{
    sealed class FontOutcome
    {
        public FontInfo Font;
        public InstallResult Result;
        public string InstalledPath;
    }

    sealed class JobReport
    {
        public readonly string Source;
        public readonly List<FontInfo> All = new List<FontInfo>();
        public readonly List<FontOutcome> Outcomes = new List<FontOutcome>();
        public bool FontOnly;
        public bool Recycled;

        public JobReport(string source) { Source = source; }

        public int Found { get { return All.Count; } }

        public int Count(InstallResult result) { return Outcomes.Count(o => o.Result == result); }

        public List<string> Names(InstallResult result)
        {
            return Outcomes.Where(o => o.Result == result).Select(o => o.Font.FullName).ToList();
        }

        public void Add(FontInfo font, InstallResult result, string installedPath)
        {
            Outcomes.Add(new FontOutcome { Font = font, Result = result, InstalledPath = installedPath });
            Log.Write("  " + result + ": " + font.FullName + "  (" + Path.GetFileName(font.FilePath) + ")");
        }

        public string Title
        {
            get
            {
                int installed = Count(InstallResult.Installed);
                if (installed > 0) return "Installed " + installed + (installed == 1 ? " font" : " fonts");
                if (Count(InstallResult.Failed) > 0) return "Font install failed";
                return "Fonts already installed";
            }
        }

        public string Subtitle
        {
            get
            {
                var sb = new StringBuilder(Path.GetFileName(Source.TrimEnd('\\')));
                sb.Append(Recycled ? " moved to Recycle Bin." : FontOnly ? " kept in Downloads." : " kept in Downloads (it has other files).");
                int skipped = Count(InstallResult.AlreadyInstalled), failed = Count(InstallResult.Failed);
                if (skipped > 0 && Count(InstallResult.Installed) > 0) sb.Append(" Skipped " + skipped + " already installed.");
                if (failed > 0) sb.Append(" " + failed + " failed, see log.");
                return sb.ToString();
            }
        }

        // One row per family for the popup: newly installed first, then ones you already had.
        public List<FontEntry> Entries()
        {
            return Outcomes
                .Where(o => o.Result != InstallResult.Failed)
                .GroupBy(o => o.Font.FamilyKey) // "Calvin Variable" and "Calvin" are one family
                .Select(g =>
                {
                    List<FontOutcome> fresh = g.Where(o => o.Result == InstallResult.Installed).ToList();
                    List<FontOutcome> source = fresh.Count > 0 ? fresh : g.ToList();
                    string preview = source.OrderBy(o => o.Font.IsItalic).Select(o => o.InstalledPath).FirstOrDefault(p => p != null);
                    string detail = fresh.Count > 0 ? Describe(fresh.Select(o => o.Font).ToList()) : "Already installed";
                    string name = g.Select(o => o.Font.Family).OrderBy(n => n.Length).First(); // shortest = plain family name
                    return new FontEntry(name, detail, preview, fresh.Count > 0);
                })
                .OrderByDescending(e => e.IsNew)
                .ToList();
        }

        static string Describe(List<FontInfo> fonts)
        {
            if (fonts.Any(f => f.IsVariable))
            {
                string text = "Variable, all weights";
                if (fonts.Any(f => f.IsVariable && f.IsItalic)) text += " + italic";
                int statics = fonts.Count(f => !f.IsVariable); // static italics kept next to an upright variable
                if (statics > 0) text += " + " + statics + (statics == 1 ? " italic" : " italics");
                return text;
            }
            return fonts.Count == 1 ? fonts[0].Style : fonts.Count + " styles";
        }
    }

    // One download in, fonts installed, download recycled. Shared by the tray app and the --scan dry run.
    static class FontJob
    {
        public static JobReport Run(string path, bool dryRun)
        {
            var report = new JobReport(path);
            string work = Path.Combine(AppPaths.WorkDir, Guid.NewGuid().ToString("N"));
            try
            {
                ScanResult scan = PackageScanner.Scan(path, work);
                report.All.AddRange(scan.Fonts);
                report.FontOnly = scan.FontOnly;
                if (scan.Fonts.Count == 0) return report;

                Log.Write((dryRun ? "[dry run] " : "") + "Fonts found in " + path + ": " + scan.Fonts.Count);
                HashSet<string> installed = FontInstaller.InstalledNames();
                foreach (FontInfo font in FontPicker.PickBest(scan.Fonts))
                {
                    string dest = null;
                    InstallResult result = dryRun
                        ? (installed.Contains(font.NameKey) ? InstallResult.AlreadyInstalled : InstallResult.Installed)
                        : FontInstaller.Install(font, installed, out dest);
                    report.Add(font, result, dest);
                }
                if (dryRun) return report;

                if (report.Count(InstallResult.Installed) > 0) NativeMethods.BroadcastFontChange();
                if (report.Count(InstallResult.Failed) == 0 && scan.FontOnly)
                {
                    report.Recycled = NativeMethods.SendToRecycleBin(path);
                    Log.Write(report.Recycled ? "  Moved to Recycle Bin." : "  Could not move to Recycle Bin.");
                }
                else
                {
                    Log.Write("  Download kept in place.");
                }
                InstallHistory.Append(report);
                return report;
            }
            finally
            {
                FileUtil.DeleteTree(work);
            }
        }
    }

    // Remembers what got installed, so a missed popup isn't lost. Plain text, one font family per line.
    static class InstallHistory
    {
        static readonly object Gate = new object();

        static string FilePath { get { return Path.Combine(AppPaths.AppDir, "history.txt"); } }

        public static void Append(JobReport report)
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            string source = Clean(Path.GetFileName(report.Source.TrimEnd('\\')));
            var lines = report.Entries().Where(e => e.IsNew)
                .Select(e => string.Join("\t", stamp, Clean(e.Family), Clean(e.Detail), e.FilePath ?? "", source))
                .ToList();
            if (lines.Count == 0) return;
            lock (Gate)
            {
                try { File.AppendAllLines(FilePath, lines); }
                catch (Exception ex) { Log.Write("History write failed: " + ex.Message); }
            }
        }

        // Newest first, one row per family.
        public static List<FontEntry> Recent(int max)
        {
            var result = new List<FontEntry>();
            string[] lines;
            lock (Gate)
            {
                try { lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath) : new string[0]; }
                catch (Exception) { return result; }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = lines.Length - 1; i >= 0 && result.Count < max; i--)
            {
                string[] p = lines[i].Split('\t');
                if (p.Length < 5 || !seen.Add(p[1])) continue;
                DateTime when;
                string date = DateTime.TryParse(p[0], out when) ? when.ToString("d MMM") : p[0];
                result.Add(new FontEntry(p[1], date + "  ·  " + p[2], p[3], true));
            }
            return result;
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }
    }

    static class ScanReport
    {
        public static int Run(string path)
        {
            NativeMethods.AttachParentConsole();
            JobReport r = FontJob.Run(path, true);
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("Fonts found: " + r.Found);
            foreach (FontInfo f in r.All)
                sb.AppendLine(string.Format("  {0}  {1,-8}  {2}  [{3}]", f.IsOtf ? "OTF" : "TTF",
                    f.IsVariable ? "variable" : "static", f.FullName, Path.GetFileName(f.FilePath)));
            List<string> add = r.Names(InstallResult.Installed), skip = r.Names(InstallResult.AlreadyInstalled);
            sb.AppendLine("Would install: " + add.Count);
            foreach (string n in add) sb.AppendLine("  + " + n);
            sb.AppendLine("Already installed (skip): " + skip.Count);
            foreach (string n in skip) sb.AppendLine("  = " + n);
            sb.AppendLine("Download would go to Recycle Bin: " + (r.Found > 0 && r.FontOnly ? "yes" : "no"));
            Console.Write(sb.ToString());
            return 0;
        }
    }
}
