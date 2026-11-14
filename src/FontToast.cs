using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FontAutoInstaller
{
    sealed class FontEntry
    {
        public readonly string Family;
        public readonly string Detail;
        public readonly string FilePath; // installed file, used to draw the name in its own font
        public readonly bool IsNew;

        public FontEntry(string family, string detail, string filePath, bool isNew)
        {
            Family = family;
            Detail = detail;
            FilePath = filePath;
            IsNew = isNew;
        }
    }

    sealed class Palette
    {
        public Color Back, Text, Muted, Border, Hover, Accent;

        public static Palette FromSystem()
        {
            bool light = true;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    light = key == null || Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) != 0;
            }
            catch (Exception) { }

            return light
                ? new Palette { Back = Color.White, Text = Color.FromArgb(17, 17, 17), Muted = Color.FromArgb(107, 107, 107),
                                Border = Color.FromArgb(222, 222, 222), Hover = Color.FromArgb(243, 244, 246), Accent = Color.FromArgb(37, 99, 235) }
                : new Palette { Back = Color.FromArgb(32, 32, 32), Text = Color.FromArgb(243, 243, 243), Muted = Color.FromArgb(160, 160, 160),
                                Border = Color.FromArgb(64, 64, 64), Hover = Color.FromArgb(48, 48, 48), Accent = Color.FromArgb(96, 165, 250) };
        }
    }

    // Bottom-right card. Popup mode fades out after 5 s (hover pauses it). List mode stays until closed.
    // Each font name is drawn in that font; clicking a name copies it.
    sealed class FontToast : Form
    {
        const int CardWidth = 360, Pad = 16, RowHeight = 56, PopupRows = 5, ListRows = 8;
        const int FadeAfterMs = 5000, TickMs = 40;

        readonly bool autoClose;
        readonly Palette colors = Palette.FromSystem();
        readonly PrivateFontCollection previews = new PrivateFontCollection();
        readonly List<Font> ownedFonts = new List<Font>();
        readonly Timer timer = new Timer { Interval = TickMs };
        readonly float scale;
        int elapsed;

        public FontToast(string title, string subtitle, IList<FontEntry> entries, bool autoClose)
        {
            this.autoClose = autoClose;
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = colors.Back;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };

            int width = S(CardWidth), inner = width - S(Pad) * 2;
            int y = S(Pad);

            var titleLabel = MakeLabel(title, UiFont(10.5f, FontStyle.Bold), colors.Text);
            titleLabel.SetBounds(S(Pad), y, inner - S(28), S(22));
            Controls.Add(titleLabel);
            Controls.Add(MakeCloseButton(width));
            y += S(24);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var sub = MakeLabel(subtitle, UiFont(8.5f), colors.Muted);
                sub.AutoSize = true;
                sub.MaximumSize = new Size(inner, 0);
                sub.Location = new Point(S(Pad), y);
                Controls.Add(sub);
                y += sub.PreferredHeight + S(10);
            }

            if (entries.Count > 0)
            {
                int maxRows = autoClose ? PopupRows : ListRows;
                List<FontEntry> shown = autoClose ? entries.Take(PopupRows).ToList() : entries.ToList();
                bool scrolls = shown.Count > maxRows;
                var list = new Panel { AutoScroll = scrolls, BackColor = colors.Back };
                list.SetBounds(S(Pad) - S(8), y, inner + S(16), S(RowHeight) * Math.Min(shown.Count, maxRows));
                int rowWidth = list.Width - (scrolls ? SystemInformation.VerticalScrollBarWidth : 0);
                for (int i = 0; i < shown.Count; i++)
                    list.Controls.Add(MakeRow(shown[i], rowWidth, i * S(RowHeight)));
                Controls.Add(list);
                y += list.Height + S(8);

                string hint = "Click a name to copy it";
                if (entries.Count > shown.Count)
                    hint = "+" + (entries.Count - shown.Count) + " more in tray menu > Recently installed fonts. " + hint + ".";
                var footer = MakeLabel(hint, UiFont(8f), colors.Muted);
                footer.AutoSize = true;
                footer.MaximumSize = new Size(inner, 0);
                footer.Location = new Point(S(Pad), y);
                Controls.Add(footer);
                y += footer.PreferredHeight;
            }

            ClientSize = new Size(width, y + S(Pad));
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - S(12), area.Bottom - Height - S(12));

            if (autoClose)
            {
                timer.Tick += OnTick;
                timer.Start();
            }
            else
            {
                Deactivate += delegate { Close(); };
            }
        }

        // Popups must not steal focus from whatever you're working in.
        protected override bool ShowWithoutActivation { get { return autoClose; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: no taskbar button, no Alt+Tab entry
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.StyleWindow(Handle, colors.Border); // Windows 11 rounded corners + 1px border
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            foreach (Font f in ownedFonts) f.Dispose();
            previews.Dispose();
            base.OnFormClosed(e);
        }

        void OnTick(object sender, EventArgs e)
        {
            if (Bounds.Contains(Cursor.Position)) { elapsed = 0; Opacity = 1; return; }
            elapsed += TickMs;
            if (elapsed < FadeAfterMs) return;
            Opacity -= 0.08;
            if (Opacity <= 0.05) Close();
        }

        Control MakeRow(FontEntry entry, int width, int top)
        {
            var row = new Panel { BackColor = colors.Back, Cursor = Cursors.Hand };
            row.SetBounds(0, top, width, S(RowHeight));

            var name = MakeLabel(entry.Family, PreviewFont(entry, 15f), colors.Text);
            name.UseCompatibleTextRendering = true; // GDI+ path, needed to draw fonts loaded from a file
            name.AutoEllipsis = true;
            name.SetBounds(S(8), S(5), width - S(16), S(30));

            var detail = MakeLabel(entry.Detail, UiFont(8f), colors.Muted);
            detail.AutoEllipsis = true;
            detail.SetBounds(S(8), S(35), width - S(16), S(16));

            row.Controls.Add(name);
            row.Controls.Add(detail);

            EventHandler enter = delegate { row.BackColor = colors.Hover; };
            EventHandler leave = delegate
            {
                if (!row.ClientRectangle.Contains(row.PointToClient(Cursor.Position))) row.BackColor = colors.Back;
            };
            EventHandler copy = delegate
            {
                try
                {
                    Clipboard.SetText(entry.Family);
                    detail.Text = "Copied \"" + entry.Family + "\"";
                    detail.ForeColor = colors.Accent;
                }
                catch (Exception ex) { Log.Write("Clipboard failed: " + ex.Message); }
            };
            foreach (Control c in new Control[] { row, name, detail })
            {
                c.MouseEnter += enter;
                c.MouseLeave += leave;
                c.Click += copy;
            }
            return row;
        }

        Control MakeCloseButton(int width)
        {
            var close = MakeLabel("✕", UiFont(10f), colors.Muted);
            close.TextAlign = ContentAlignment.MiddleCenter;
            close.Cursor = Cursors.Hand;
            close.SetBounds(width - S(Pad) - S(24), S(Pad) - S(3), S(26), S(26));
            close.MouseEnter += delegate { close.BackColor = colors.Hover; close.ForeColor = colors.Text; };
            close.MouseLeave += delegate { close.BackColor = colors.Back; close.ForeColor = colors.Muted; };
            close.Click += delegate { Close(); };
            return close;
        }

        Label MakeLabel(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font, ForeColor = color, BackColor = Color.Transparent, UseMnemonic = false };
        }

        Font UiFont(float pt, FontStyle style = FontStyle.Regular)
        {
            return Own(new Font("Segoe UI", pt, style));
        }

        // Load the installed file privately so the name renders in its own typeface.
        // GDI+ can't draw PostScript-flavoured OTF; those fall back to the installed family, then Segoe UI.
        Font PreviewFont(FontEntry entry, float pt)
        {
            try
            {
                if (!string.IsNullOrEmpty(entry.FilePath) && File.Exists(entry.FilePath))
                {
                    var before = new HashSet<string>(previews.Families.Select(f => f.Name));
                    previews.AddFontFile(entry.FilePath);
                    FontFamily family =
                        previews.Families.FirstOrDefault(f => f.Name.Equals(entry.Family, StringComparison.OrdinalIgnoreCase)) ??
                        previews.Families.FirstOrDefault(f => f.Name.StartsWith(entry.Family, StringComparison.OrdinalIgnoreCase)) ??
                        previews.Families.FirstOrDefault(f => !before.Contains(f.Name));
                    if (family != null)
                        foreach (FontStyle style in new[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic })
                            if (family.IsStyleAvailable(style)) return Own(new Font(family, pt, style));
                }
            }
            catch (Exception) { }

            try
            {
                var installed = new Font(entry.Family, pt);
                if (installed.Name.Equals(entry.Family, StringComparison.OrdinalIgnoreCase)) return Own(installed);
                installed.Dispose();
            }
            catch (Exception) { }

            return UiFont(pt * 0.85f);
        }

        Font Own(Font f)
        {
            ownedFonts.Add(f);
            return f;
        }

        int S(int px) { return (int)Math.Round(px * scale); }
    }
}
