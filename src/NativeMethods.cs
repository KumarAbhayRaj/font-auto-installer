using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace FontAutoInstaller
{
    static class NativeMethods
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "AddFontResourceW")]
        public static extern int AddFontResource(string file);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags,
            IntPtr token, out IntPtr path);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Sharp text on high-resolution screens.
        public static void EnableDpiAwareness()
        {
            try { SetProcessDPIAware(); } catch (Exception) { }
        }

        // Windows 11 rounded corners and a thin border color. Ignored on older Windows.
        public static void StyleWindow(IntPtr hwnd, System.Drawing.Color border)
        {
            try
            {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
                int colorRef = border.R | (border.G << 8) | (border.B << 16);
                DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref colorRef, sizeof(int));
            }
            catch (Exception) { }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        public static string DownloadsFolder()
        {
            IntPtr ptr;
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out ptr) == 0)
            {
                string path = Marshal.PtrToStringUni(ptr);
                Marshal.FreeCoTaskMem(ptr);
                return path;
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        // Tells open apps (Illustrator, Figma, Word...) that the font list changed.
        public static void BroadcastFontChange()
        {
            IntPtr ignored;
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001D /* WM_FONTCHANGE */, IntPtr.Zero, IntPtr.Zero,
                0x0002 /* SMTO_ABORTIFHUNG */, 1000, out ignored);
        }

        public static bool SendToRecycleBin(string path)
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = 0x0003, // FO_DELETE
                pFrom = path + "\0",
                // FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI
                fFlags = 0x0004 | 0x0010 | 0x0040 | 0x0400
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }

        // Windows 11 "Efficiency mode": lowest priority + EcoQoS, so the app barely touches the battery.
        public static void EnableEfficiencyMode()
        {
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.Idle;
                var state = new PowerThrottlingState { Version = 1, ControlMask = 1, StateMask = 1 };
                SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf(state));
            }
            catch (Exception) { }
        }

        public static void AttachParentConsole()
        {
            AttachConsole(-1);
        }
    }

    static class FileUtil
    {
        public static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception) { return true; } // unreadable: treat as unsafe
        }

        // Every real file under a folder. Never follows shortcuts, symlinks or junctions,
        // so a crafted archive can't point the app at files outside the download.
        public static IEnumerable<string> FilesUnder(string dir)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                string current = stack.Pop();
                string[] files, subdirs;
                try
                {
                    files = Directory.GetFiles(current);
                    subdirs = Directory.GetDirectories(current);
                }
                catch (Exception) { continue; }

                foreach (string f in files)
                    if (!IsLink(f)) yield return f;
                foreach (string d in subdirs)
                    if (!IsLink(d)) stack.Push(d);
            }
        }

        public static void DeleteTree(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (string f in FilesUnder(dir))
                    File.SetAttributes(f, FileAttributes.Normal);
                // Directory.Delete removes links themselves without entering them.
                Directory.Delete(dir, true);
            }
            catch (Exception ex) { Log.Write("Could not clean " + dir + ": " + ex.Message); }
        }
    }

    static class Log
    {
        static readonly object Gate = new object();
        public static string FilePath;

        public static void Write(string message)
        {
            lock (Gate)
            {
                try
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                }
                catch (Exception) { }
            }
        }
    }
}
