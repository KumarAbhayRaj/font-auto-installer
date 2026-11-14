using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace FontAutoInstaller
{
    sealed class ScanResult
    {
        public readonly List<FontInfo> Fonts = new List<FontInfo>();
        // True when the download holds nothing but fonts and harmless extras (license, readme, specimen images).
        // Only then is it safe to send the download to the Recycle Bin.
        public bool FontOnly = true;
        public int ArchiveCount;
    }

    // Finds font files inside a standalone file, a folder, or a ZIP/RAR/7Z (nested archives too).
    static class PackageScanner
    {
        const int MaxDepth = 3;
        const long MaxArchiveBytes = 500L * 1024 * 1024; // font packs are far smaller; avoids chewing huge unrelated archives

        static readonly HashSet<string> FontExt = Set(".ttf", ".otf");
        static readonly HashSet<string> ArchiveExt = Set(".zip", ".rar", ".7z");
        static readonly HashSet<string> HarmlessExt = Set(
            "", ".woff", ".woff2", ".eot", ".svg", ".ttc", ".txt", ".md", ".pdf", ".rtf", ".html", ".htm",
            ".url", ".doc", ".docx", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".css", ".json", ".ini",
            ".ds_store", ".license", ".ofl");

        // Windows 11 ships bsdtar (libarchive), which extracts ZIP, RAR and 7Z for free.
        static readonly string Tar = Path.Combine(Environment.SystemDirectory, "tar.exe");

        static HashSet<string> Set(params string[] items)
        {
            return new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsPackageFile(string path)
        {
            string ext = Path.GetExtension(path);
            return FontExt.Contains(ext) || ArchiveExt.Contains(ext);
        }

        public static ScanResult Scan(string path, string workDir)
        {
            var result = new ScanResult();
            Visit(path, result, workDir, 0);
            return result;
        }

        static void Visit(string path, ScanResult r, string workDir, int depth)
        {
            if (Directory.Exists(path))
            {
                foreach (string file in FileUtil.FilesUnder(path))
                    VisitFile(file, r, workDir, depth);
            }
            else if (File.Exists(path))
            {
                VisitFile(path, r, workDir, depth);
            }
        }

        static void VisitFile(string file, ScanResult r, string workDir, int depth)
        {
            // macOS junk that rides along in many font zips.
            if (Path.GetFileName(file).StartsWith("._") || file.IndexOf("__MACOSX", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            string ext = Path.GetExtension(file);

            if (FontExt.Contains(ext))
            {
                FontInfo info = FontInfo.TryRead(file);
                if (info != null) r.Fonts.Add(info);
                else { Log.Write("  Unreadable font skipped: " + file); r.FontOnly = false; }
                return;
            }

            if (ArchiveExt.Contains(ext))
            {
                if (depth >= MaxDepth || new FileInfo(file).Length > MaxArchiveBytes)
                {
                    Log.Write("  Archive skipped (too deep or over 500 MB): " + file);
                    r.FontOnly = false;
                    return;
                }
                string dest = Path.Combine(workDir, "x" + r.ArchiveCount++);
                if (!Extract(file, dest, ext)) r.FontOnly = false;
                Visit(dest, r, workDir, depth + 1); // use whatever did extract
                return;
            }

            if (!HarmlessExt.Contains(ext)) r.FontOnly = false;
        }

        static bool Extract(string archive, string dest, string ext)
        {
            try
            {
                Directory.CreateDirectory(dest);
                // Copy to a plain ASCII name first: avoids tar choking on unusual characters
                // and never locks the original download.
                string local = dest + ext;
                File.Copy(archive, local, true);

                var psi = new ProcessStartInfo(Tar, "-xf \"" + local + "\" -C \"" + dest + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode == 0) return true;
                    Log.Write("  Could not fully extract " + archive + ": " + err.Trim());
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Write("  Extract failed for " + archive + ": " + ex.Message);
                return false;
            }
        }
    }
}
