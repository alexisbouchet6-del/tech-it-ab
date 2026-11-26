// NetSwitch - interface cyber (panneau près de l'horloge, thème sombre / clair)
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSwitch
{
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

        static float? dpi;
        public static float K
        {
            get
            {
                if (!dpi.HasValue) { try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX / 96f; } catch { dpi = 1f; } }
                return dpi.Value;
            }
        }
        public static int S(float px) { return (int)Math.Round(px * K); }
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

    // ===================================================================== Bouton « puce »
    class Chip : Control
    {
        public bool Active, Primary;
        public Func<Theme, Color> AccentOf = t => t.Accent;
        bool hover, pressed;

        public Chip(string text)
        {
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand; TabStop = true;
            Font = new Font("Segoe UI Semibold", 9f);
            using (var g = CreateGraphics())
                Size = new Size((int)g.MeasureString("✓ " + text, Font).Width + Theme.S(18), Theme.S(30));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
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
            Color acc = Enabled ? AccentOf(t) : t.Muted;
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var path = Theme.Round(r, 7 * k))
            {
                Color fill; Color fore;
                if (Active || Primary)
                {
                    fill = pressed ? Theme.Blend(acc, Color.Black, 0.2) : hover ? Theme.Blend(acc, Color.White, 0.12) : acc;
                    fore = t.Dark ? t.Bg : Color.White;
                }
                else
                {
                    fill = Theme.Blend(t.Surface, acc, pressed ? 0.30 : hover ? 0.16 : 0.0);
                    fore = Enabled ? t.Text : t.Muted;
                }
                if (Active && t.Dark && Enabled)
                    using (var glow = new Pen(Color.FromArgb(70, acc), 4 * k)) g.DrawPath(glow, path);
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var pen = new Pen(Enabled ? Color.FromArgb(hover || Active ? 255 : 140, acc) : t.Border, 1.2f * k)) g.DrawPath(pen, path);
                using (var tb = new SolidBrush(fore))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString((Active ? "✓ " : "") + Text, Font, tb, r, sf);
            }
        }
    }

    // ===================================================================== Carte d'une carte réseau
    class Card : Panel
    {
        public Color Status;
        public Card() { DoubleBuffered = true; ResizeRedraw = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.K; var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : t.Bg);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Round(r, 10 * k))
            {
                using (var b = new SolidBrush(t.Surface2)) g.FillPath(b, path);
                var clip = g.Clip; g.SetClip(path);
                using (var b = new SolidBrush(Status)) g.FillRectangle(b, 0, 0, 4 * k, Height);
                if (t.Dark) using (var p = new Pen(Color.FromArgb(8, Status))) for (float y = 0; y < Height; y += 3 * k) g.DrawLine(p, 0, y, Width, y);
                g.Clip = clip;
                using (var p = new Pen(Color.FromArgb(t.Dark ? 120 : 255, Theme.Blend(t.Border, Status, 0.35)), 1.2f * k)) g.DrawPath(p, path);
            }
        }
    }

    // ===================================================================== Menu clic droit aux couleurs du thème
    class CyberRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using (var b = new SolidBrush(Theme.Current.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using (var p = new Pen(Theme.Current.Accent)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { using (var b = new SolidBrush(Theme.Current.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var t = Theme.Current;
            var c = e.Item.Selected && e.Item.Enabled ? Theme.Blend(t.Surface, t.Accent, 0.22) : t.Surface;
            using (var b = new SolidBrush(c)) e.Graphics.FillRectangle(b, 2, 0, e.Item.Width - 4, e.Item.Height);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Theme.Current.Text : Theme.Current.Muted; base.OnRenderItemText(e); }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { int y = e.Item.Height / 2; using (var p = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            using (var f = new Font("Segoe UI Symbol", 9f)) using (var b = new SolidBrush(Theme.Current.Accent))
                e.Graphics.DrawString("✓", f, b, e.ImageRectangle.X, e.ImageRectangle.Y - 1);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Current.Accent; base.OnRenderArrow(e); }
    }

    // ===================================================================== Épinglage de l'icône (Windows 11)
    static class TrayPin
    {
        public static void Promote()
        {
            try
            {
                string exeName = Path.GetFileName(Application.ExecutablePath);
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return;
                    foreach (var name in root.GetSubKeyNames())
                        using (var k = root.OpenSubKey(name, true))
                        {
                            var path = k == null ? null : k.GetValue("ExecutablePath") as string;
                            if (path == null || !path.EndsWith("\\" + exeName, StringComparison.OrdinalIgnoreCase)) continue;
                            var cur = k.GetValue("IsPromoted");
                            if (cur == null || Convert.ToInt32(cur) != 1)
                            {
                                k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                Logger.Write("Icône épinglée près de l'horloge (" + name + ")");
                            }
                        }
                }
            }
            catch (Exception ex) { Logger.Write("Épinglage icône : " + ex.Message); }
        }
    }

    // ===================================================================== Fenêtres de dialogue stylées
    class CyberDialog : Form
    {
        protected readonly int Pad = Theme.S(18);
        protected int Y;

        public CyberDialog(string title)
        {
            Text = title + " · by alex.b";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false; ShowInTaskbar = true; TopMost = true;
            StartPosition = FormStartPosition.CenterScreen; KeyPreview = true;
            BackColor = Theme.Current.Bg; ForeColor = Theme.Current.Text;
            Font = new Font("Segoe UI", 9.5f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Y = Pad;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Dwm.Style(this); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); }

        protected Label AddLabel(string text, Color c, Font f, int width)
        {
            var l = new Label { Text = text, ForeColor = c, BackColor = Color.Transparent, Font = f, AutoSize = true, MaximumSize = new Size(width, 0), Location = new Point(Pad, Y) };
            Controls.Add(l);
            Y += l.PreferredHeight + Theme.S(8);
            return l;
        }

        protected void AddButtons(int width, params Chip[] chips)
        {
            Y += Theme.S(8);
            int x = Pad + width;
            foreach (var c in chips.Reverse()) { x -= c.Width; c.Location = new Point(x, Y); Controls.Add(c); x -= Theme.S(8); }
            Y += Theme.S(30) + Pad;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var t = Theme.Current;
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, ClientSize.Width), 1), t.Accent, t.Accent2, 0f))
                e.Graphics.FillRectangle(lg, 0, 0, ClientSize.Width, Theme.S(2));
        }
    }

    class InputDialog : CyberDialog
    {
        readonly TextBox[] boxes;
        bool ok;

        InputDialog(string title, string hint, string[] labels, string[] defaults) : base(title)
        {
            var t = Theme.Current;
            int W = Theme.S(430);
            AddLabel("> " + title.ToUpperInvariant(), t.Accent, new Font("Consolas", 11f, FontStyle.Bold), W);
            if (!string.IsNullOrEmpty(hint)) AddLabel(hint, t.Muted, new Font("Segoe UI", 8.5f), W);
            Y += Theme.S(4);

            boxes = new TextBox[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                var l = new Label { Text = labels[i], ForeColor = t.Text, AutoSize = true, Location = new Point(Pad, Y + Theme.S(7)) };
                Controls.Add(l);
                var frame = new Panel { Location = new Point(Pad + Theme.S(190), Y), Size = new Size(W - Theme.S(190), Theme.S(32)), BackColor = t.Surface2 };
                var tb = new TextBox
                {
                    Text = defaults[i], BorderStyle = BorderStyle.None, BackColor = t.Surface2, ForeColor = t.Text,
                    Font = new Font("Consolas", 10f), Location = new Point(Theme.S(10), Theme.S(7)), Width = frame.Width - Theme.S(20)
                };
                frame.Controls.Add(tb);
                Panel fr = frame;
                tb.GotFocus += (s, e) => fr.Invalidate();
                tb.LostFocus += (s, e) => fr.Invalidate();
                frame.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var p = new Pen(tb.Focused ? Theme.Current.Accent : Theme.Current.Border, 1.4f * Theme.K))
                    using (var path = Theme.Round(new RectangleF(0.7f, 0.7f, fr.Width - 2f, fr.Height - 2f), 6 * Theme.K))
                        e.Graphics.DrawPath(p, path);
                };
                Controls.Add(frame);
                boxes[i] = tb;
                Y += Theme.S(40);
            }

            var bOk = new Chip("Appliquer") { Primary = true };
            var bCancel = new Chip("Annuler") { AccentOf = x => x.Muted };
            bOk.Click += (s, e) => { ok = true; Close(); };
            bCancel.Click += (s, e) => Close();
            AddButtons(W, bCancel, bOk);
            ClientSize = new Size(W + 2 * Pad, Y);
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { ok = true; e.SuppressKeyPress = true; Close(); }
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
            };
            Shown += (s, e) => { if (boxes.Length > 0) { boxes[0].Focus(); boxes[0].SelectAll(); } };
        }

        public static string[] Ask(string title, string hint, string[] labels, string[] defaults)
        {
            using (var f = new InputDialog(title, hint, labels, defaults))
            {
                f.ShowDialog();
                return f.ok ? f.boxes.Select(b => b.Text.Trim()).ToArray() : null;
            }
        }
    }

    class InfoDialog : CyberDialog
    {
        InfoDialog(string title, string body, Color accent) : base(title)
        {
            int W = Theme.S(460);
            AddLabel("> " + title.ToUpperInvariant(), accent, new Font("Consolas", 11f, FontStyle.Bold), W);
            AddLabel(body, Theme.Current.Text, new Font("Consolas", 9.5f), W);
            var bOk = new Chip("OK") { Primary = true, AccentOf = x => accent };
            bOk.Click += (s, e) => Close();
            AddButtons(W, bOk);
            ClientSize = new Size(W + 2 * Pad, Y);
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape) Close(); };
        }

        public static void Show(string title, string body, bool error)
        {
            using (var f = new InfoDialog(title, body, error ? Theme.Current.Warn : Theme.Current.Accent)) f.ShowDialog();
        }
    }

    // ===================================================================== Panneau près de l'horloge
    class Flyout : Form
    {
        readonly TrayContext ctx;
        public DateTime HiddenAt = DateTime.MinValue;

        public Flyout(TrayContext owner)
        {
            ctx = owner;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; KeyPreview = true; DoubleBuffered = true;
            Font = new Font("Segoe UI", 9.5f);
            Deactivate += (s, e) => { if (Visible) { Hide(); HiddenAt = DateTime.Now; } };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; /* CS_DROPSHADOW */ return cp; }
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Dwm.Style(this); }

        public void Build(List<AdapterInfo> adapters, Config cfg, bool busy)
        {
            var t = Theme.Current; float k = Theme.K;
            SuspendLayout();
            foreach (Control c in Controls.Cast<Control>().ToList()) c.Dispose();
            Controls.Clear();
            BackColor = t.Bg;

            int W = Theme.S(452), pad = Theme.S(14), inner = W - 2 * pad;
            int y = Theme.S(72);

            // boutons d'en-tête
            var bTheme = new Chip(t.Dark ? "☀ Clair" : "☾ Sombre") { AccentOf = x => x.Accent2 };
            var bClose = new Chip("✕") { AccentOf = x => x.Muted };
            bClose.Location = new Point(W - pad - bClose.Width, Theme.S(14));
            bTheme.Location = new Point(bClose.Left - Theme.S(6) - bTheme.Width, Theme.S(14));
            bTheme.Click += (s, e) => ctx.ToggleTheme();
            bClose.Click += (s, e) => Hide();
            Controls.Add(bTheme); Controls.Add(bClose);

            if (busy)
            {
                var lb = new Label { Text = "[ application en cours… ]", ForeColor = t.Warn, Font = new Font("Consolas", 9f, FontStyle.Bold), AutoSize = true, Location = new Point(pad, y) };
                Controls.Add(lb); y += Theme.S(26);
            }

            if (adapters.Count == 0)
            {
                Controls.Add(new Label { Text = "Aucune carte Wi-Fi / Ethernet détectée.", ForeColor = t.Muted, AutoSize = true, Location = new Point(pad, y) });
                y += Theme.S(30);
            }

            foreach (var a in adapters)
            {
                var card = new Card { Location = new Point(pad, y), Width = inner };
                card.Status = !a.IsUp ? t.Muted : a.Dhcp ? t.Ok : t.Accent;
                Color cardBg = t.Surface2;

                var title = new Label
                {
                    Text = a.Kind.ToUpperInvariant() + "  ·  " + a.Name,
                    ForeColor = t.Text, BackColor = cardBg, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f),
                    Location = new Point(Theme.S(16), Theme.S(10))
                };
                card.Controls.Add(title);

                var state = new Label
                {
                    Text = a.IsUp ? "● EN LIGNE" : "○ DÉCONNECTÉE", ForeColor = a.IsUp ? t.Ok : t.Muted, BackColor = cardBg,
                    AutoSize = true, Font = new Font("Consolas", 8.5f, FontStyle.Bold)
                };
                card.Controls.Add(state);
                state.Location = new Point(inner - state.PreferredWidth - Theme.S(12), Theme.S(13));

                string ip = a.Ips.FirstOrDefault(i => !i.StartsWith("169.254.")) ?? a.Ips.FirstOrDefault() ?? "pas d'IP";
                var info = new Label
                {
                    Text = (a.Dhcp ? "DHCP" : "STATIQUE") + "  " + ip + (a.Gateway.Length > 0 ? "   GW " + a.Gateway : "") +
                           (a.DuplicateIps.Count > 0 ? "   ⚠ CONFLIT" : ""),
                    ForeColor = a.DuplicateIps.Count > 0 ? t.Bad : (a.Dhcp ? t.Ok : t.Accent), BackColor = cardBg,
                    AutoSize = true, Font = new Font("Consolas", 9f), Location = new Point(Theme.S(17), Theme.S(36))
                };
                card.Controls.Add(info);

                // puces d'action
                var chips = new List<Chip>();
                string name = a.Name;
                var cD = new Chip("DHCP") { Active = a.Dhcp, AccentOf = x => x.Ok, Enabled = !busy };
                cD.Click += (s, e) => { Hide(); ctx.ApplyDhcp(name); };
                chips.Add(cD);
                foreach (int p in cfg.Presets)
                {
                    int third = p;
                    string pip = cfg.Base + "." + third + "." + cfg.HostOctet;
                    var c = new Chip(pip) { Active = !a.Dhcp && a.RawIps.Contains(pip), Enabled = !busy };
                    c.Click += (s, e) => { Hide(); ctx.ApplyRange(name, third, cfg.HostOctet); };
                    chips.Add(c);
                }
                var cO = new Chip("Autre plage…") { AccentOf = x => x.Accent2, Enabled = !busy };
                cO.Click += (s, e) => { Hide(); ctx.AskOtherRange(name); };
                chips.Add(cO);
                var cP = new Chip("Personnalisée…") { AccentOf = x => x.Accent2, Enabled = !busy };
                cP.Click += (s, e) => { Hide(); ctx.AskCustom(name); };
                chips.Add(cP);
                var cI = new Chip("Détails") { AccentOf = x => x.Muted };
                AdapterInfo snap = a;
                cI.Click += (s, e) => { Hide(); ctx.ShowDetails(snap); };
                chips.Add(cI);

                int cx = Theme.S(14), cy = Theme.S(64), gap = Theme.S(6), maxX = inner - Theme.S(12);
                foreach (var c in chips)
                {
                    if (cx + c.Width > maxX && cx > Theme.S(14)) { cx = Theme.S(14); cy += c.Height + gap; }
                    c.Location = new Point(cx, cy);
                    card.Controls.Add(c);
                    cx += c.Width + gap;
                }
                card.Height = cy + Theme.S(30) + Theme.S(14);
                Controls.Add(card);
                y += card.Height + Theme.S(10);
            }

            // pied
            y += Theme.S(4);
            var sign = new Label { Text = "Développé by alex.b · v" + Program.Version, ForeColor = t.Muted, AutoSize = true, Font = new Font("Consolas", 8.5f), Location = new Point(pad, y + Theme.S(8)) };
            Controls.Add(sign);
            var bQuit = new Chip("Quitter") { AccentOf = x => x.Bad };
            var bSet = new Chip("⚙ Paramètres") { AccentOf = x => x.Muted };
            bQuit.Location = new Point(W - pad - bQuit.Width, y);
            bSet.Location = new Point(bQuit.Left - Theme.S(6) - bSet.Width, y);
            bQuit.Click += (s, e) => ctx.Quit();
            bSet.Click += (s, e) => { Hide(); ctx.AskSettings(); };
            Controls.Add(bSet); Controls.Add(bQuit);
            y += Theme.S(30) + pad;

            ClientSize = new Size(W, y);
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Right - W - Theme.S(12), wa.Bottom - y - Theme.S(12));
            ResumeLayout();
            Invalidate();
            if (IsHandleCreated) Dwm.Style(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.K; var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(t.Bg);
            if (t.Dark) using (var p = new Pen(Color.FromArgb(12, t.Accent))) for (float x = 0; x < Width; x += 22 * k) g.DrawLine(p, x, 0, x, 60 * k);
            using (var f = new Font("Consolas", 15f, FontStyle.Bold)) using (var b = new SolidBrush(t.Accent))
                g.DrawString("> NETSWITCH_", f, b, 12 * k, 10 * k);
            using (var f = new Font("Consolas", 8f)) using (var b = new SolidBrush(t.Muted))
                g.DrawString("DHCP ⇄ IP statique  //  by alex.b", f, b, 15 * k, 40 * k);
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), 1), t.Accent, t.Accent2, 0f))
                g.FillRectangle(lg, 0, 60 * k, Width, 2 * k);
            using (var p = new Pen(Color.FromArgb(t.Dark ? 160 : 255, t.Accent), 1f)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    // ===================================================================== Application (icône près de l'horloge)
    class TrayContext : ApplicationContext
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool DestroyIcon(IntPtr hIcon);

        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly Control invoker;
        readonly Flyout flyout;
        readonly Timer pinTimer;
        Config cfg;
        bool busy, autoStart;
        int pinTries;
        IntPtr iconHandle = IntPtr.Zero;

        public TrayContext()
        {
            cfg = Config.Load();
            Theme.Current = Theme.Make(cfg.DarkTheme);
            invoker = new Control();
            var forceHandle = invoker.Handle;

            flyout = new Flyout(this);
            menu = new ContextMenuStrip { Renderer = new CyberRenderer(), ShowCheckMargin = true, ShowImageMargin = false };
            menu.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };

            tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "NetSwitch by alex.b" };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };

            NetworkChange.NetworkAddressChanged += (s, e) => SafeInvoke(Refresh);
            NetworkChange.NetworkAvailabilityChanged += (s, e) => SafeInvoke(Refresh);

            pinTimer = new Timer { Interval = 4000 };
            pinTimer.Tick += (s, e) => { TrayPin.Promote(); if (++pinTries >= 6) pinTimer.Stop(); pinTimer.Interval = 15000; };
            pinTimer.Start();

            Task.Run(() => { bool on = AutoStart.IsEnabled(); SafeInvoke(() => autoStart = on); });
            UpdateIcon();
            if (!cfg.TipShown)
            {
                tray.ShowBalloonTip(4000, "NetSwitch actif — by alex.b", "Clic sur l'icône près de l'horloge pour ouvrir le panneau.", ToolTipIcon.Info);
                cfg.TipShown = true; cfg.Save();
            }
            Logger.Write("Démarrage NetSwitch " + Program.Version);
        }

        void SafeInvoke(Action a) { try { if (invoker.IsHandleCreated) invoker.BeginInvoke(a); } catch { } }

        void Refresh()
        {
            UpdateIcon();
            if (flyout.Visible) flyout.Build(NetOps.GetAdapters(), cfg, busy);
        }

        void ToggleFlyout()
        {
            if (flyout.Visible) { flyout.Hide(); return; }
            if ((DateTime.Now - flyout.HiddenAt).TotalMilliseconds < 300) return;   // clic qui vient de le fermer
            flyout.Build(NetOps.GetAdapters(), cfg, busy);
            flyout.Show();
            flyout.Activate();
        }

        public void ToggleTheme()
        {
            cfg.DarkTheme = !cfg.DarkTheme; cfg.Save();
            Theme.Current = Theme.Make(cfg.DarkTheme);
            if (flyout.Visible) { flyout.Build(NetOps.GetAdapters(), cfg, busy); flyout.Activate(); }
        }

        // ------------------------------------------------------------- icône néon
        void SetIcon(Color c, string letter)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(Color.FromArgb(7, 10, 17))) g.FillEllipse(b, 1, 1, 30, 30);
                    using (var p = new Pen(c, 3f)) g.DrawEllipse(p, 2.5f, 2.5f, 27, 27);
                    using (var f = new Font("Consolas", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    using (var br = new SolidBrush(c))
                        g.DrawString(letter, f, br, new RectangleF(0, 1, 32, 32), sf);
                }
                IntPtr h = bmp.GetHicon();
                var old = tray.Icon;
                tray.Icon = Icon.FromHandle(h);
                if (old != null) old.Dispose();
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                iconHandle = h;
            }
        }

        void UpdateIcon()
        {
            var n = Theme.Make(true);
            if (busy) { SetIcon(n.Warn, "…"); SetText("NetSwitch — application en cours…"); return; }
            var up = NetOps.GetAdapters().Where(a => a.IsUp).ToList();
            if (up.Count == 0) SetIcon(n.Muted, "–");
            else if (up.Any(a => !a.Dhcp)) SetIcon(n.Accent, "S");
            else SetIcon(n.Ok, "D");

            var lines = new List<string> { "NetSwitch by alex.b" };
            foreach (var a in up)
            {
                string ip = a.RawIps.FirstOrDefault(i => !i.StartsWith("169.254.")) ?? "pas d'IP";
                lines.Add(a.Kind + " : " + (a.Dhcp ? "DHCP " : "Fixe ") + ip);
            }
            if (up.Count == 0) lines.Add("Aucune carte connectée");
            SetText(string.Join("\n", lines));
        }

        void SetText(string t) { tray.Text = t.Length > 63 ? t.Substring(0, 63) : t; }

        // ------------------------------------------------------------- menu clic droit
        void BuildMenu()
        {
            foreach (ToolStripItem it in menu.Items.Cast<ToolStripItem>().ToList()) it.Dispose();
            menu.Items.Clear();
            menu.Renderer = new CyberRenderer();

            var head = new ToolStripMenuItem("NETSWITCH  ·  by alex.b") { Enabled = false, Font = new Font("Consolas", 9.5f, FontStyle.Bold) };
            menu.Items.Add(head);
            menu.Items.Add(new ToolStripSeparator());
            Add("Ouvrir le panneau", ToggleFlyout);

            foreach (var a in NetOps.GetAdapters())
            {
                var root = new ToolStripMenuItem(a.Kind + " « " + a.Name + " » — " + a.StateText);
                string name = a.Name;
                var d = new ToolStripMenuItem("DHCP (automatique)") { Checked = a.Dhcp, Enabled = !busy };
                d.Click += (s, e) => ApplyDhcp(name);
                root.DropDownItems.Add(d);
                foreach (int p in cfg.Presets)
                {
                    int third = p; string ip = cfg.Base + "." + third + "." + cfg.HostOctet;
                    var it = new ToolStripMenuItem("Statique " + ip) { Checked = !a.Dhcp && a.RawIps.Contains(ip), Enabled = !busy };
                    it.Click += (s, e) => ApplyRange(name, third, cfg.HostOctet);
                    root.DropDownItems.Add(it);
                }
                var o = new ToolStripMenuItem("Autre plage…") { Enabled = !busy }; o.Click += (s, e) => AskOtherRange(name); root.DropDownItems.Add(o);
                var c = new ToolStripMenuItem("Personnalisée…") { Enabled = !busy }; c.Click += (s, e) => AskCustom(name); root.DropDownItems.Add(c);
                AdapterInfo snap = a;
                var det = new ToolStripMenuItem("Détails…"); det.Click += (s, e) => ShowDetails(snap); root.DropDownItems.Add(det);
                menu.Items.Add(root);
            }

            menu.Items.Add(new ToolStripSeparator());
            var th = new ToolStripMenuItem("Thème sombre") { Checked = Theme.Current.Dark };
            th.Click += (s, e) => ToggleTheme();
            menu.Items.Add(th);
            Add("Paramètres des plages…", AskSettings);
            Add("Connexions réseau Windows (ncpa.cpl)", () => { try { Process.Start("ncpa.cpl"); } catch { } });
            var auto = new ToolStripMenuItem("Démarrer avec Windows") { Checked = autoStart };
            auto.Click += (s, e) =>
            {
                bool target = !autoStart;
                Task.Run(() =>
                {
                    bool ok = true;
                    if (target) ok = AutoStart.Enable(); else AutoStart.Disable();
                    bool now = AutoStart.IsEnabled();
                    SafeInvoke(() =>
                    {
                        autoStart = now;
                        if (!ok) tray.ShowBalloonTip(4000, "NetSwitch", "Impossible de créer la tâche de démarrage (voir le journal).", ToolTipIcon.Error);
                    });
                });
            };
            menu.Items.Add(auto);
            Add("Ouvrir le journal", () => { try { if (File.Exists(Logger.FilePath)) Process.Start("notepad.exe", "\"" + Logger.FilePath + "\""); } catch { } });
            menu.Items.Add(new ToolStripSeparator());
            Add("Quitter", Quit);
        }

        void Add(string text, Action a)
        {
            var it = new ToolStripMenuItem(text);
            it.Click += (s, e) => a();
            menu.Items.Add(it);
        }

        // ------------------------------------------------------------- actions
        void RunOp(string title, Func<OpResult> op)
        {
            if (busy) { tray.ShowBalloonTip(2000, "NetSwitch", "Une modification est déjà en cours.", ToolTipIcon.Warning); return; }
            busy = true;
            UpdateIcon();
            Logger.Write(">>> " + title);
            Task.Run(() =>
            {
                OpResult r;
                try { r = op(); } catch (Exception ex) { r = OpResult.Fail(ex.Message); }
                Logger.Write((r.Ok ? "OK  " : "ERR ") + r.Message.Replace("\n", " | "));
                SafeInvoke(() =>
                {
                    busy = false;
                    Refresh();
                    tray.ShowBalloonTip(5000, title, string.IsNullOrEmpty(r.Message) ? "Terminé" : r.Message, r.Ok ? ToolTipIcon.Info : ToolTipIcon.Error);
                });
            });
        }

        public void ApplyDhcp(string name) { RunOp("DHCP — " + name, () => NetOps.SetDhcp(name)); }

        public void ApplyRange(string name, int third, int host)
        {
            string ip = cfg.Base + "." + third + "." + host;
            string gw = cfg.Base + "." + third + "." + cfg.GatewayOctet;
            var dns = new List<string> { gw };
            if (!string.IsNullOrEmpty(cfg.Dns2)) dns.Add(cfg.Dns2);
            int prefix = cfg.Prefix;
            RunOp("Statique " + ip + " — " + name, () => NetOps.SetStatic(name, ip, prefix, gw, dns));
        }

        public void AskOtherRange(string name)
        {
            var r = InputDialog.Ask("Autre plage — " + name,
                "IP = " + cfg.Base + ".X.Y   Passerelle = " + cfg.Base + ".X." + cfg.GatewayOctet + "   /" + cfg.Prefix,
                new[] { "Plage X (3e octet, 0-255)", "Dernier octet Y (1-254)" }, new[] { "", cfg.HostOctet.ToString() });
            if (r == null) return;
            int x, y;
            if (!int.TryParse(r[0], out x) || x < 0 || x > 255) { Error("Plage invalide : " + r[0]); return; }
            if (!int.TryParse(r[1], out y) || y < 1 || y > 254) { Error("Dernier octet invalide : " + r[1]); return; }
            if (y == cfg.GatewayOctet) { Error("Le dernier octet est celui de la passerelle (" + y + ")."); return; }
            ApplyRange(name, x, y);
        }

        public void AskCustom(string name)
        {
            string defIp = cfg.Base + "." + (cfg.Presets.Count > 0 ? cfg.Presets[0] : 1) + "." + cfg.HostOctet;
            var p = defIp.Split('.');
            string defGw = p[0] + "." + p[1] + "." + p[2] + "." + cfg.GatewayOctet;
            string defDns = defGw + (string.IsNullOrEmpty(cfg.Dns2) ? "" : ", " + cfg.Dns2);
            var r = InputDialog.Ask("IP personnalisée — " + name,
                "Préfixe : 24 ou 255.255.255.0. Passerelle vide = aucune. DNS séparés par des virgules.",
                new[] { "Adresse IP", "Préfixe / masque", "Passerelle", "DNS" }, new[] { defIp, cfg.Prefix.ToString(), defGw, defDns });
            if (r == null) return;
            int prefix;
            if (!Validate.TryParsePrefix(r[1], out prefix)) { Error("Préfixe / masque invalide : " + r[1]); return; }
            var dns = r[3].Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).ToList();
            string err = Validate.CheckStatic(r[0], prefix, r[2], dns);
            if (err != null) { Error(err); return; }
            string ip = r[0], gw = r[2];
            RunOp("Statique " + ip + " — " + name, () => NetOps.SetStatic(name, ip, prefix, gw, dns));
        }

        public void AskSettings()
        {
            var r = InputDialog.Ask("Paramètres des plages",
                "IP statique = Base.<plage>.Hôte. Les plages listées apparaissent directement dans le panneau.",
                new[] { "Base (2 octets)", "Plages (ex : 1,2,10)", "Dernier octet de l'IP", "Dernier octet passerelle", "Préfixe (/24 …)", "DNS secondaire" },
                new[] { cfg.Base, string.Join(",", cfg.Presets), cfg.HostOctet.ToString(), cfg.GatewayOctet.ToString(), cfg.Prefix.ToString(), cfg.Dns2 });
            if (r == null) return;
            List<int> presets; int host, gwo, pre;
            if (!Validate.IsBase(r[0])) { Error("Base invalide (format attendu : 192.168)."); return; }
            if (!Validate.TryParsePresets(r[1], out presets)) { Error("Plages invalides (nombres 0-255 séparés par des virgules, 12 max)."); return; }
            if (!int.TryParse(r[2], out host) || host < 1 || host > 254) { Error("Dernier octet de l'IP invalide."); return; }
            if (!int.TryParse(r[3], out gwo) || gwo < 1 || gwo > 254) { Error("Dernier octet de la passerelle invalide."); return; }
            if (host == gwo) { Error("L'IP et la passerelle auraient le même dernier octet."); return; }
            if (!int.TryParse(r[4].TrimStart('/'), out pre) || pre < 8 || pre > 30) { Error("Préfixe invalide (8 à 30)."); return; }
            if (r[5].Length > 0 && !Validate.IsIPv4(r[5])) { Error("DNS secondaire invalide."); return; }
            cfg.Base = r[0]; cfg.Presets = presets; cfg.HostOctet = host; cfg.GatewayOctet = gwo; cfg.Prefix = pre; cfg.Dns2 = r[5];
            cfg.Save();
            tray.ShowBalloonTip(2500, "NetSwitch", "Paramètres enregistrés.", ToolTipIcon.Info);
        }

        public void ShowDetails(AdapterInfo a)
        {
            var now = NetOps.Find(a.Name) ?? a;
            var sb = new StringBuilder();
            sb.AppendLine("Nom ........ " + now.Name);
            sb.AppendLine("Type ....... " + now.Kind);
            sb.AppendLine("Carte ...... " + now.Description);
            sb.AppendLine("MAC ........ " + now.Mac);
            sb.AppendLine("État ....... " + (now.IsUp ? "connectée" : "déconnectée"));
            sb.AppendLine("Mode ....... " + now.Mode);
            sb.AppendLine("IPv4 ....... " + (now.Ips.Count > 0 ? string.Join(", ", now.Ips) : "aucune"));
            sb.AppendLine("Passerelle . " + (now.Gateway.Length > 0 ? now.Gateway : "aucune"));
            sb.Append("DNS ........ " + (now.Dns.Count > 0 ? string.Join(", ", now.Dns) : "aucun"));
            if (now.DuplicateIps.Count > 0) sb.Append("\nCONFLIT IP . " + string.Join(", ", now.DuplicateIps));
            InfoDialog.Show("Détails — " + now.Name, sb.ToString(), false);
        }

        void Error(string msg) { InfoDialog.Show("NetSwitch", msg, true); }

        public void Quit()
        {
            Logger.Write("Arrêt NetSwitch");
            flyout.Hide();
            tray.Visible = false;
            tray.Dispose();
            if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
            ExitThread();
        }
    }
}
