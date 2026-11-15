using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace FontAutoInstaller
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            AppPaths.Init();

            // Dry run: "FontAutoInstaller.exe --scan <path>" prints what would be installed.
            if (args.Length == 2 && args[0] == "--scan")
                return ScanReport.Run(args[1]);

            bool firstInstance;
            using (new Mutex(true, @"Local\FontAutoInstaller", out firstInstance))
            {
                if (!firstInstance)
                {
                    // Already running: ask the running copy to show itself, so a double-click never looks dead.
                    EventWaitHandle signal;
                    if (EventWaitHandle.TryOpenExisting(TrayApp.ShowSignalName, out signal))
                        using (signal) signal.Set();
                    return 0;
                }
                NativeMethods.EnableDpiAwareness();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(args.Contains("--startup")));
            }
            return 0;
        }
    }

    static class AppPaths
    {
        public static string AppDir;
        public static string WorkDir;

        // Log + unzip workspace live next to the exe. If that folder is read-only
        // (for example Program Files), fall back to %LOCALAPPDATA%\FontAutoInstaller.
        public static void Init()
        {
            string dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
            if (!CanWrite(dir))
            {
                dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FontAutoInstaller");
                System.IO.Directory.CreateDirectory(dir);
            }
            AppDir = dir;
            WorkDir = System.IO.Path.Combine(dir, "work");
            Log.FilePath = System.IO.Path.Combine(dir, "log.txt");
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = System.IO.Path.Combine(dir, ".write-test");
                System.IO.File.WriteAllText(probe, "");
                System.IO.File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
