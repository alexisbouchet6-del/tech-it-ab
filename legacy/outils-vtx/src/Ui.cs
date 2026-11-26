// Outils VTx - interface (thème cyber sombre / clair, zone de notification)
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OutilsVTx
{
    // ===================================================================== Programme
    static class Program
    {
        public const string AppName = "Outils VTx";
        public const string Version = "1.1.2";
        public const string ShowEventName = "Global\\OutilsVTx_show_alexb";

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            AlexBHarden.Harden.Apply(Logger.Dir);
            string a0 = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (a0 == "--autostart-on") return AutoStart.Enable() ? 0 : 1;
            if (a0 == "--autostart-off") { AutoStart.Disable(); return 0; }
            bool startInTray = a0 == "--tray";

            if (!IsAdmin())
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath)
                    { UseShellExecute = true, Verb = "runas", Arguments = startInTray ? "--tray" : "" });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    MessageBox.Show("Outils VTx a besoin des droits administrateur (bcdedit, registre, fonctionnalités Windows).",
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return 0;
            }

            bool created;
            using (var mutex = new Mutex(true, "Global\\OutilsVTx_alexb", out created))
            {
                if (!created)
                {
                    // déjà lancé : on demande à l'instance existante d'afficher sa fenêtre
                    try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch { }
                    return 0;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Logger.Write("ERREUR UI : " + e.Exception);
                Application.Run(new TrayApp(startInTray));
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        public static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    // ===================================================================== Préférences
    static class Settings
    {
        static string FilePath { get { return Path.Combine(Logger.Dir, "config.ini"); } }

        public static bool DarkTheme = true;
        public static bool TrayTipShown = false;

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq < 1 || raw.StartsWith("#")) continue;
                    string k = raw.Substring(0, eq).Trim().ToLowerInvariant(), v = raw.Substring(eq + 1).Trim().ToLowerInvariant();
                    if (k == "theme") DarkTheme = v != "light";
                    if (k == "traytipshown") TrayTipShown = v == "1";
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
                    "# Outils VTx - préférences (by alex.b)\r\nTheme=" + (DarkTheme ? "dark" : "light") +
                    "\r\nTrayTipShown=" + (TrayTipShown ? "1" : "0") + "\r\n", new UTF8Encoding(false));
            }
            catch { }
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
                Bg = Color.FromArgb(238, 242, 247), Surface = Color.White, Surface2 = Color.FromArgb(246, 248, 251),
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
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static float Dpi
        {
            get
            {
                try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; } catch { return 1f; }
            }
        }
    }

    // ===================================================================== Bouton moderne
    class CyberButton : Control
    {
        public string Glyph = "", Title = "", Subtitle = "";
        public Func<Theme, Color> AccentOf = t => t.Accent;
        public bool Recommended;
        bool hover, pressed;

        public CyberButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            Margin = new Padding(6);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            float k = Theme.Dpi;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent != null ? Parent.BackColor : t.Bg);

            Color acc = Enabled ? AccentOf(t) : t.Muted;
            var r = new RectangleF(3 * k, 3 * k, Width - 6 * k - 1, Height - 6 * k - 1);
            float rad = 9 * k;

            // halo néon au survol / recommandé
            if (Enabled && (hover || Recommended) && t.Dark)
                using (var glow = Theme.Round(new RectangleF(r.X - 2 * k, r.Y - 2 * k, r.Width + 4 * k, r.Height + 4 * k), rad + 2 * k))
                using (var pg = new Pen(Color.FromArgb(hover ? 90 : 55, acc), 4 * k))
                    g.DrawPath(pg, glow);

            using (var path = Theme.Round(r, rad))
            {
                double mix = !Enabled ? 0 : pressed ? 0.30 : hover ? 0.16 : Recommended ? 0.08 : 0.0;
                Color fill = Theme.Blend(t.Surface2, acc, mix);
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);

                // barre d'accent à gauche
                var oldClip = g.Clip;
                g.SetClip(path);
                using (var bar = new SolidBrush(acc)) g.FillRectangle(bar, r.X, r.Y, 4 * k, r.Height);
                g.Clip = oldClip;

                float bw = (hover || Focused || Recommended) && Enabled ? 1.8f * k : 1f * k;
                Color bc = Enabled ? Color.FromArgb(hover || Recommended ? 255 : 150, acc) : t.Border;
                using (var pen = new Pen(bc, bw)) g.DrawPath(pen, path);
            }

            // pictogramme
            float x = r.X + 16 * k;
            if (Glyph.Length > 0)
            {
                using (var gf = new Font("Segoe UI Symbol", 15f, FontStyle.Regular))
                using (var gb = new SolidBrush(acc))
                {
                    var gs = g.MeasureString(Glyph, gf);
                    g.DrawString(Glyph, gf, gb, x, r.Y + (r.Height - gs.Height) / 2);
                    x += 34 * k;
                }
            }

            // textes
            using (var tf = new Font("Segoe UI Semibold", 10f))
            using (var sf = new Font("Segoe UI", 8.5f))
            using (var tb = new SolidBrush(Enabled ? t.Text : t.Muted))
            using (var sb = new SolidBrush(t.Muted))
            {
                var th = g.MeasureString(Title, tf);
                bool hasSub = Subtitle.Length > 0;
                float total = th.Height + (hasSub ? g.MeasureString(Subtitle, sf).Height : 0);
                float y = r.Y + (r.Height - total) / 2;
                var area = new RectangleF(x, y, r.Right - x - 8 * k, th.Height);
                g.DrawString(Title, tf, tb, area, new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                if (hasSub)
                    g.DrawString(Subtitle, sf, sb, new RectangleF(x, y + th.Height, r.Right - x - 8 * k, r.Bottom - y - th.Height),
                        new StringFormat { Trimming = StringTrimming.EllipsisCharacter });
            }

            // étiquette « RECOMMANDÉ »
            if (Recommended && Enabled)
            {
                using (var f = new Font("Consolas", 7f, FontStyle.Bold))
                {
                    string tag = "RECOMMANDÉ";
                    var ts = g.MeasureString(tag, f);
                    var tr = new RectangleF(r.Right - ts.Width - 14 * k, r.Y + 6 * k, ts.Width + 8 * k, ts.Height + 2 * k);
                    using (var p = Theme.Round(tr, 4 * k))
                    using (var b = new SolidBrush(acc)) g.FillPath(b, p);
                    using (var tbr = new SolidBrush(t.Dark ? t.Bg : Color.White))
                        g.DrawString(tag, f, tbr, tr.X + 4 * k, tr.Y + 1 * k);
                }
            }
        }
    }

    // ===================================================================== En-tête
    class HeaderBar : Control
    {
        bool cursorOn = true;
        readonly System.Windows.Forms.Timer blink = new System.Windows.Forms.Timer { Interval = 550 };

        public HeaderBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            blink.Tick += (s, e) => { cursorOn = !cursorOn; Invalidate(new Rectangle(0, 0, Width / 2, Height)); };
            blink.Start();
        }

        protected override void Dispose(bool disposing) { if (disposing) blink.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.Dpi; var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(t.Bg);

            // grille discrète
            if (t.Dark)
                using (var p = new Pen(Color.FromArgb(14, t.Accent)))
                    for (float x = 0; x < Width; x += 22 * k) g.DrawLine(p, x, 0, x, Height);

            using (var f = new Font("Consolas", 17f, FontStyle.Bold))
            using (var b = new SolidBrush(t.Accent))
            {
                string title = "> OUTILS_VTX" + (cursorOn ? "_" : " ");
                g.DrawString(title, f, b, 16 * k, 10 * k);
            }
            using (var f = new Font("Consolas", 8.5f))
            using (var b = new SolidBrush(t.Muted))
                g.DrawString("VMware Workstation ⇄ Hyper-V / VBS  //  by alex.b  //  v" + Program.Version, f, b, 19 * k, 44 * k);

            // ligne néon dégradée
            var line = new RectangleF(0, Height - 2 * k, Width, 2 * k);
            if (line.Width > 1)
                using (var lg = new LinearGradientBrush(new RectangleF(0, 0, Width, 1), t.Accent, t.Accent2, 0f))
                    g.FillRectangle(lg, line);
        }
    }

    // ===================================================================== Bandeau d'état
    class StatusBanner : Control
    {
        public string Title = "ANALYSE EN COURS…", Sub = "";
        public Func<Theme, Color> ColorOf = t => t.Muted;

        public StatusBanner()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.Dpi; var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(t.Bg);
            Color c = ColorOf(t);

            var r = new RectangleF(14 * k, 10 * k, Width - 28 * k, Height - 16 * k);
            using (var path = Theme.Round(r, 10 * k))
            {
                using (var b = new SolidBrush(Theme.Blend(t.Surface, c, t.Dark ? 0.10 : 0.07))) g.FillPath(b, path);
                if (t.Dark)
                {
                    var clip = g.Clip; g.SetClip(path);
                    using (var p = new Pen(Color.FromArgb(10, c)))
                        for (float y = r.Y; y < r.Bottom; y += 3 * k) g.DrawLine(p, r.X, y, r.Right, y);   // lignes de balayage
                    g.Clip = clip;
                }
                using (var p = new Pen(Color.FromArgb(200, c), 1.4f * k)) g.DrawPath(p, path);
            }

            // coins « HUD »
            using (var p = new Pen(c, 2.4f * k))
            {
                float L = 14 * k;
                g.DrawLines(p, new[] { new PointF(r.X - 3 * k, r.Y + L), new PointF(r.X - 3 * k, r.Y - 3 * k), new PointF(r.X + L, r.Y - 3 * k) });
                g.DrawLines(p, new[] { new PointF(r.Right + 3 * k, r.Bottom - L), new PointF(r.Right + 3 * k, r.Bottom + 3 * k), new PointF(r.Right - L, r.Bottom + 3 * k) });
            }

            // pastille
            float d = 12 * k;
            var dot = new RectangleF(r.X + 18 * k, r.Y + (r.Height - d) / 2 - (Sub.Length > 0 ? 8 * k : 0), d, d);
            if (t.Dark) using (var gb = new SolidBrush(Color.FromArgb(70, c))) g.FillEllipse(gb, RectangleF.Inflate(dot, 5 * k, 5 * k));
            using (var b = new SolidBrush(c)) g.FillEllipse(b, dot);

            using (var f = new Font("Consolas", 14f, FontStyle.Bold))
            using (var b = new SolidBrush(c))
            {
                var sz = g.MeasureString(Title, f);
                float y = Sub.Length > 0 ? r.Y + 10 * k : r.Y + (r.Height - sz.Height) / 2;
                g.DrawString(Title, f, b, r.X + 42 * k, y);
                if (Sub.Length > 0)
                    using (var sf = new Font("Segoe UI", 9f))
                    using (var sb = new SolidBrush(t.Warn))
                        g.DrawString(Sub, sf, sb, r.X + 44 * k, y + sz.Height + 1 * k);
            }
        }
    }

    // ===================================================================== Menu contextuel aux couleurs du thème
    class CyberRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Current.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Current.Accent)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Current.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var t = Theme.Current;
            var c = e.Item.Selected && e.Item.Enabled ? Theme.Blend(t.Surface, t.Accent, 0.22) : t.Surface;
            using (var b = new SolidBrush(c)) e.Graphics.FillRectangle(b, 2, 0, e.Item.Width - 4, e.Item.Height);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Current.Text : Theme.Current.Muted;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            using (var f = new Font("Segoe UI Symbol", 9f))
            using (var b = new SolidBrush(Theme.Current.Accent))
                e.Graphics.DrawString("✓", f, b, e.ImageRectangle.X, e.ImageRectangle.Y - 1);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Current.Accent;
            base.OnRenderArrow(e);
        }
    }

    // ===================================================================== Démarrage auto (tâche planifiée, sans UAC)
    static class AutoStart
    {
        const string TaskName = "OutilsVTx";
        static string Schtasks { get { return Sys.Exe("schtasks.exe"); } }

        public static bool IsEnabled()
        {
            string o;
            try { return Sys.Run(Schtasks, "/query /tn \"" + TaskName + "\"", out o, 10000) == 0; } catch { return false; }
        }

        public static bool Enable()
        {
            // tâche élevée sans invite : uniquement pour l'exe installé (un exe dans un dossier modifiable pourrait être remplacé)
            if (!AlexBHarden.Harden.InProtectedDir(Application.ExecutablePath)) { Logger.Write("AutoStart refusé : exe hors de Program Files (" + Application.ExecutablePath + ")"); return false; }
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Author>alex.b</Author><Description>Lance Outils VTx dans la zone de notification (by alex.b).</Description></RegistrationInfo>\r\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled><Delay>PT5S</Delay></LogonTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><GroupId>S-1-5-32-545</GroupId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
"    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
"    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"    <Hidden>false</Hidden>\r\n" +
"    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
"    <WakeToRun>false</WakeToRun>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Priority>7</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>\"" + SecurityElement.Escape(Application.ExecutablePath) + "\"</Command><Arguments>--tray</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
            string tdir = AlexBHarden.Harden.SecureTempDir("outilsvtx"); string tmp = Path.Combine(tdir, "outilsvtx-task.xml");
            try
            {
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                string o;
                return Sys.Run(Schtasks, "/create /tn \"" + TaskName + "\" /xml \"" + tmp + "\" /f", out o, 15000) == 0;
            }
            catch (Exception ex) { Logger.Write("AutoStart ON erreur : " + ex.Message); return false; }
            finally { try { Directory.Delete(tdir, true); } catch { } }
        }

        public static void Disable()
        {
            string o;
            try { Sys.Run(Schtasks, "/delete /tn \"" + TaskName + "\" /f", out o, 10000); } catch { }
        }
    }

    // ===================================================================== Épinglage de l'icône (Windows 11)
    static class TrayPin
    {
        // Windows 11 mémorise chaque icône dans HKCU\Control Panel\NotifyIconSettings\<id>.
        // IsPromoted = 1 => icône toujours visible à côté de l'horloge (pas cachée derrière ^).
        public static void Promote()
        {
            try
            {
                string exeName = Path.GetFileName(Application.ExecutablePath);
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return;
                    foreach (var name in root.GetSubKeyNames())
                    {
                        using (var k = root.OpenSubKey(name, true))
                        {
                            if (k == null) continue;
                            var path = k.GetValue("ExecutablePath") as string;
                            if (path == null || !path.EndsWith("\\" + exeName, StringComparison.OrdinalIgnoreCase)) continue;
                            var cur = k.GetValue("IsPromoted");
                            if (cur == null || Convert.ToInt32(cur) != 1)
                            {
                                k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                Logger.Write("Icône épinglée dans la zone de notification (" + name + ")");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Write("Épinglage icône : " + ex.Message); }
        }
    }

    // ===================================================================== Application (zone de notification + fenêtre)
    class TrayApp : ApplicationContext
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly MainForm form;
        readonly EventWaitHandle showEvent;
        readonly System.Windows.Forms.Timer pinTimer;
        IntPtr iconHandle = IntPtr.Zero;
        bool autoStart;
        int pinTries;

        public TrayApp(bool startInTray)
        {
            Settings.Load();
            Theme.Current = Theme.Make(Settings.DarkTheme);

            form = new MainForm(this);
            var forceHandle = form.Handle;

            menu = new ContextMenuStrip { Renderer = new CyberRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
            menu.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };

            tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "Outils VTx by alex.b" };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
            SetTrayState(null);

            // une 2e instance demande l'affichage de la fenêtre
            bool createdNew;
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName, out createdNew);
            var th = new Thread(() =>
            {
                while (true)
                {
                    try { showEvent.WaitOne(); form.BeginInvoke(new Action(ShowMain)); } catch { return; }
                }
            }) { IsBackground = true };
            th.Start();

            // épinglage de l'icône : Windows crée l'entrée quelques secondes après l'apparition
            pinTimer = new System.Windows.Forms.Timer { Interval = 4000 };
            pinTimer.Tick += (s, e) => { TrayPin.Promote(); if (++pinTries >= 6) pinTimer.Stop(); pinTimer.Interval = 15000; };
            pinTimer.Start();

            Task.Run(() => { bool on = AutoStart.IsEnabled(); form.BeginInvoke(new Action(() => autoStart = on)); });

            if (startInTray) form.RefreshStatus();
            else ShowMain();
            Logger.Write("Démarrage Outils VTx " + Program.Version + (startInTray ? " (zone de notification)" : ""));
        }

        public void ShowMain()
        {
            if (!form.Visible) form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            form.BringToFront();
        }

        public void NotifyHidden()
        {
            if (Settings.TrayTipShown) return;
            Settings.TrayTipShown = true; Settings.Save();
            tray.ShowBalloonTip(4000, "Outils VTx — by alex.b", "Toujours actif près de l'horloge. Clic sur l'icône pour rouvrir.", ToolTipIcon.Info);
        }

        public void SetTrayState(VtxStatus s)
        {
            var t = Theme.Make(true);   // icône toujours en couleurs néon, lisible sur barre claire ou sombre
            Color c = t.Muted; string letter = "…"; string txt = "Outils VTx by alex.b";
            if (s != null)
            {
                switch (s.Current)
                {
                    case VtxStatus.Mode.VMware: c = t.Ok; letter = "V"; txt = "Outils VTx — mode VMware"; break;
                    case VtxStatus.Mode.Windows: c = t.Warn; letter = "W"; txt = "Outils VTx — mode Windows"; break;
                    default: c = t.Bad; letter = "!"; txt = "Outils VTx — VT-x désactivé (BIOS)"; break;
                }
                if (s.Pending != null) txt += " · redémarrage requis";
            }
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(Color.FromArgb(7, 10, 17))) g.FillEllipse(b, 1, 1, 30, 30);
                    using (var p = new Pen(c, 3f)) g.DrawEllipse(p, 2.5f, 2.5f, 27, 27);
                    using (var f = new Font("Consolas", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    using (var b = new SolidBrush(c))
                        g.DrawString(letter, f, b, new RectangleF(0, 1, 32, 32), sf);
                }
                IntPtr h = bmp.GetHicon();
                var old = tray.Icon;
                tray.Icon = Icon.FromHandle(h);
                if (old != null) old.Dispose();
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                iconHandle = h;
            }
            tray.Text = txt.Length > 63 ? txt.Substring(0, 63) : txt;
        }

        void BuildMenu()
        {
            foreach (ToolStripItem it in menu.Items.Cast<ToolStripItem>().ToList()) it.Dispose();
            menu.Items.Clear();
            menu.Renderer = new CyberRenderer();

            var head = new ToolStripMenuItem("OUTILS_VTX  ·  by alex.b") { Enabled = false };
            head.Font = new Font("Consolas", 9.5f, FontStyle.Bold);
            menu.Items.Add(head);
            if (form.LastStatus != null)
            {
                string m = form.LastStatus.Current == VtxStatus.Mode.VMware ? "État : mode VMware"
                         : form.LastStatus.Current == VtxStatus.Mode.Windows ? "État : mode Windows" : "État : VT-x désactivé (BIOS)";
                menu.Items.Add(new ToolStripMenuItem(m) { Enabled = false });
            }
            menu.Items.Add(new ToolStripSeparator());

            Add("Ouvrir Outils VTx", ShowMain);
            Add("▶  Mode VMware", () => { ShowMain(); form.RunVmware(); }, !form.Busy);
            Add("◀  Mode Windows", () => { ShowMain(); form.RunWindows(); }, !form.Busy);
            Add("⚡  Redémarrer dans le BIOS", () => { ShowMain(); form.RunBios(); }, !form.Busy);
            menu.Items.Add(new ToolStripSeparator());

            var theme = new ToolStripMenuItem("Thème sombre") { Checked = Theme.Current.Dark };
            theme.Click += (s, e) => form.ToggleTheme();
            menu.Items.Add(theme);

            var auto = new ToolStripMenuItem("Démarrer avec Windows") { Checked = autoStart };
            auto.Click += (s, e) =>
            {
                bool target = !autoStart;
                Task.Run(() =>
                {
                    if (target) AutoStart.Enable(); else AutoStart.Disable();
                    bool now = AutoStart.IsEnabled();
                    form.BeginInvoke(new Action(() =>
                    {
                        autoStart = now;
                        if (target && !now && tray != null) tray.ShowBalloonTip(6000, "Outils VTx — by alex.b", "Démarrage automatique possible uniquement avec la version installée (Setup), pas la version portable.", ToolTipIcon.Warning);
                    }));
                });
            };
            menu.Items.Add(auto);
            Add("Ouvrir le journal", OutilsVTx.MainForm.OpenLog);
            menu.Items.Add(new ToolStripSeparator());
            Add("Quitter", Quit);
        }

        void Add(string text, Action a, bool enabled = true)
        {
            var it = new ToolStripMenuItem(text) { Enabled = enabled };
            it.Click += (s, e) => a();
            menu.Items.Add(it);
        }

        public void Quit()
        {
            Logger.Write("Arrêt Outils VTx");
            tray.Visible = false;
            tray.Dispose();
            if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
            form.AllowClose = true;
            form.Close();
            ExitThread();
        }
    }

    // ===================================================================== Fenêtre principale
    class MainForm : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        readonly TrayApp app;
        readonly HeaderBar header;
        readonly StatusBanner banner;
        readonly RichTextBox details;
        readonly Panel detailsWrap, footer;
        readonly TableLayoutPanel grid;
        readonly CyberButton btnVmware, btnWindows, btnFeatures, btnBios, btnRefresh, btnReboot, btnTheme;
        readonly Label lblSign, lblBusy;
        readonly LinkLabel lnkLog;

        public VtxStatus LastStatus;
        public bool Busy;
        public bool AllowClose;

        public MainForm(TrayApp owner)
        {
            app = owner;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = Program.AppName + " · by alex.b";
            ClientSize = new Size(760, 720);
            MinimumSize = new Size(680, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // --- zone centrale (Fill)
            details = new RichTextBox
            {
                ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, ScrollBars = RichTextBoxScrollBars.Vertical,
                Font = new Font("Consolas", 9.5f), DetectUrls = false, WordWrap = false, TabStop = false
            };
            detailsWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 12) };
            var detailsCard = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 10, 10) };
            detailsCard.Paint += (s, e) =>
            {
                var t = Theme.Current;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, detailsCard.Width - 2, detailsCard.Height - 2), 10 * Theme.Dpi))
                using (var pen = new Pen(t.Border)) e.Graphics.DrawPath(pen, p);
            };
            detailsCard.Controls.Add(details);
            detailsWrap.Controls.Add(detailsCard);
            Controls.Add(detailsWrap);

            // --- boutons (Bottom)
            grid = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 222, ColumnCount = 2, RowCount = 3, Padding = new Padding(10, 0, 10, 6) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
            btnVmware = Btn("▶", "MODE VMWARE", "Couper Hyper-V / VBS / Intégrité mémoire", t => t.Ok);
            btnWindows = Btn("◀", "MODE WINDOWS", "Restaurer Hyper-V / VBS / Intégrité mémoire", t => t.Accent);
            btnFeatures = Btn("✕", "DÉSACTIVER HYPER-V / WSL2 / SANDBOX", "Si le mode VMware ne suffit pas", t => t.Accent2);
            btnBios = Btn("⚡", "REDÉMARRER DANS LE BIOS", "Si VT-x est désactivé dans le BIOS", t => t.Bad);
            btnRefresh = Btn("↻", "ACTUALISER", "Relire le diagnostic", t => t.Accent);
            btnReboot = Btn("↺", "REDÉMARRER", "Appliquer les changements en attente", t => t.Warn);
            grid.Controls.Add(btnVmware, 0, 0); grid.Controls.Add(btnWindows, 1, 0);
            grid.Controls.Add(btnFeatures, 0, 1); grid.Controls.Add(btnBios, 1, 1);
            grid.Controls.Add(btnRefresh, 0, 2); grid.Controls.Add(btnReboot, 1, 2);
            Controls.Add(grid);

            // --- pied de page (Bottom, tout en bas)
            footer = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            lblSign = new Label { Text = "Développé by alex.b  ·  v" + Program.Version, AutoSize = true, Location = new Point(18, 7), Font = new Font("Consolas", 8.5f) };
            lblBusy = new Label { AutoSize = true, Location = new Point(260, 7), Font = new Font("Consolas", 8.5f, FontStyle.Bold) };
            lnkLog = new LinkLabel { Text = "journal", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right, Font = new Font("Consolas", 8.5f) };
            lnkLog.LinkClicked += (s, e) => OpenLog();
            footer.Controls.Add(lblSign); footer.Controls.Add(lblBusy); footer.Controls.Add(lnkLog);
            footer.Resize += (s, e) => lnkLog.Location = new Point(footer.Width - lnkLog.Width - 18, 7);
            Controls.Add(footer);

            // --- bandeau d'état (Top)
            banner = new StatusBanner { Dock = DockStyle.Top, Height = 92 };
            Controls.Add(banner);

            // --- en-tête (Top, tout en haut) + bouton de thème
            header = new HeaderBar { Dock = DockStyle.Top, Height = 72 };
            btnTheme = new CyberButton { Glyph = "", Title = "", Size = new Size(132, 44), Anchor = AnchorStyles.Top | AnchorStyles.Right, AccentOf = t => t.Accent2 };
            header.Controls.Add(btnTheme);
            header.Resize += (s, e) => btnTheme.Location = new Point(header.Width - btnTheme.Width - 12, 12);
            Controls.Add(header);

            btnVmware.Click += (s, e) => RunVmware();
            btnWindows.Click += (s, e) => RunWindows();
            btnFeatures.Click += (s, e) => DoAction("Fonctionnalités hyperviseur",
                "Désactiver Hyper-V, la Plateforme de machine virtuelle, la Plateforme de l'hyperviseur et Windows Sandbox ?\n\nÀ utiliser seulement si le mode VMware ne suffit pas. Peut prendre plusieurs minutes.\nRéactivées par « Mode Windows ».",
                Actions.DisableFeatures);
            btnBios.Click += (s, e) => RunBios();
            btnRefresh.Click += (s, e) => RefreshStatus();
            btnReboot.Click += (s, e) => { if (Ask("Redémarrer l'ordinateur maintenant ?")) Reboot(false); };
            btnTheme.Click += (s, e) => ToggleTheme();

            ResumeLayout(false);
            PerformLayout();
            ApplyTheme();
            Shown += (s, e) => { if (LastStatus == null) RefreshStatus(); };
        }

        CyberButton Btn(string glyph, string title, string sub, Func<Theme, Color> acc)
        {
            return new CyberButton { Glyph = glyph, Title = title, Subtitle = sub, AccentOf = acc, Dock = DockStyle.Fill };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyDwm(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;      // la croix renvoie dans la zone de notification
                Hide();
                if (app != null) app.NotifyHidden();
                return;
            }
            base.OnFormClosing(e);
        }

        void ApplyDwm()
        {
            try
            {
                int dark = Theme.Current.Dark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref dark, 4);     // barre de titre sombre / claire
                int round = 2;
                DwmSetWindowAttribute(Handle, 33, ref round, 4);    // coins arrondis Windows 11
                int border = ColorTranslator.ToWin32(Theme.Current.Accent);
                DwmSetWindowAttribute(Handle, 34, ref border, 4);   // bordure néon Windows 11
            }
            catch { }
        }

        public void ToggleTheme()
        {
            Settings.DarkTheme = !Settings.DarkTheme;
            Settings.Save();
            Theme.Current = Theme.Make(Settings.DarkTheme);
            ApplyTheme();
        }

        void ApplyTheme()
        {
            var t = Theme.Current;
            BackColor = t.Bg;
            detailsWrap.BackColor = t.Bg;
            detailsWrap.Controls[0].BackColor = t.Surface;
            details.BackColor = t.Surface;
            details.ForeColor = t.Text;
            grid.BackColor = t.Bg;
            footer.BackColor = t.Dark ? Color.FromArgb(5, 8, 13) : Color.FromArgb(226, 232, 240);
            lblSign.ForeColor = t.Muted;
            lblBusy.ForeColor = t.Warn;
            lnkLog.LinkColor = t.Accent; lnkLog.ActiveLinkColor = t.Accent2; lnkLog.VisitedLinkColor = t.Accent;
            btnTheme.Glyph = t.Dark ? "☀" : "☾";
            btnTheme.Title = t.Dark ? "CLAIR" : "SOMBRE";
            btnTheme.Subtitle = "Thème";
            foreach (Control c in new Control[] { header, banner, btnVmware, btnWindows, btnFeatures, btnBios, btnRefresh, btnReboot, btnTheme, detailsWrap.Controls[0] })
                c.Invalidate();
            if (IsHandleCreated) ApplyDwm();
            if (LastStatus != null) Render(LastStatus);
        }

        void SetBusy(bool on, string text)
        {
            Busy = on;
            lblBusy.Text = on ? "[ " + text + " ]" : "";
            foreach (var b in new[] { btnVmware, btnWindows, btnFeatures, btnBios, btnRefresh, btnReboot }) b.Enabled = !on;
            UseWaitCursor = on;
        }

        bool Ask(string q)
        {
            return MessageBox.Show(this, q, Program.AppName + " · by alex.b", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        public static void OpenLog()
        {
            try { if (File.Exists(Logger.FilePath)) Process.Start("notepad.exe", "\"" + Logger.FilePath + "\""); } catch { }
        }

        // ------------------------------------------------------------- état
        public void RefreshStatus()
        {
            if (Busy) return;
            SetBusy(true, "analyse…");
            Task.Run(() =>
            {
                VtxStatus st = null; string err = null;
                try { st = VtxStatus.Read(); } catch (Exception ex) { err = ex.Message; }
                BeginInvoke(new Action(() =>
                {
                    SetBusy(false, "");
                    if (st == null) { details.Text = "Erreur de lecture : " + err; return; }
                    LastStatus = st;
                    Render(st);
                    if (app != null) app.SetTrayState(st);
                }));
            });
        }

        void Render(VtxStatus s)
        {
            var t = Theme.Current;
            switch (s.Current)
            {
                case VtxStatus.Mode.VMware: banner.Title = "MODE VMWARE // VT-x DISPONIBLE"; banner.ColorOf = x => x.Ok; break;
                case VtxStatus.Mode.Windows: banner.Title = "MODE WINDOWS // HYPERVISEUR ACTIF"; banner.ColorOf = x => x.Warn; break;
                default: banner.Title = "VT-x DÉSACTIVÉ DANS LE BIOS"; banner.ColorOf = x => x.Bad; break;
            }
            banner.Sub = s.Pending ?? "";
            banner.Invalidate();

            btnVmware.Recommended = s.Current == VtxStatus.Mode.Windows && s.Pending == null;
            btnBios.Recommended = s.Current == VtxStatus.Mode.BiosOff;
            btnReboot.Recommended = s.Pending != null;
            foreach (var b in new[] { btnVmware, btnBios, btnReboot }) b.Invalidate();

            string vtx = s.HypervisorPresent ? "MASQUÉ (détenu par l'hyperviseur)" : s.FirmwareVtx ? "ACTIVÉ" : "DÉSACTIVÉ dans le BIOS";
            Color vtxC = s.HypervisorPresent ? t.Warn : s.FirmwareVtx ? t.Ok : t.Bad;
            string vbs = s.VbsStatus == 0 ? "désactivée" : s.VbsStatus == 1 ? "activée, non démarrée" : s.VbsStatus == 2 ? "EN COURS D'EXÉCUTION" : "inconnue";
            string svc = s.ServicesRunning.Count == 0 ? "aucun" :
                string.Join(", ", s.ServicesRunning.Select(x => VtxStatus.ServiceNames.ContainsKey(x) ? VtxStatus.ServiceNames[x] : "service " + x));

            details.SuspendLayout();
            details.Clear();
            Head("// DIAGNOSTIC");
            Row("Processeur", s.Cpu, t.Text);
            Row("VT-x (firmware)", vtx, vtxC);
            Row("Hyperviseur Windows", s.HypervisorPresent ? "CHARGÉ" : "non chargé", s.HypervisorPresent ? t.Warn : t.Ok);
            Row("hypervisorlaunchtype", s.LaunchType, s.LaunchType.Equals("Off", StringComparison.OrdinalIgnoreCase) ? t.Ok : t.Text);
            Row("VBS", vbs, s.VbsStatus == 2 ? t.Warn : t.Text);
            Row("Services VBS actifs", svc, s.ServicesRunning.Count > 0 ? t.Warn : t.Text);
            Row("Intégrité de la mémoire", s.Hvci == 1 ? "activée" : s.Hvci.HasValue ? "désactivée" : "non définie", s.Hvci == 1 ? t.Warn : t.Text);
            Row("Credential Guard", s.LsaCfgFlags.HasValue && s.LsaCfgFlags.Value != 0 ? "configuré (" + s.LsaCfgFlags + ")" : "non configuré", t.Text);
            Row("BitLocker (lecteur système)", s.BitLockerOn ? "protection activée" : "inactif", t.Text);
            Row("Sauvegarde d'origine", Backup.Exists ? "présente" : "aucune (créée au 1er mode VMware)", t.Text);
            Line("");
            Head("// FONCTIONNALITÉS WINDOWS");
            foreach (var f in s.Features)
                Row(f.Label, f.State == "Enabled" ? "activée" : f.State == "Disabled" ? "désactivée" : "absente", f.State == "Enabled" ? t.Warn : t.Muted);
            Line("");
            Head("// CONCLUSION");
            if (s.Pending != null)
                Text2(s.Pending + " → bouton REDÉMARRER.", t.Warn);
            if (s.Current == VtxStatus.Mode.Windows)
            {
                Text2("L'hyperviseur Windows est chargé : il occupe VT-x.", t.Text);
                if (s.Pending == null) Text2("→ MODE VMWARE, puis redémarrer.", t.Accent);
            }
            else if (s.Current == VtxStatus.Mode.BiosOff)
            {
                Text2("VT-x est désactivé dans le BIOS / UEFI : impossible à corriger depuis Windows.", t.Bad);
                Text2("→ REDÉMARRER DANS LE BIOS, puis :", t.Accent);
                Text2("   ASUS carte mère : F7 Advanced Mode > Advanced > CPU Configuration", t.Text);
                Text2("                     > Intel (VMX) Virtualization Technology = Enabled", t.Text);
                Text2("   ASUS portable   : Advanced > CPU Configuration > Intel Virtualization Technology", t.Text);
                Text2("   HP              : F10 > Advanced > System Options > Virtualization Technology (VTx)", t.Text);
                Text2("   Dell            : F2 > Virtualization Support > Enable Intel Virtualization Technology", t.Text);
                Text2("   Lenovo          : F1/F2 > Security (ou Configuration) > Virtualization > Intel VT-x", t.Text);
                Text2("   Désactiver aussi Intel TXT / Trusted Execution si présent. F10 pour enregistrer.", t.Muted);
            }
            else if (s.Pending == null)
            {
                Text2("VT-x est disponible et aucun hyperviseur Windows n'est chargé : VMware doit démarrer.", t.Ok);
                Text2("Si l'erreur persiste : REDÉMARRER (pas « Arrêter ») et mettre VMware Workstation à jour.", t.Muted);
            }
            details.SelectionStart = 0;
            details.ScrollToCaret();
            details.ResumeLayout();
        }

        void Append(string text, Color c, bool bold)
        {
            details.SelectionStart = details.TextLength;
            details.SelectionLength = 0;
            details.SelectionColor = c;
            details.SelectionFont = new Font("Consolas", 9.5f, bold ? FontStyle.Bold : FontStyle.Regular);
            details.AppendText(text);
        }
        void Head(string h) { Append(h + "\n", Theme.Current.Accent, true); }
        void Row(string k, string v, Color c) { Append(("  " + k + " ").PadRight(36, '·') + " ", Theme.Current.Muted, false); Append(v + "\n", c, false); }
        void Text2(string s, Color c) { Append("  " + s + "\n", c, false); }
        void Line(string s) { Append(s + "\n", Theme.Current.Text, false); }

        // ------------------------------------------------------------- actions
        public void RunVmware()
        {
            DoAction("Mode VMware",
                "Passer en mode VMware ?\n\n• Hyperviseur Windows, VBS et Intégrité de la mémoire coupés\n• WSL2, Docker Desktop, Windows Sandbox et VM Hyper-V ne fonctionneront plus\n• Protection du noyau réduite\n\nRéversible avec « Mode Windows ». Redémarrage nécessaire.",
                Actions.EnableVMwareMode);
        }

        public void RunWindows()
        {
            DoAction("Mode Windows",
                "Revenir au mode Windows ?\n\nL'hyperviseur, la VBS et l'Intégrité de la mémoire seront restaurés comme avant le premier passage en mode VMware.\nVMware passera alors par Hyper-V (plus lent) ou refusera de démarrer.\n\nRedémarrage nécessaire.",
                Actions.EnableWindowsMode);
        }

        public void RunBios()
        {
            if (Busy || !Ask("Redémarrer directement dans le BIOS / UEFI ?\nEnregistre ton travail avant.")) return;
            Reboot(true);
        }

        void DoAction(string title, string confirm, Func<string> action)
        {
            if (Busy || !Ask(confirm)) return;
            SetBusy(true, title.ToLowerInvariant() + " en cours…");
            Logger.Write(">>> " + title);
            Task.Run(() =>
            {
                string result; bool ok = true;
                try { result = action(); }
                catch (Exception ex) { result = ex.Message; ok = false; Logger.Write("ERREUR " + title + " : " + ex); }
                BeginInvoke(new Action(() =>
                {
                    SetBusy(false, "");
                    RefreshStatus();
                    if (!ok) { MessageBox.Show(this, "Échec : " + result, title, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                    if (MessageBox.Show(this, result + "\n\nRedémarrage nécessaire pour appliquer. Redémarrer maintenant ?", title + " · by alex.b",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                        Reboot(false);
                }));
            });
        }

        void Reboot(bool firmware)
        {
            string o;
            int rc = Sys.Run(Sys.Exe("shutdown.exe"), firmware ? "/r /fw /t 3" : "/r /t 3", out o, 15000);
            if (rc != 0)
                MessageBox.Show(this, "Échec du redémarrage : " + o +
                    (firmware ? "\n\nAlternative : redémarrer et appuyer en boucle sur F2 / Suppr (ASUS), F10 (HP), F2 (Dell) au démarrage." : ""),
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Logger.Write(firmware ? "Redémarrage vers le firmware" : "Redémarrage");
        }
    }
}
