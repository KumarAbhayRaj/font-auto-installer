using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FontAutoInstaller
{
    // Event driven: Windows wakes us only when something lands in Downloads.
    // A short timer runs only while an item is still downloading/copying, then stops.
    sealed class DownloadWatcher : IDisposable
    {
        static readonly HashSet<string> TempExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload" };

        const int TickMs = 2000;
        static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);
        static readonly TimeSpan GiveUp = TimeSpan.FromHours(2);

        sealed class Pending
        {
            public long Signature = long.MinValue;
            public DateTime Changed;
            public DateTime First;
        }

        readonly string folder;
        readonly Action<string> onReady;
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> done = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly object gate = new object();
        readonly Timer timer;
        FileSystemWatcher fsw;
        bool timerArmed;
        bool disposed;

        public volatile bool Paused;

        public DownloadWatcher(string folder, Action<string> onReady)
        {
            this.folder = folder;
            this.onReady = onReady;
            timer = new Timer(delegate { Tick(); }, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            lock (gate)
            {
                if (disposed) return;
                if (fsw != null) fsw.Dispose();
                fsw = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024
                };
                fsw.Created += OnEvent;
                fsw.Changed += OnEvent;
                fsw.Renamed += OnEvent;
                fsw.Error += OnError;
                fsw.EnableRaisingEvents = true;
            }
        }

        // Picks up downloads that finished while the app was starting (for example right after login).
        public void CatchUp(TimeSpan window)
        {
            try
            {
                DateTime since = DateTime.UtcNow - window;
                foreach (string path in Directory.EnumerateFileSystemEntries(folder))
                    if (File.GetCreationTimeUtc(path) > since || File.GetLastWriteTimeUtc(path) > since)
                        Touch(path);
            }
            catch (Exception ex) { Log.Write("Catch-up scan failed: " + ex.Message); }
        }

        void OnEvent(object sender, FileSystemEventArgs e)
        {
            // A freshly created/renamed item is new, even if an identical file was handled here before.
            if (e.ChangeType != WatcherChangeTypes.Changed)
                lock (gate) done.Remove(e.FullPath);
            Touch(e.FullPath);
        }

        void OnError(object sender, ErrorEventArgs e)
        {
            Log.Write("Watcher error: " + e.GetException().Message + ". Restarting.");
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (!disposed)
                {
                    try { Start(); Log.Write("Watcher restarted."); return; }
                    catch (Exception ex) { Log.Write("Restart failed: " + ex.Message); Thread.Sleep(30000); }
                }
            });
        }

        void Touch(string path)
        {
            if (Paused || TempExt.Contains(Path.GetExtension(path))) return;
            if (!Directory.Exists(path) && !PackageScanner.IsPackageFile(path)) return;
            if (FileUtil.IsLink(path)) return; // shortcuts/links in Downloads are ignored

            lock (gate)
            {
                if (disposed) return;
                Pending p;
                if (!pending.TryGetValue(path, out p))
                {
                    p = new Pending { First = DateTime.UtcNow, Changed = DateTime.UtcNow };
                    pending[path] = p;
                }
                if (!timerArmed)
                {
                    timerArmed = true;
                    timer.Change(TickMs, Timeout.Infinite);
                }
            }
        }

        void Tick()
        {
            var ready = new List<string>();
            lock (gate)
            {
                if (disposed) return;
                DateTime now = DateTime.UtcNow;
                foreach (var entry in pending.ToList())
                {
                    string path = entry.Key;
                    Pending p = entry.Value;
                    long sig = Signature(path);

                    if (sig == long.MinValue || now - p.First > GiveUp) { pending.Remove(path); continue; }
                    if (sig != p.Signature) { p.Signature = sig; p.Changed = now; continue; }
                    if (now - p.Changed < Quiet || !IsUnlocked(path)) continue;

                    pending.Remove(path);
                    long previous;
                    if (done.TryGetValue(path, out previous) && previous == sig) continue; // same item, already handled
                    done[path] = sig;
                    ready.Add(path);
                }

                timerArmed = pending.Count > 0;
                if (timerArmed) timer.Change(TickMs, Timeout.Infinite);
            }
            foreach (string path in ready) onReady(path);
        }

        // Changes while a file is still being written or a folder is still being copied.
        static long Signature(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    return fi.Length * 31 + fi.LastWriteTimeUtc.Ticks;
                }
                if (Directory.Exists(path))
                {
                    long sig = 17;
                    foreach (string file in FileUtil.FilesUnder(path))
                    {
                        var fi = new FileInfo(file);
                        sig = sig * 31 + fi.Length + fi.LastWriteTimeUtc.Ticks;
                    }
                    return sig;
                }
                return long.MinValue; // gone
            }
            catch (IOException) { return DateTime.UtcNow.Ticks; }               // busy: treat as still changing
            catch (UnauthorizedAccessException) { return DateTime.UtcNow.Ticks; }
        }

        static bool IsUnlocked(string path)
        {
            if (!File.Exists(path)) return true;
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                if (fsw != null) fsw.Dispose();
                timer.Dispose();
            }
        }
    }
}
