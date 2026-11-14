using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FontAutoInstaller
{
    sealed class TrayApp : ApplicationContext
    {
        const string AppName = "FontAutoInstaller";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string SettingsKey = @"Software\FontAutoInstaller";
        public const string ShowSignalName = @"Local\FontAutoInstaller.Show";

        readonly EventWaitHandle showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);

        readonly NotifyIcon tray;
        readonly DownloadWatcher watcher;
        readonly BlockingCollection<string> queue = new BlockingCollection<string>();
        readonly SynchronizationContext ui;
        readonly Icon activeIcon = TrayIcon.Draw(Color.FromArgb(37, 99, 235));
        readonly Icon pausedIcon = TrayIcon.Draw(Color.FromArgb(107, 114, 128));
        readonly ToolStripMenuItem pauseItem;
        readonly ToolStripMenuItem startupItem;
        FontToast card;

        public TrayApp(bool launchedAtStartup)
        {
            NativeMethods.EnableEfficiencyMode();
            ui = new WindowsFormsSynchronizationContext();
            FileUtil.DeleteTree(AppPaths.WorkDir);

            string downloads = NativeMethods.DownloadsFolder();

            pauseItem = new ToolStripMenuItem("Pause watching", null, delegate { TogglePause(); });
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { SetStartup(!startupItem.Checked); });

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Watching " + downloads) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Recently installed fonts", null, delegate { ShowHistory(); })
                { Font = new Font(menu.Font, FontStyle.Bold) });
            menu.Items.Add(pauseItem);
            menu.Items.Add("Open log", null, delegate { OpenPath(Log.FilePath); });
            menu.Items.Add("Open installed fonts folder", null, delegate { OpenPath(FontInstaller.UserFontDir); });
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { Quit(); });

            tray = new NotifyIcon { Icon = activeIcon, Text = "Font Auto Installer", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowHistory(); };

            ApplyStartupSetting();

            new Thread(WorkLoop) { IsBackground = true, Name = "FontJobs" }.Start();
            watcher = new DownloadWatcher(downloads, Enqueue);
            watcher.Start();
            watcher.CatchUp(TimeSpan.FromMinutes(15));
            new Thread(ShowLoop) { IsBackground = true, Name = "ShowSignal" }.Start();

            Log.Write("Started. Watching " + downloads);
            if (!launchedAtStartup)
                Notify("Font Auto Installer is on", "New fonts in Downloads will install automatically.");
        }

        void Enqueue(string path)
        {
            try { queue.Add(path); }
            catch (InvalidOperationException) { } // app is quitting
        }

        void WorkLoop()
        {
            foreach (string path in queue.GetConsumingEnumerable())
            {
                try
                {
                    JobReport report = FontJob.Run(path, false);
                    if (report.Found > 0) ShowCard(report.Title, report.Subtitle, report.Entries(), true);
                }
                catch (Exception ex)
                {
                    Log.Write("Error on " + path + ": " + ex);
                }
            }
        }

        // Sleeps (no CPU) until someone double-clicks the exe again.
        void ShowLoop()
        {
            while (showSignal.WaitOne())
                Notify("Font Auto Installer is already running",
                    (watcher.Paused ? "Paused. " : "Watching Downloads. ") +
                    "Find the blue Aa icon near the clock (click ^ if hidden).");
        }

        void Notify(string title, string body)
        {
            ShowCard(title, body, new FontEntry[0], true);
        }

        void ShowHistory()
        {
            List<FontEntry> recent = InstallHistory.Recent(50);
            ShowCard("Recently installed fonts",
                recent.Count == 0 ? "Nothing yet. Download a font and it shows up here." : "Newest first. Click a name to copy it.",
                recent, false);
        }

        // One card on screen at a time; a new one replaces the old.
        void ShowCard(string title, string subtitle, IList<FontEntry> entries, bool autoClose)
        {
            ui.Post(delegate
            {
                try
                {
                    if (card != null && !card.IsDisposed) card.Close();
                    card = new FontToast(title, subtitle, entries, autoClose);
                    card.Show();
                    if (!autoClose) card.Activate();
                }
                catch (Exception ex) { Log.Write("Popup failed: " + ex); }
            }, null);
        }

        void TogglePause()
        {
            watcher.Paused = !watcher.Paused;
            pauseItem.Text = watcher.Paused ? "Resume watching" : "Pause watching";
            tray.Icon = watcher.Paused ? pausedIcon : activeIcon;
            tray.Text = watcher.Paused ? "Font Auto Installer (paused)" : "Font Auto Installer";
            Log.Write(watcher.Paused ? "Paused." : "Resumed.");
        }

        void ApplyStartupSetting()
        {
            bool disabled;
            using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                disabled = key != null && Convert.ToInt32(key.GetValue("StartupDisabled", 0)) == 1;
            WriteRunEntry(!disabled); // also repairs the path if the folder moved
            startupItem.Checked = !disabled;
        }

        void SetStartup(bool on)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                key.SetValue("StartupDisabled", on ? 0 : 1, RegistryValueKind.DWord);
            WriteRunEntry(on);
            startupItem.Checked = on;
        }

        static void WriteRunEntry(bool on)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(AppName, "\"" + Application.ExecutablePath + "\" --startup");
                else key.DeleteValue(AppName, false);
            }
        }

        static void OpenPath(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) File.AppendAllText(path, "");
                Process.Start(path);
            }
            catch (Exception ex) { Log.Write("Could not open " + path + ": " + ex.Message); }
        }

        void Quit()
        {
            watcher.Dispose();
            queue.CompleteAdding();
            tray.Visible = false;
            tray.Dispose();
            Log.Write("Quit.");
            ExitThread();
        }
    }

    static class TrayIcon
    {
        // Draws a flat rounded square with "Aa", so no .ico file is needed.
        public static Icon Draw(Color fill)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var shape = RoundedRect(new Rectangle(1, 1, 30, 30), 8))
                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, shape);
                using (var font = new Font("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("Aa", font, Brushes.White, new RectangleF(0, 0, 32, 31), format);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
