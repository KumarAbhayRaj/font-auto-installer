using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FontAutoInstaller
{
    sealed class JobReport
    {
        public readonly string Source;
        public readonly List<FontInfo> All = new List<FontInfo>();
        public readonly List<FontInfo> Chosen = new List<FontInfo>();
        public readonly List<string> Installed = new List<string>();
        public readonly List<string> Skipped = new List<string>();
        public readonly List<string> Failed = new List<string>();
        public bool FontOnly;
        public bool Recycled;

        public JobReport(string source) { Source = source; }

        public int Found { get { return All.Count; } }

        public void Add(FontInfo font, InstallResult result)
        {
            Chosen.Add(font);
            (result == InstallResult.Installed ? Installed : result == InstallResult.AlreadyInstalled ? Skipped : Failed)
                .Add(font.FullName);
            Log.Write("  " + result + ": " + font.FullName + "  (" + Path.GetFileName(font.FilePath) + ")");
        }

        public string Title
        {
            get
            {
                if (Installed.Count > 0) return "Installed " + Installed.Count + (Installed.Count == 1 ? " font" : " fonts");
                if (Failed.Count > 0) return "Font install failed";
                return "Fonts already installed";
            }
        }

        public string Body
        {
            get
            {
                List<string> families = Chosen.Select(f => f.Family).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var sb = new StringBuilder(string.Join(", ", families.Take(3)));
                if (families.Count > 3) sb.Append(" +" + (families.Count - 3) + " more");
                sb.Append(".");
                if (Skipped.Count > 0 && Installed.Count > 0) sb.Append(" Skipped " + Skipped.Count + " already installed.");
                if (Failed.Count > 0) sb.Append(" " + Failed.Count + " failed, see log.");
                sb.Append(Recycled ? " Download moved to Recycle Bin."
                        : FontOnly ? " Download kept." : " Download kept (it has other files).");
                return sb.ToString();
            }
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
                    InstallResult result = dryRun
                        ? (installed.Contains(font.FullName) ? InstallResult.AlreadyInstalled : InstallResult.Installed)
                        : FontInstaller.Install(font, installed);
                    report.Add(font, result);
                }
                if (dryRun) return report;

                if (report.Installed.Count > 0) NativeMethods.BroadcastFontChange();
                if (report.Failed.Count == 0 && scan.FontOnly)
                {
                    report.Recycled = NativeMethods.SendToRecycleBin(path);
                    Log.Write(report.Recycled ? "  Moved to Recycle Bin." : "  Could not move to Recycle Bin.");
                }
                else
                {
                    Log.Write("  Download kept in place.");
                }
                return report;
            }
            finally
            {
                FileUtil.DeleteTree(work);
            }
        }
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
            sb.AppendLine("Would install: " + r.Installed.Count);
            foreach (string n in r.Installed) sb.AppendLine("  + " + n);
            sb.AppendLine("Already installed (skip): " + r.Skipped.Count);
            foreach (string n in r.Skipped) sb.AppendLine("  = " + n);
            sb.AppendLine("Download would go to Recycle Bin: " + (r.Found > 0 && r.FontOnly ? "yes" : "no"));
            Console.Write(sb.ToString());
            return 0;
        }
    }
}
