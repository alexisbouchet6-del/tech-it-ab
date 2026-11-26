// Socle commun des outils alex.b : thème cyber sombre / clair, composants, utilitaires système
// Cible : .NET Framework 4.8 (inclus dans Windows 10 / 11)
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Sécurité : les fonctions natives appelées (user32, kernel32, mpr, dwmapi…) ne sont chargées QUE depuis System32
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace AlexB
{
    // ===================================================================== Identité de l'appli
    static class AppInfo
    {
        public static string Name = "Outil";        // nom affiché
        public static string Id = "Outil";          // nom de dossier
        public static string Version = "1.0.0";
        public static bool MachineWide = false;     // données dans ProgramData (sinon LocalAppData)
        static bool locked;

        public static string DataDir
        {
            get
            {
                var root = Environment.GetFolderPath(MachineWide ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData);
                var d = Path.Combine(root, Id);
                try { Directory.CreateDirectory(d); } catch { }
                if (MachineWide && !locked) { locked = true; Hardening.LockDir(d); }
                return d;
            }
        }
    }

    // ===================================================================== Durcissement
    static class Hardening
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectory(string path);

        // retire le dossier courant de la recherche des DLL
        public static void Apply() { try { SetDllDirectory(""); } catch { } }

        // dossier temporaire NEUF, créé directement avec une ACL SYSTEM + Administrateurs (sans fenêtre de course) :
        // pour tout fichier ensuite exécuté ou importé avec les droits admin (%TEMP% est modifiable par les processus non élevés)
        public static string SecureTempDir(string prefix)
        {
            var name = prefix + "-" + Guid.NewGuid().ToString("N");
            if (!Elevation.IsAdmin) { var t = Path.Combine(Path.GetTempPath(), name); Directory.CreateDirectory(t); return t; }
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), name);
            var ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            var inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            try { Directory.CreateDirectory(d, ds); }
            catch (Exception ex) when (ex is PlatformNotSupportedException || ex is NotImplementedException) { Directory.CreateDirectory(d); }   // Mono (tests)
            return d;
        }

        // un exe lancé avec les droits admin sans invite (tâche planifiée) doit être dans Program Files :
        // dans Téléchargements ou sur le Bureau, n'importe quel programme de la session pourrait le remplacer
        public static bool InProtectedDir(string exe)
        {
            var full = Path.GetFullPath(exe);
            foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var pf = Environment.GetFolderPath(sf);
                if (pf.Length > 0 && full.StartsWith(pf.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // dossier de données dans ProgramData : seuls SYSTEM et Administrateurs écrivent, les utilisateurs lisent
        public static void LockDir(string dir)
        {
            if (!Elevation.IsAdmin) return;
            try
            {
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                var inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                var ds = new DirectorySecurity();
                ds.SetAccessRuleProtection(true, false);
                ds.SetOwner(admins);
                ds.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
                ds.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
                ds.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(dir, ds);
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fs = new FileSecurity();
                        fs.SetAccessRuleProtection(false, false);
                        fs.SetOwner(admins);
                        File.SetAccessControl(f, fs);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // écrase un fichier avec des données aléatoires avant de le supprimer
        public static bool Shred(string path)
        {
            try
            {
                var len = new FileInfo(path).Length;
                var rnd = new System.Security.Cryptography.RNGCryptoServiceProvider();
                var buf = new byte[64 * 1024];
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    for (long done = 0; done < len; done += buf.Length) { rnd.GetBytes(buf); fs.Write(buf, 0, (int)Math.Min(buf.Length, len - done)); }
                    fs.Flush(true);
                }
                File.Delete(path);
                return true;
            }
            catch { try { File.Delete(path); } catch { } return false; }
        }
    }

    static class Log
    {
        static readonly object Lock = new object();
        public static string FilePath { get { return Path.Combine(AppInfo.DataDir, AppInfo.Id.ToLowerInvariant() + ".log"); } }

        public static void Write(string msg)
        {
            try
            {
                lock (Lock)
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void Open()
        {
            try { if (File.Exists(FilePath)) Process.Start("notepad.exe", "\"" + FilePath + "\""); } catch { }
        }
    }

    // ===================================================================== Fichier ini simple
    class Ini
    {
        readonly string path;
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Sections = new List<string>();      // lignes brutes de [section] multi-valeurs

        public Ini(string file)
        {
            path = file;
            try
            {
                if (!File.Exists(path)) return;
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
        }

        public string Get(string key, string def) { string v; return values.TryGetValue(key, out v) ? v : def; }
        public int GetInt(string key, int def) { int n; return int.TryParse(Get(key, ""), out n) ? n : def; }
        public bool GetBool(string key, bool def) { var v = Get(key, ""); return v.Length == 0 ? def : v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); }
        public void Set(string key, string value) { values[key] = value ?? ""; }
        public void Set(string key, bool value) { values[key] = value ? "1" : "0"; }
        public IEnumerable<string> Keys { get { return values.Keys.ToList(); } }
        public void Remove(string key) { values.Remove(key); }

        public void Save(string header)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# " + header + " (by alex.b)");
                foreach (var kv in values.OrderBy(k => k.Key)) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("Écriture " + path + " : " + ex.Message); }
        }
    }

    static class Prefs
    {
        static Ini ini;
        public static Ini Ini { get { if (ini == null) ini = new Ini(Path.Combine(AppInfo.DataDir, "config.ini")); return ini; } }
        public static void Save() { Ini.Save(AppInfo.Name + " - préférences"); }
    }

    // ===================================================================== Processus et droits
    static class Proc
    {
        public static string Sys(string exe) { return Path.Combine(Environment.SystemDirectory, exe); }

        // lecteur système (C:\) avec repli si indéterminable
        public static string SysDrive
        {
            get { try { var r = Path.GetPathRoot(Environment.SystemDirectory); return string.IsNullOrEmpty(r) ? "C:\\" : r; } catch { return "C:\\"; } }
        }

        public static Encoding Oem
        {
            get { try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { return Encoding.Default; } }
        }

        // Jeton d'annulation de la tâche en cours (fourni par l'application hôte, ex. centre des tâches d'AMI·OPS)
        public static Func<CancellationToken> Token;

        // journal : jamais de mot de passe en clair (PASSWORD=…, /p:…, -Password …)
        public static string Mask(string args)
        {
            return System.Text.RegularExpressions.Regex.Replace(args ?? "", @"(?i)(\bPASSWORD=|/p:|-Password\s+)(""[^""]*""|\S+)", "$1***");
        }

        // arrêt du programme ET de ses enfants (DISM, sfc… lancés par PowerShell)
        static void KillTree(Process p)
        {
            try { using (var k = Process.Start(new ProcessStartInfo(Sys("taskkill.exe"), "/T /F /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false })) if (k != null) k.WaitForExit(10000); } catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        // Lance un programme caché, renvoie le code de sortie ; chaque ligne est transmise à onLine si fourni.
        // Annulation de la tâche : le programme et ses enfants sont arrêtés, OperationCanceledException est levée.
        public static int Run(string exe, string args, int timeoutMs, Encoding enc, Action<string> onLine, out string output)
        {
            var tok = Token != null ? Token() : CancellationToken.None;
            tok.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = enc ?? Oem, StandardErrorEncoding = enc ?? Oem
            };
            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    DataReceivedEventHandler h = (s, e) =>
                    {
                        if (e.Data == null) return;
                        string line = e.Data.Replace("\0", "").TrimEnd();
                        lock (sb) sb.AppendLine(line);
                        if (onLine != null && line.Length > 0) onLine(line);
                    };
                    p.OutputDataReceived += h; p.ErrorDataReceived += h;
                    p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    var sw = Stopwatch.StartNew(); bool exited, cancelled = false;
                    while (!(exited = p.WaitForExit(200)))
                    {
                        if (tok.IsCancellationRequested) { cancelled = true; break; }
                        if (timeoutMs >= 0 && sw.ElapsedMilliseconds > timeoutMs) break;
                    }
                    if (!exited)
                    {
                        KillTree(p);
                        if (cancelled) { Log.Write(Path.GetFileName(exe) + " => annulé"); throw new OperationCanceledException(); }
                        output = sb + "\nDélai dépassé (" + timeoutMs / 1000 + " s)";
                        Log.Write(Path.GetFileName(exe) + " " + Mask(args) + " => délai dépassé");
                        return -1;
                    }
                    p.WaitForExit();
                    output = sb.ToString();
                    Log.Write(Path.GetFileName(exe) + " " + Mask(args) + " => " + p.ExitCode);
                    return p.ExitCode;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                output = ex.Message;
                Log.Write(Path.GetFileName(exe) + " " + Mask(args) + " => " + ex.Message);
                return -2;
            }
        }

        public static int Run(string exe, string args, int timeoutMs, out string output)
        {
            return Run(exe, args, timeoutMs, null, null, out output);
        }
    }

    static class Elevation
    {
        public static bool IsAdmin
        {
            get { using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
        }

        // Relance en administrateur ; renvoie true si l'instance actuelle doit se fermer
        public static bool EnsureAdmin(string why)
        {
            if (IsAdmin) return false;
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(AppInfo.Name + " a besoin des droits administrateur : " + why, AppInfo.Name + " · by alex.b",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return true;
        }

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        public static void DpiAware() { try { SetProcessDPIAware(); } catch { } }
    }

    static class Fmt
    {
        public static string Size(double bytes)
        {
            string[] u = { "o", "Ko", "Mo", "Go", "To" };
            int i = 0;
            while (Math.Abs(bytes) >= 1024 && i < u.Length - 1) { bytes /= 1024; i++; }
            return bytes.ToString(i == 0 ? "0" : "0.0", CultureInfo.GetCultureInfo("fr-FR")) + " " + u[i];
        }

        public static string Html(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
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

        public Color ByName(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "muted": return Muted;
                case "accent": return Accent;
                case "accent2": return Accent2;
                case "ok": return Ok;
                case "warn": return Warn;
                case "bad": return Bad;
                default: return Text;
            }
        }

        public static Color Blend(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Max(1f, rad * 2);
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

        static bool? mdl2;
        public static bool HasMdl2
        {
            get { if (!mdl2.HasValue) mdl2 = FontFamily.Families.Any(f => f.Name == "Segoe MDL2 Assets"); return mdl2.Value; }
        }
        public static Font IconFont(float size) { return HasMdl2 ? new Font("Segoe MDL2 Assets", size) : new Font("Segoe UI Symbol", size); }
        public static string Icon(string mdl2Code, string fallback) { return HasMdl2 ? mdl2Code : fallback; }
    }

    static class Dwm
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        public static void Style(Form f)
        {
            try
            {
                int dark = Theme.Current.Dark ? 1 : 0; DwmSetWindowAttribute(f.Handle, 20, ref dark, 4);
                int round = 2; DwmSetWindowAttribute(f.Handle, 33, ref round, 4);
                int border = ColorTranslator.ToWin32(Theme.Current.Accent); DwmSetWindowAttribute(f.Handle, 34, ref border, 4);
            }
            catch { }
        }
    }

    // ===================================================================== Composants
    abstract class CyberControl : Control
    {
        protected bool hover, pressed;
        protected CyberControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand; TabStop = true;
        }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { PerformClick(); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected Graphics Prep(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Current.Bg);
            return g;
        }
    }

    // Grande tuile : pictogramme + titre + indication
    class Tile : CyberControl
    {
        public string Glyph = "", Title = "", Hint = "";
        public Func<Theme, Color> AccentOf = t => t.Accent;

        public Tile() { Margin = new Padding(Theme.S(5)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.K; var g = Prep(e);
            Color acc = Enabled ? AccentOf(t) : t.Muted;
            var r = new RectangleF(2 * k, 2 * k, Width - 4 * k - 1, Height - 4 * k - 1);
            if (hover && Enabled && t.Dark)
                using (var gp = Theme.Round(RectangleF.Inflate(r, 1.5f * k, 1.5f * k), 12 * k))
                using (var pen = new Pen(Color.FromArgb(80, acc), 4 * k)) g.DrawPath(pen, gp);
            using (var path = Theme.Round(r, 11 * k))
            {
                using (var b = new SolidBrush(Theme.Blend(t.Surface2, acc, !Enabled ? 0 : pressed ? 0.30 : hover ? 0.15 : 0.0))) g.FillPath(b, path);
                var clip = g.Clip; g.SetClip(path);
                using (var bar = new SolidBrush(acc)) g.FillRectangle(bar, r.X, r.Bottom - 3 * k, r.Width, 3 * k);
                g.Clip = clip;
                using (var pen = new Pen(Color.FromArgb(hover || Focused ? 255 : 110, acc), (hover || Focused ? 1.8f : 1f) * k)) g.DrawPath(pen, path);
            }
            bool hasGlyph = Glyph.Length > 0;
            if (hasGlyph)
                using (var f = Theme.IconFont(20f)) using (var b = new SolidBrush(acc))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(Glyph, f, b, new RectangleF(r.X, r.Y + 8 * k, r.Width, r.Height * 0.45f), sf);
            using (var tf = new Font("Segoe UI Semibold", 9.5f)) using (var hf = new Font("Consolas", 7.5f))
            using (var tb = new SolidBrush(Enabled ? t.Text : t.Muted)) using (var hb = new SolidBrush(t.Muted))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = hasGlyph ? StringAlignment.Near : StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
            {
                var tr = hasGlyph ? new RectangleF(r.X + 4 * k, r.Y + r.Height * 0.52f, r.Width - 8 * k, r.Height * 0.30f)
                                  : new RectangleF(r.X + 4 * k, r.Y, r.Width - 8 * k, r.Height - (Hint.Length > 0 ? 14 * k : 0));
                g.DrawString(Title, tf, tb, tr, sf);
                if (Hint.Length > 0)
                    using (var hs = new StringFormat { Alignment = StringAlignment.Center })
                        g.DrawString(Hint, hf, hb, new RectangleF(r.X, r.Bottom - 22 * k, r.Width, 14 * k), hs);
            }
        }
    }

    // Petit bouton arrondi
    class Chip : CyberControl
    {
        public bool Active, Primary;
        public Func<Theme, Color> AccentOf = t => t.Accent;

        public Chip(string text) { SetText(text); }

        public void SetText(string text)
        {
            Text = text;
            Font = new Font("Segoe UI Semibold", 9f);
            using (var g = CreateGraphics())
                Size = new Size((int)g.MeasureString("✓ " + text, Font).Width + Theme.S(18), Theme.S(30));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.K; var g = Prep(e);
            Color acc = Enabled ? AccentOf(t) : t.Muted;
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var path = Theme.Round(r, 7 * k))
            {
                Color fill, fore;
                if ((Active || Primary) && Enabled)
                {
                    fill = pressed ? Theme.Blend(acc, Color.Black, 0.2) : hover ? Theme.Blend(acc, Color.White, 0.12) : acc;
                    fore = t.Dark ? t.Bg : Color.White;
                }
                else
                {
                    fill = Theme.Blend(t.Surface, acc, !Enabled ? 0 : pressed ? 0.30 : hover ? 0.16 : 0.0);
                    fore = Enabled ? t.Text : t.Muted;
                }
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var pen = new Pen(Enabled ? Color.FromArgb(hover || Active || Focused ? 255 : 140, acc) : t.Border, 1.2f * k)) g.DrawPath(pen, path);
                using (var tb = new SolidBrush(fore))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString((Active ? "✓ " : "") + Text, Font, tb, r, sf);
            }
        }
    }

    // Carte arrondie avec barre d'état à gauche
    class Card : Panel
    {
        public Func<Theme, Color> StatusOf = t => t.Accent;
        public Card() { DoubleBuffered = true; ResizeRedraw = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.K; var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : t.Bg);
            Color st = StatusOf(t);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Round(r, 10 * k))
            {
                using (var b = new SolidBrush(t.Surface2)) g.FillPath(b, path);
                var clip = g.Clip; g.SetClip(path);
                using (var b = new SolidBrush(st)) g.FillRectangle(b, 0, 0, 4 * k, Height);
                g.Clip = clip;
                using (var p = new Pen(Color.FromArgb(t.Dark ? 120 : 255, Theme.Blend(t.Border, st, 0.35)), 1.2f * k)) g.DrawPath(p, path);
            }
        }
    }

    // Barre de progression néon
    class NeonBar : Control
    {
        double value;
        public double Value { get { return value; } set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
        public NeonBar() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Height = Theme.S(10); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.K; var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : t.Bg);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var p = Theme.Round(r, Height / 2f)) using (var b = new SolidBrush(t.Surface2)) g.FillPath(b, p);
            if (value > 0)
            {
                var f = new RectangleF(0, 0, Math.Max(Height, (Width - 1) * (float)value), Height - 1);
                using (var p = Theme.Round(f, Height / 2f))
                using (var lg = new LinearGradientBrush(new RectangleF(0, 0, Math.Max(1, Width), 1), t.Accent, t.Accent2, 0f))
                    g.FillPath(lg, p);
            }
        }
    }

    // Zone de journal colorée, utilisable depuis n'importe quel thread
    class LogBox : RichTextBox
    {
        public LogBox()
        {
            ReadOnly = true; BorderStyle = BorderStyle.None; DetectUrls = false; WordWrap = true;
            Font = new Font("Consolas", 9.5f); ScrollBars = RichTextBoxScrollBars.Vertical; TabStop = false;
        }

        public void Line(string text, Color c, bool bold = false)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => Line(text, c, bold))); } catch { } return; }
            SelectionStart = TextLength; SelectionLength = 0;
            SelectionColor = c;
            SelectionFont = new Font("Consolas", 9.5f, bold ? FontStyle.Bold : FontStyle.Regular);
            AppendText(text + "\n");
            SelectionStart = TextLength; ScrollToCaret();
        }

        public void Line(string text, string colorName, bool bold = false) { Line(text, Theme.Current.ByName(colorName), bold); }
    }

    // ===================================================================== Fenêtre de base
    class CyberForm : Form
    {
        public readonly Panel Body;
        protected readonly Chip ThemeChip;
        protected string Subtitle = "";
        public static readonly int HeaderH = Theme.S(78), FooterH = Theme.S(30);

        public CyberForm(string title, int width, int height)
        {
            Text = AppInfo.Name + " · by alex.b";
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true; KeyPreview = true;
            ClientSize = new Size(Theme.S(width), Theme.S(height));
            MinimumSize = new Size(Theme.S(width * 0.8f), Theme.S(height * 0.8f));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            HeaderTitle = title;

            Body = new Panel();
            Controls.Add(Body);
            ThemeChip = new Chip(Theme.Current.Dark ? "☀ Clair" : "☾ Sombre") { AccentOf = x => x.Accent2, TabStop = false };
            ThemeChip.Click += (s, e) => ToggleTheme();
            Controls.Add(ThemeChip);
            Resize += (s, e) => DoLayout();
            DoLayout();
        }

        public string HeaderTitle;

        void DoLayout()
        {
            Body.Location = new Point(0, HeaderH);
            Body.Size = new Size(ClientSize.Width, Math.Max(10, ClientSize.Height - HeaderH - FooterH));
            ThemeChip.Location = new Point(ClientSize.Width - ThemeChip.Width - Theme.S(14), Theme.S(16));
            Invalidate();
        }

        public void SetSubtitle(string s) { Subtitle = s; Invalidate(new Rectangle(0, 0, Width, HeaderH)); }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Dwm.Style(this); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); ApplyTheme(); }

        public void ToggleTheme()
        {
            Prefs.Ini.Set("Theme", Theme.Current.Dark ? "light" : "dark");
            Prefs.Save();
            Theme.Current = Theme.Make(!Theme.Current.Dark);
            ThemeChip.SetText(Theme.Current.Dark ? "☀ Clair" : "☾ Sombre");
            ApplyTheme();
        }

        public static void LoadTheme() { Theme.Current = Theme.Make(Prefs.Ini.Get("Theme", "dark") != "light"); }

        public virtual void ApplyTheme()
        {
            BackColor = Theme.Current.Bg;
            Recolor(this);
            if (IsHandleCreated) Dwm.Style(this);
            Invalidate(true);
        }

        // Recolore récursivement les contrôles standard ; Tag = "muted" / "accent" / "warn"… pour la couleur du texte
        public static void Recolor(Control root)
        {
            var t = Theme.Current;
            foreach (Control c in root.Controls)
            {
                Color bg = c.Parent is Card ? t.Surface2 : (c.Parent != null ? c.Parent.BackColor : t.Bg);
                if (c is Card) { }
                else if (c is LogBox || c is RichTextBox) { c.BackColor = t.Surface; c.ForeColor = t.Text; }
                else if (c is TextBox || c is NumericUpDown || c is ComboBox || c is ListBox) { c.BackColor = t.Surface2 == t.Surface ? Theme.Blend(t.Surface, t.Border, 0.25) : t.Surface2; c.ForeColor = t.Text; }
                else if (c is CheckBox || c is RadioButton)
                {
                    c.BackColor = bg; c.ForeColor = c.Tag is string ? t.ByName((string)c.Tag) : t.Text;
                    var cb = c as ButtonBase; if (cb != null) { cb.FlatStyle = FlatStyle.Flat; cb.FlatAppearance.BorderColor = t.Accent; cb.FlatAppearance.CheckedBackColor = t.Accent; }
                }
                else if (c is ListView) { c.BackColor = t.Surface; c.ForeColor = t.Text; }
                else if (c is Label && !(c is LinkLabel)) { c.BackColor = bg; c.ForeColor = c.Tag is string ? t.ByName((string)c.Tag) : t.Text; }
                else if (c is LinkLabel) { var l = (LinkLabel)c; l.BackColor = bg; l.LinkColor = t.Accent; l.ActiveLinkColor = t.Accent2; l.VisitedLinkColor = t.Accent; }
                else if (c is Panel || c is TableLayoutPanel || c is FlowLayoutPanel || c is SplitContainer) { c.BackColor = c.Parent is Card ? t.Surface2 : (c.Parent != null ? c.Parent.BackColor : t.Bg); }
                if (c is CyberControl || c is Card || c is NeonBar) c.Invalidate();
                if (c.HasChildren) Recolor(c);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.K; var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(t.Bg);
            if (t.Dark) using (var p = new Pen(Color.FromArgb(12, t.Accent))) for (float x = 0; x < Width; x += 22 * k) g.DrawLine(p, x, 0, x, HeaderH);
            using (var f = new Font("Consolas", 16f, FontStyle.Bold)) using (var b = new SolidBrush(t.Accent))
                g.DrawString("> " + HeaderTitle + "_", f, b, 16 * k, 10 * k);
            using (var f = new Font("Consolas", 8.5f)) using (var b = new SolidBrush(t.Muted))
                g.DrawString(Subtitle.Length > 0 ? Subtitle : "by alex.b  //  v" + AppInfo.Version, f, b, 19 * k, 46 * k);
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), 1), t.Accent, t.Accent2, 0f))
                g.FillRectangle(lg, 0, HeaderH - 2 * k, Width, 2 * k);
            using (var f = new Font("Consolas", 8f)) using (var b = new SolidBrush(t.Muted))
                g.DrawString("Développé by alex.b  ·  " + AppInfo.Name + " v" + AppInfo.Version + "  ·  journal : " + Log.FilePath, f, b, 16 * k, ClientSize.Height - FooterH + 8 * k);
        }

        // Boîtes de message cohérentes
        public static bool Ask(IWin32Window owner, string q)
        {
            return MessageBox.Show(owner, q, AppInfo.Name + " · by alex.b", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        public static void Info(IWin32Window owner, string m) { MessageBox.Show(owner, m, AppInfo.Name + " · by alex.b", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        public static void Warn(IWin32Window owner, string m) { MessageBox.Show(owner, m, AppInfo.Name + " · by alex.b", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // ===================================================================== Point d'entrée commun
    static class Boot
    {
        public static void Run(Func<Form> make, bool needsAdmin, string adminWhy)
        {
            if (needsAdmin && Elevation.EnsureAdmin(adminWhy)) return;
            bool created;
            using (var mutex = new Mutex(true, "Local\\" + AppInfo.Id + "_alexb", out created))
            {
                if (!created) return;
                Hardening.Apply();
                Elevation.DpiAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) =>
                {
                    Log.Write("ERREUR UI : " + e.Exception);
                    MessageBox.Show("Erreur inattendue : " + e.Exception.Message + "\nDétails dans le journal.", AppInfo.Name + " · by alex.b", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                CyberForm.LoadTheme();
                Log.Write("Démarrage " + AppInfo.Name + " " + AppInfo.Version);
                Application.Run(make());
                GC.KeepAlive(mutex);
            }
        }
    }
}
