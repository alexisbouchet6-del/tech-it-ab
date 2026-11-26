// PowerMenu - éteindre / redémarrer / mettre à jour / déconnecter proprement, en un clic
// Cible : .NET Framework 4.8 (inclus dans Windows 10 / 11)
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("PowerMenu")]
[assembly: AssemblyDescription("Éteindre, redémarrer, mettre à jour, déconnecter proprement")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("PowerMenu")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

namespace PowerMenu
{
    // ===================================================================== Programme
    static class Program
    {
        public const string AppName = "PowerMenu";
        public const string Version = "1.0.1";

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            AlexBHarden.Harden.Apply(null);
            bool created;
            using (var mutex = new Mutex(true, "Local\\PowerMenu_alexb", out created))
            {
                if (!created) return;   // déjà ouvert
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Logger.Write("ERREUR UI : " + e.Exception);
                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
        }
    }

    // ===================================================================== Journal + préférences
    static class Logger
    {
        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerMenu"); } }
        public static string FilePath { get { return Path.Combine(Dir, "powermenu.log"); } }

        public static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    static class Settings
    {
        static string FilePath { get { return Path.Combine(Logger.Dir, "config.ini"); } }
        public static bool Dark = true;
        public static int Countdown = 10;            // secondes avant l'action (annulable)
        public static DateTime? ScheduledAt = null;  // arrêt programmé via ce logiciel

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq < 1 || raw.StartsWith("#")) continue;
                    string k = raw.Substring(0, eq).Trim().ToLowerInvariant(), v = raw.Substring(eq + 1).Trim();
                    int n; long ticks;
                    if (k == "theme") Dark = v.ToLowerInvariant() != "light";
                    if (k == "countdown" && int.TryParse(v, out n) && n >= 0 && n <= 120) Countdown = n;
                    if (k == "scheduledat" && long.TryParse(v, out ticks) && ticks > DateTime.Now.Ticks) ScheduledAt = new DateTime(ticks);
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Logger.Dir);
                File.WriteAllText(FilePath,
                    "# PowerMenu - préférences (by alex.b)\r\nTheme=" + (Dark ? "dark" : "light") +
                    "\r\nCountdown=" + Countdown +
                    "\r\nScheduledAt=" + (ScheduledAt.HasValue ? ScheduledAt.Value.Ticks.ToString() : "") + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ===================================================================== Actions système
    enum PowerAction { Shutdown, Restart, UpdateShutdown, UpdateRestart, Logoff, Lock, Sleep, Bios, Schedule, CancelSchedule }

    static class Power
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct TokPriv1Luid { public int Count; public long Luid; public int Attr; }

        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, int acc, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string host, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr tok, bool disall, ref TokPriv1Luid newst, int len, IntPtr prev, IntPtr relen);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern uint InitiateShutdownW(string machine, string msg, uint grace, uint flags, uint reason);
        [DllImport("user32.dll", SetLastError = true)] static extern bool ExitWindowsEx(uint flags, uint reason);
        [DllImport("user32.dll", SetLastError = true)] static extern bool LockWorkStation();
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

        const uint SHUTDOWN_RESTART = 0x4, SHUTDOWN_POWEROFF = 0x8, SHUTDOWN_INSTALL_UPDATES = 0x40, SHUTDOWN_RESTARTAPPS = 0x80;
        const uint REASON_PLANNED = 0x80000000;

        static void EnableShutdownPrivilege()
        {
            IntPtr tok;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out tok)) return;   // ADJUST_PRIVILEGES | QUERY
            try
            {
                var tp = new TokPriv1Luid { Count = 1, Attr = 0x2 };   // SE_PRIVILEGE_ENABLED
                if (LookupPrivilegeValue(null, "SeShutdownPrivilege", out tp.Luid))
                    AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(tok); }
        }

        // Arrêt / redémarrage COMPLET (pas le démarrage rapide hybride), sans forcer :
        // les applications avec du travail non enregistré peuvent encore demander de sauvegarder.
        static void Initiate(uint flags)
        {
            EnableShutdownPrivilege();
            uint rc = InitiateShutdownW(null, null, 0, flags, REASON_PLANNED);
            if (rc != 0 && (flags & SHUTDOWN_INSTALL_UPDATES) != 0)
            {
                Logger.Write("InitiateShutdown avec mises à jour => " + rc + ", nouvel essai sans");
                rc = InitiateShutdownW(null, null, 0, flags & ~SHUTDOWN_INSTALL_UPDATES, REASON_PLANNED);
            }
            if (rc != 0) throw new Win32Exception((int)rc);
        }

        public static void Execute(PowerAction a)
        {
            Logger.Write("Action : " + a);
            switch (a)
            {
                case PowerAction.Shutdown: Initiate(SHUTDOWN_POWEROFF); break;
                case PowerAction.Restart: Initiate(SHUTDOWN_RESTART | SHUTDOWN_RESTARTAPPS); break;
                case PowerAction.UpdateShutdown: Initiate(SHUTDOWN_POWEROFF | SHUTDOWN_INSTALL_UPDATES); break;
                case PowerAction.UpdateRestart: Initiate(SHUTDOWN_RESTART | SHUTDOWN_INSTALL_UPDATES | SHUTDOWN_RESTARTAPPS); break;
                case PowerAction.Logoff:
                    if (!ExitWindowsEx(0x0 /* EWX_LOGOFF, sans forcer */, REASON_PLANNED)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;
                case PowerAction.Lock:
                    if (!LockWorkStation()) throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;
                case PowerAction.Sleep:
                    Application.SetSuspendState(PowerState.Suspend, false, false);
                    break;
                case PowerAction.Bios:
                    // nécessite les droits admin : Windows affiche l'invite UAC
                    Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /fw /t 0")
                    { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                    break;
            }
        }

        public static bool ScheduleShutdown(int minutes, out string err)
        {
            err = null;
            string o;
            int rc = Run("shutdown.exe", "/s /t " + (minutes * 60) + " /c \"PowerMenu (by alex.b) : arrêt programmé\"", out o);
            if (rc != 0) { err = o; return false; }
            Settings.ScheduledAt = DateTime.Now.AddMinutes(minutes);
            Settings.Save();
            Logger.Write("Arrêt programmé dans " + minutes + " min");
            return true;
        }

        public static bool CancelScheduled(out string msg)
        {
            string o;
            int rc = Run("shutdown.exe", "/a", out o);
            Settings.ScheduledAt = null; Settings.Save();
            Logger.Write("Annulation arrêt programmé => " + rc);
            msg = rc == 0 ? "Arrêt programmé annulé." : "Aucun arrêt programmé en cours.";
            return rc == 0;
        }

        static int Run(string exe, string args, out string output)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            try { psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { }
            using (var p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd(), se = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                output = (so + " " + se).Trim();
                return p.ExitCode;
            }
        }

        public static TimeSpan Uptime { get { try { return TimeSpan.FromMilliseconds(GetTickCount64()); } catch { return TimeSpan.Zero; } } }

        public static bool UpdatesPending
        {
            get
            {
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    {
                        foreach (var path in new[]
                        {
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"
                        })
                            using (var k = b.OpenSubKey(path)) if (k != null) return true;
                    }
                }
                catch { }
                return false;
            }
        }
    }

    // ===================================================================== Thème
    class Theme
    {
        public bool Dark;
        public Color Bg, Surface, Surface2, Text, Muted, Accent, Accent2, Border, Ok, Warn, Bad;
        public static Theme Current = Make(true);

        public static Theme Make(bool dark)
        {
            if (dark) return new Theme
            {
                Dark = true,
                Bg = Color.FromArgb(7, 10, 17), Surface = Color.FromArgb(13, 19, 30), Surface2 = Color.FromArgb(18, 27, 42),
                Text = Color.FromArgb(220, 236, 246), Muted = Color.FromArgb(105, 128, 150), Border = Color.FromArgb(32, 48, 70),
                Accent = Color.FromArgb(0, 229, 255), Accent2 = Color.FromArgb(255, 43, 214),
                Ok = Color.FromArgb(57, 255, 140), Warn = Color.FromArgb(255, 176, 0), Bad = Color.FromArgb(255, 56, 96)
            };
            return new Theme
            {
                Dark = false,
                Bg = Color.FromArgb(238, 242, 247), Surface = Color.White, Surface2 = Color.White,
                Text = Color.FromArgb(18, 26, 38), Muted = Color.FromArgb(96, 112, 130), Border = Color.FromArgb(206, 216, 228),
                Accent = Color.FromArgb(0, 132, 168), Accent2 = Color.FromArgb(186, 24, 148),
                Ok = Color.FromArgb(0, 146, 80), Warn = Color.FromArgb(206, 118, 0), Bad = Color.FromArgb(206, 28, 62)
            };
        }

        public static Color Blend(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        static float? k;
        public static float K
        {
            get
            {
                if (!k.HasValue) { try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) k = g.DpiX / 96f; } catch { k = 1f; } }
                return k.Value;
            }
        }
        public static int S(float px) { return (int)Math.Round(px * K); }

        // Icônes Segoe MDL2 (Windows 10/11) avec repli en caractères Unicode
        static bool? mdl2;
        public static bool HasMdl2
        {
            get
            {
                if (!mdl2.HasValue) mdl2 = FontFamily.Families.Any(f => f.Name == "Segoe MDL2 Assets");
                return mdl2.Value;
            }
        }
        public static Font IconFont(float size) { return HasMdl2 ? new Font("Segoe MDL2 Assets", size) : new Font("Segoe UI Symbol", size); }
        public static string Icon(string mdl2Code, string fallback) { return HasMdl2 ? mdl2Code : fallback; }
    }

    // ===================================================================== Tuile
    class Tile : Control
    {
        public string Glyph = "", Title = "", Hint = "";
        public Func<Theme, Color> AccentOf = t => t.Accent;
        public bool Badge;
        bool hover, pressed;

        public Tile()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand; TabStop = true; Margin = new Padding(Theme.S(5));
        }

        public void PerformClick() { OnClick(EventArgs.Empty); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.K; var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent != null ? Parent.BackColor : t.Bg);
            Color acc = AccentOf(t);
            var r = new RectangleF(2 * k, 2 * k, Width - 4 * k - 1, Height - 4 * k - 1);

            if (hover && t.Dark)
                using (var gp = Theme.Round(RectangleF.Inflate(r, 1.5f * k, 1.5f * k), 12 * k))
                using (var pen = new Pen(Color.FromArgb(80, acc), 4 * k)) g.DrawPath(pen, gp);

            using (var path = Theme.Round(r, 11 * k))
            {
                using (var b = new SolidBrush(Theme.Blend(t.Surface2, acc, pressed ? 0.30 : hover ? 0.15 : 0.0))) g.FillPath(b, path);
                var clip = g.Clip; g.SetClip(path);
                using (var bar = new SolidBrush(acc)) g.FillRectangle(bar, r.X, r.Bottom - 3 * k, r.Width, 3 * k);
                g.Clip = clip;
                using (var pen = new Pen(Color.FromArgb(hover || Focused ? 255 : 110, acc), (hover || Focused ? 1.8f : 1f) * k)) g.DrawPath(pen, path);
            }

            using (var f = Theme.IconFont(20f))
            using (var b = new SolidBrush(acc))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(Glyph, f, b, new RectangleF(r.X, r.Y + 10 * k, r.Width, r.Height * 0.45f), sf);

            using (var tf = new Font("Segoe UI Semibold", 9.5f))
            using (var hf = new Font("Consolas", 7.5f))
            using (var tb = new SolidBrush(t.Text))
            using (var hb = new SolidBrush(t.Muted))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter })
            {
                float y = r.Y + r.Height * 0.52f;
                g.DrawString(Title, tf, tb, new RectangleF(r.X + 4 * k, y, r.Width - 8 * k, r.Height * 0.30f), sf);
                if (Hint.Length > 0) g.DrawString(Hint, hf, hb, new RectangleF(r.X, r.Bottom - 22 * k, r.Width, 14 * k), sf);
            }

            if (Badge)
            {
                float d = 9 * k;
                using (var b = new SolidBrush(t.Warn)) g.FillEllipse(b, r.Right - d - 9 * k, r.Y + 9 * k, d, d);
            }
        }
    }

    // ===================================================================== Fenêtre
    class MainForm : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        readonly TableLayoutPanel grid;
        readonly Panel countdownPanel;
        readonly Label lblCount, lblAction, lblHint;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly Dictionary<Keys, Tile> shortcuts = new Dictionary<Keys, Tile>();
        Tile tTheme;
        PowerAction pending;
        int remaining;
        string status = "";

        public MainForm()
        {
            Settings.Load();
            Theme.Current = Theme.Make(Settings.Dark);

            Text = "PowerMenu · by alex.b";
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; KeyPreview = true; DoubleBuffered = true;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(Theme.S(560), Theme.S(560));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            grid = new TableLayoutPanel { ColumnCount = 3, RowCount = 4, Location = new Point(Theme.S(14), Theme.S(92)), Size = new Size(Theme.S(532), Theme.S(420)) };
            for (int i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            for (int i = 0; i < 4; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));
            Controls.Add(grid);

            bool upd = Power.UpdatesPending;
            AddTile(0, 0, Theme.Icon("", "⏻"), "Éteindre", "E", t => t.Bad, () => Start(PowerAction.Shutdown), Keys.E);
            AddTile(1, 0, Theme.Icon("", "↻"), "Redémarrer", "R", t => t.Warn, () => Start(PowerAction.Restart), Keys.R);
            AddTile(2, 0, Theme.Icon("", "☾"), "Veille", "V", t => t.Accent, () => Start(PowerAction.Sleep), Keys.V);
            AddTile(0, 1, Theme.Icon("", "⇩"), "MAJ + éteindre", "U", t => t.Bad, () => Start(PowerAction.UpdateShutdown), Keys.U).Badge = upd;
            AddTile(1, 1, Theme.Icon("", "⟳"), "MAJ + redémarrer", "M", t => t.Warn, () => Start(PowerAction.UpdateRestart), Keys.M).Badge = upd;
            AddTile(2, 1, Theme.Icon("", "🔒"), "Verrouiller", "L", t => t.Accent, () => Start(PowerAction.Lock), Keys.L);
            AddTile(0, 2, Theme.Icon("", "⇥"), "Se déconnecter", "D", t => t.Accent2, () => Start(PowerAction.Logoff), Keys.D);
            AddTile(1, 2, Theme.Icon("", "⚙"), "Redémarrer BIOS", "B", t => t.Accent2, () => Start(PowerAction.Bios), Keys.B);
            AddTile(2, 2, Theme.Icon("", "⏲"), "Programmer l'arrêt", "P", t => t.Ok, AskSchedule, Keys.P);
            AddTile(0, 3, Theme.Icon("", "✕"), "Annuler arrêt progr.", "A", t => t.Ok, CancelSchedule, Keys.A);
            tTheme = AddTile(1, 3, Theme.Icon("", "☀"), Theme.Current.Dark ? "Thème clair" : "Thème sombre", "T", t => t.Muted, ToggleTheme, Keys.T);
            AddTile(2, 3, Theme.Icon("", "✕"), "Fermer", "Échap", t => t.Muted, Close, Keys.None);

            // écran de compte à rebours
            countdownPanel = new Panel { Location = grid.Location, Size = grid.Size, Visible = false };
            lblAction = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Consolas", 14f, FontStyle.Bold), Dock = DockStyle.Top, Height = Theme.S(70) };
            lblCount = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Consolas", 64f, FontStyle.Bold), Dock = DockStyle.Top, Height = Theme.S(150) };
            lblHint = new Label { AutoSize = false, TextAlign = ContentAlignment.TopCenter, Font = new Font("Segoe UI", 9.5f), Dock = DockStyle.Top, Height = Theme.S(60) };
            var btns = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Bottom, Height = Theme.S(110) };
            btns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); btns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var bNow = new Tile { Glyph = Theme.Icon("", "▶"), Title = "Maintenant", Hint = "Entrée", AccentOf = t => t.Bad, Dock = DockStyle.Fill };
            var bCancel = new Tile { Glyph = Theme.Icon("", "✕"), Title = "Annuler", Hint = "Échap", AccentOf = t => t.Ok, Dock = DockStyle.Fill };
            bNow.Click += (s, e) => Fire();
            bCancel.Click += (s, e) => StopCountdown();
            btns.Controls.Add(bNow, 0, 0); btns.Controls.Add(bCancel, 1, 0);
            countdownPanel.Controls.Add(btns);
            countdownPanel.Controls.Add(lblHint); countdownPanel.Controls.Add(lblCount); countdownPanel.Controls.Add(lblAction);
            Controls.Add(countdownPanel);

            timer.Tick += (s, e) =>
            {
                remaining--;
                lblCount.Text = remaining.ToString();
                if (remaining <= 0) Fire();
            };

            KeyDown += (s, e) =>
            {
                if (countdownPanel.Visible)
                {
                    if (e.KeyCode == Keys.Escape) StopCountdown();
                    else if (e.KeyCode == Keys.Enter) Fire();
                    e.Handled = true; return;
                }
                if (e.KeyCode == Keys.Escape) { Close(); return; }
                Tile tile;
                if (shortcuts.TryGetValue(e.KeyCode, out tile)) { e.Handled = true; tile.Focus(); BeginInvoke(new Action(tile.PerformClick)); }
            };

            ApplyTheme();
            RefreshStatus();
        }

        Tile AddTile(int col, int row, string glyph, string title, string hint, Func<Theme, Color> acc, Action click, Keys key)
        {
            var t = new Tile { Glyph = glyph, Title = title, Hint = hint, AccentOf = acc, Dock = DockStyle.Fill };
            t.Click += (s, e) => click();
            grid.Controls.Add(t, col, row);
            if (key != Keys.None) shortcuts[key] = t;
            return t;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyDwm(); }

        void ApplyDwm()
        {
            try
            {
                int dark = Theme.Current.Dark ? 1 : 0; DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
                int border = ColorTranslator.ToWin32(Theme.Current.Accent); DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch { }
        }

        void ToggleTheme()
        {
            Settings.Dark = !Settings.Dark; Settings.Save();
            Theme.Current = Theme.Make(Settings.Dark);
            tTheme.Title = Theme.Current.Dark ? "Thème clair" : "Thème sombre";
            ApplyTheme();
        }

        void ApplyTheme()
        {
            var t = Theme.Current;
            BackColor = t.Bg; grid.BackColor = t.Bg; countdownPanel.BackColor = t.Bg;
            foreach (Control c in countdownPanel.Controls) c.BackColor = t.Bg;
            lblHint.ForeColor = t.Muted; lblCount.ForeColor = t.Bad;
            foreach (Control c in grid.Controls) c.Invalidate();
            foreach (Control c in countdownPanel.Controls[0].Controls) c.Invalidate();
            if (IsHandleCreated) ApplyDwm();
            Invalidate();
        }

        void RefreshStatus()
        {
            var up = Power.Uptime;
            string uptime = up.TotalDays >= 1 ? string.Format("{0} j {1} h", (int)up.TotalDays, up.Hours) : string.Format("{0} h {1:00} min", (int)up.TotalHours, up.Minutes);
            status = "Allumé depuis " + uptime + "   ·   MAJ en attente : " + (Power.UpdatesPending ? "OUI" : "non");
            if (Settings.ScheduledAt.HasValue && Settings.ScheduledAt.Value > DateTime.Now)
                status += "   ·   Arrêt programmé à " + Settings.ScheduledAt.Value.ToString("HH:mm");
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.K; var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(t.Bg);
            if (t.Dark) using (var p = new Pen(Color.FromArgb(12, t.Accent))) for (float x = 0; x < Width; x += 22 * k) g.DrawLine(p, x, 0, x, 76 * k);
            using (var f = new Font("Consolas", 16f, FontStyle.Bold)) using (var b = new SolidBrush(t.Accent))
                g.DrawString("> POWER_MENU_", f, b, 16 * k, 10 * k);
            using (var f = new Font("Consolas", 8.5f)) using (var b = new SolidBrush(t.Muted))
                g.DrawString(status, f, b, 19 * k, 44 * k);
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), 1), t.Accent, t.Accent2, 0f))
                g.FillRectangle(lg, 0, 76 * k, Width, 2 * k);
            using (var f = new Font("Consolas", 8f)) using (var b = new SolidBrush(t.Muted))
                g.DrawString("Développé by alex.b  ·  v" + Program.Version + "  ·  touches : lettre de la tuile, Échap pour fermer", f, b, 16 * k, ClientSize.Height - 26 * k);
        }

        // ------------------------------------------------------------- compte à rebours
        static string ActionLabel(PowerAction a)
        {
            switch (a)
            {
                case PowerAction.Shutdown: return "ARRÊT DU PC";
                case PowerAction.Restart: return "REDÉMARRAGE";
                case PowerAction.UpdateShutdown: return "MISES À JOUR + ARRÊT";
                case PowerAction.UpdateRestart: return "MISES À JOUR + REDÉMARRAGE";
                case PowerAction.Logoff: return "DÉCONNEXION";
                case PowerAction.Bios: return "REDÉMARRAGE DANS LE BIOS";
                default: return a.ToString().ToUpperInvariant();
            }
        }

        void Start(PowerAction a)
        {
            if (a == PowerAction.Lock || a == PowerAction.Sleep) { pending = a; Fire(); return; }   // immédiat, sans risque
            pending = a;
            remaining = Settings.Countdown;
            if (remaining <= 0) { Fire(); return; }
            lblAction.Text = ActionLabel(a) + " DANS";
            lblAction.ForeColor = a == PowerAction.Bios || a == PowerAction.Logoff ? Theme.Current.Accent2 : Theme.Current.Bad;
            lblCount.ForeColor = lblAction.ForeColor;
            lblCount.Text = remaining.ToString();
            lblHint.Text = "Enregistre ton travail. Les applications ouvertes pourront encore demander d'enregistrer." +
                           (a == PowerAction.UpdateShutdown || a == PowerAction.UpdateRestart ? (Power.UpdatesPending ? "\nMises à jour en attente : elles seront installées." : "\nAucune mise à jour en attente détectée : action normale.") : "");
            grid.Visible = false; countdownPanel.Visible = true;
            countdownPanel.Controls[0].Controls[1].Focus();
            timer.Start();
            Logger.Write("Compte à rebours : " + a);
        }

        void StopCountdown()
        {
            timer.Stop();
            countdownPanel.Visible = false; grid.Visible = true;
            Logger.Write("Annulé : " + pending);
        }

        void Fire()
        {
            timer.Stop();
            try
            {
                Power.Execute(pending);
                if (pending == PowerAction.Lock || pending == PowerAction.Sleep) { Close(); return; }
                lblAction.Text = ActionLabel(pending);
                lblCount.Text = "…";
                lblHint.Text = "En cours. Si une application bloque, Windows te demandera quoi faire.";
                if (pending == PowerAction.Bios) BeginInvoke(new Action(Close));
            }
            catch (Exception ex)
            {
                Logger.Write("ERREUR " + pending + " : " + ex.Message);
                StopCountdown();
                if (!(ex is Win32Exception && ((Win32Exception)ex).NativeErrorCode == 1223))   // 1223 = UAC refusée
                    MessageBox.Show(this, "Échec : " + ex.Message, "PowerMenu · by alex.b", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ------------------------------------------------------------- arrêt programmé
        void AskSchedule()
        {
            using (var f = new ScheduleDialog())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                string err;
                if (Power.ScheduleShutdown(f.Minutes, out err))
                    MessageBox.Show(this, "Arrêt programmé à " + DateTime.Now.AddMinutes(f.Minutes).ToString("HH:mm") + ".\nAnnulable avec la tuile « Annuler arrêt progr. » (touche A).",
                        "PowerMenu · by alex.b", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, "Impossible de programmer l'arrêt : " + err + "\n(un arrêt est peut-être déjà programmé : annule-le d'abord)",
                        "PowerMenu · by alex.b", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RefreshStatus();
        }

        void CancelSchedule()
        {
            string msg;
            Power.CancelScheduled(out msg);
            MessageBox.Show(this, msg, "PowerMenu · by alex.b", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshStatus();
        }
    }

    // ===================================================================== Choix du délai
    class ScheduleDialog : Form
    {
        public int Minutes;

        public ScheduleDialog()
        {
            var t = Theme.Current;
            Text = "Programmer l'arrêt · by alex.b";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; KeyPreview = true;
            BackColor = t.Bg; Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(Theme.S(440), Theme.S(250));

            var title = new Label { Text = "> ÉTEINDRE DANS…", ForeColor = t.Accent, Font = new Font("Consolas", 12f, FontStyle.Bold), AutoSize = true, Location = new Point(Theme.S(16), Theme.S(14)) };
            Controls.Add(title);

            var grid = new TableLayoutPanel { ColumnCount = 3, RowCount = 2, Location = new Point(Theme.S(10), Theme.S(50)), Size = new Size(Theme.S(420), Theme.S(140)), BackColor = t.Bg };
            for (int i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            for (int i = 0; i < 2; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            int[] mins = { 15, 30, 60, 90, 120, 180 };
            string[] labels = { "15 min", "30 min", "1 h", "1 h 30", "2 h", "3 h" };
            for (int i = 0; i < mins.Length; i++)
            {
                int m = mins[i];
                var tile = new Tile { Glyph = "", Title = labels[i], Hint = "à " + DateTime.Now.AddMinutes(m).ToString("HH:mm"), AccentOf = x => x.Ok, Dock = DockStyle.Fill };
                tile.Click += (s, e) => { Minutes = m; DialogResult = DialogResult.OK; Close(); };
                grid.Controls.Add(tile, i % 3, i / 3);
            }
            Controls.Add(grid);

            var custom = new Label { Text = "Autre durée (minutes) :", ForeColor = t.Text, AutoSize = true, Location = new Point(Theme.S(16), Theme.S(208)) };
            var box = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = 45, Location = new Point(Theme.S(190), Theme.S(204)), Width = Theme.S(80), BackColor = t.Surface2, ForeColor = t.Text };
            var ok = new Button { Text = "OK", Location = new Point(Theme.S(285), Theme.S(202)), Size = new Size(Theme.S(70), Theme.S(30)), FlatStyle = FlatStyle.Flat, ForeColor = t.Ok, BackColor = t.Surface2 };
            ok.FlatAppearance.BorderColor = t.Ok;
            ok.Click += (s, e) => { Minutes = (int)box.Value; DialogResult = DialogResult.OK; Close(); };
            Controls.Add(custom); Controls.Add(box); Controls.Add(ok);
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }
    }
}
