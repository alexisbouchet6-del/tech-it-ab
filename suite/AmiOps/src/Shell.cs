// Tech IT AB - point d'entrée, fenêtre principale, navigation, palette Ctrl+K, composants communs
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

namespace AmiOps
{
    static class Program
    {
        public static bool Secours, StartHidden;   // lancé depuis la clé WinPE ou avec --secours
        public static bool IsWinPE { get { try { using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT")) return k != null; } catch { return false; } } }

        // Le .NET réduit de WinPE peut ne pas contenir System.Web.Extensions : le mode secours doit démarrer quand même
#if !POSTE
        // Tech IT AB remplace AMI·OPS : première ouverture = copie des données AMI·OPS (clients, interventions, coffre, réglages)
        static void Migrate()
        {
            try
            {
                var oldDir = Path.Combine(Path.GetDirectoryName(AppInfo.DataDir), "AmiOps");
                if (!Directory.Exists(oldDir) || File.Exists(Path.Combine(AppInfo.DataDir, "config.ini")) || Directory.Exists(Path.Combine(AppInfo.DataDir, "donnees"))) return;
                foreach (var d in Directory.GetDirectories(oldDir, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(oldDir, AppInfo.DataDir));
                foreach (var f in Directory.GetFiles(oldDir, "*", SearchOption.AllDirectories)) { var dst = f.Replace(oldDir, AppInfo.DataDir); if (!File.Exists(dst)) File.Copy(f, dst); }
                File.WriteAllText(Path.Combine(AppInfo.DataDir, "migration-amiops.txt"), "Données AMI·OPS copiées depuis " + oldDir + " le " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " (l'original est conservé).");
            }
            catch (Exception ex) { Log.Write("Migration AMI·OPS : " + ex.Message); }
        }
#endif

        static void SafeLoad()
        {
            try { Store.Load(); }
            catch (Exception ex) { Log.Write("Données non chargées (" + ex.GetType().Name + ") : " + ex.Message); Store.D = new Data(); }
        }

        [STAThread]
        static void Main(string[] args)
        {
            AppInfo.Name = Ed.Name; AppInfo.Id = Ed.Id; AppInfo.Version = Ed.Version; AppInfo.MachineWide = false;
#if !POSTE
            Migrate();
#endif
            #if POSTE
            Secours = false;
#else
            Secours = args.Any(a => a.Equals("--secours", StringComparison.OrdinalIgnoreCase)) || IsWinPE;
#endif
            // « Annuler la tâche » arrête réellement le programme en cours de la tâche (et ses enfants)
            Proc.Token = () => Job.Current != null ? Job.Current.Cts.Token : System.Threading.CancellationToken.None;
            StartHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            // --selftest [fichier] : complet ; --selftest-ci [fichier] : sans Windows Update ni SMART (machines de CI)
            int st = Array.FindIndex(args, a => a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase));
            if (st >= 0) { Environment.Exit(SelfTest.Headless(st + 1 < args.Length ? args[st + 1] : null, !args[st].EndsWith("-ci", StringComparison.OrdinalIgnoreCase))); return; }
            try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072 | (System.Net.SecurityProtocolType)12288; } catch { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072; }
            Boot.Run(() => { SafeLoad(); return new MainForm(); }, true, Ed.Name + " diagnostique et répare les postes : droits administrateur nécessaires.");
        }
    }

    // ===================================================================== Presse-papiers privé (hors historique Win+V et cloud)
    static class Clip
    {
        static Timer clear;
        public static void Private(string text, int seconds)
        {
            if (string.IsNullOrEmpty(text)) return;
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
            try { Clipboard.SetDataObject(data, false); } catch { return; }
            if (clear != null) clear.Dispose();
            clear = new Timer { Interval = seconds * 1000 };
            clear.Tick += (s, e) => { clear.Stop(); try { if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear(); } catch { } };
            clear.Start();
        }
        public static void Text(string text) { if (!string.IsNullOrEmpty(text)) try { Clipboard.SetText(text); } catch { } }
    }

    // ===================================================================== Boîte de saisie générique
    class F
    {
        public string Label, Value = "";
        public bool Multi, Password, Required;
        public string[] Options;   // liste déroulante (modifiable si Editable)
        public bool Editable = true;
        public int Lines = 3;
        public F(string label, string value) { Label = label; Value = value ?? ""; }
    }

    static class Dlg
    {
        public static bool Edit(IWin32Window owner, string title, params F[] fields)
        {
            Func<float, int> k = Theme.S;
            using (var f = new Form { Text = title + " · by alex.b", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, Font = new Font("Segoe UI", 9.5f), BackColor = Theme.Current.Bg, ShowInTaskbar = false })
            {
                int y = k(12), w = k(520); var ctrls = new List<Control>();
                foreach (var fd in fields)
                {
                    f.Controls.Add(new Label { Text = fd.Label + (fd.Required ? " *" : ""), AutoSize = true, Location = new Point(k(14), y), Tag = "muted" });
                    y += k(18);
                    Control c;
                    if (fd.Options != null)
                    {
                        var cb = new ComboBox { DropDownStyle = fd.Editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList, Width = w };
                        cb.Items.AddRange(fd.Options);
                        if (fd.Editable) cb.Text = fd.Value; else { int i = Array.IndexOf(fd.Options, fd.Value); cb.SelectedIndex = i >= 0 ? i : (fd.Options.Length > 0 ? 0 : -1); }
                        c = cb;
                    }
                    else c = new TextBox { Text = fd.Value, Width = w, Multiline = fd.Multi, ScrollBars = fd.Multi ? ScrollBars.Vertical : ScrollBars.None, AcceptsReturn = fd.Multi, Height = fd.Multi ? k(20 * fd.Lines) : k(24), UseSystemPasswordChar = fd.Password };
                    c.Location = new Point(k(14), y); f.Controls.Add(c); ctrls.Add(c);
                    y += c.Height + k(10);
                }
                var ok = new Button { Text = "Enregistrer", DialogResult = DialogResult.OK, Size = new Size(k(110), k(30)), Location = new Point(k(14) + w - k(226), y + k(4)) };
                var ca = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Size = new Size(k(110), k(30)), Location = new Point(k(14) + w - k(110), y + k(4)) };
                f.Controls.Add(ok); f.Controls.Add(ca); f.AcceptButton = fields.Any(x => x.Multi) ? null : ok; f.CancelButton = ca;
                f.ClientSize = new Size(w + k(28), y + k(48));
                CyberForm.Recolor(f);
                foreach (var b in new[] { ok, ca }) { b.FlatStyle = FlatStyle.Flat; b.BackColor = Theme.Current.Surface2; b.ForeColor = Theme.Current.Text; b.FlatAppearance.BorderColor = Theme.Current.Accent; }
                while (true)
                {
                    if (f.ShowDialog(owner) != DialogResult.OK) return false;
                    for (int i = 0; i < fields.Length; i++) fields[i].Value = (ctrls[i].Text ?? "").Trim();
                    var miss = fields.FirstOrDefault(x => x.Required && x.Value.Length == 0);
                    if (miss == null) return true;
                    CyberForm.Warn(owner, "Champ obligatoire : " + miss.Label);
                }
            }
        }

        public static string Ask(IWin32Window owner, string title, string label, string value, bool password)
        {
            var fd = new F(label, value) { Password = password, Required = true };
            return Edit(owner, title, fd) ? fd.Value : null;
        }
    }

    // ===================================================================== Liste au thème (en-têtes dessinés)
    class Grid : ListView
    {
        public Grid()
        {
            View = View.Details; FullRowSelect = true; HideSelection = false; BorderStyle = BorderStyle.None; OwnerDraw = true;
            Font = new Font("Segoe UI", 9f); DoubleBuffered = true;
        }
        public Grid Col(string name, int px) { Columns.Add(name, Theme.S(px)); return this; }
        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            var t = Theme.Current;
            using (var b = new SolidBrush(t.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(t.Border)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            using (var f = new Font("Consolas", 8.5f, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), f, Rectangle.Inflate(e.Bounds, -6, 0), t.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
        protected override void OnDrawItem(DrawListViewItemEventArgs e) { e.DrawDefault = true; }
        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e) { e.DrawDefault = true; }
        public object Sel { get { return SelectedItems.Count > 0 ? SelectedItems[0].Tag : null; } }
        public void Fit() { if (Columns.Count == 0) return; int used = 0; for (int i = 0; i < Columns.Count - 1; i++) used += Columns[i].Width; Columns[Columns.Count - 1].Width = Math.Max(Theme.S(80), ClientSize.Width - used - 2); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
    }

    // ===================================================================== Carte statistique
    class Stat : CyberControl
    {
        public string Value = "—", Caption = "", Sub = "";
        public Func<Theme, Color> AccentOf = t => t.Accent;
        public Stat() { Cursor = Cursors.Hand; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.K; var g = Prep(e); var acc = AccentOf(t);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var path = Theme.Round(r, 10 * k))
            {
                using (var b = new SolidBrush(Theme.Blend(t.Surface2, acc, hover ? 0.12 : 0.04))) g.FillPath(b, path);
                using (var p = new Pen(Color.FromArgb(hover ? 230 : 110, acc), k)) g.DrawPath(p, path);
            }
            using (var bar = new SolidBrush(acc)) g.FillRectangle(bar, r.X + 10 * k, r.Y, 36 * k, 3 * k);
            using (var f = new Font("Consolas", 22f, FontStyle.Bold)) using (var b = new SolidBrush(acc)) g.DrawString(Value, f, b, 12 * k, 12 * k);
            using (var f = new Font("Segoe UI Semibold", 9f)) using (var b = new SolidBrush(t.Text)) g.DrawString(Caption, f, b, 14 * k, Height - 40 * k);
            using (var f = new Font("Consolas", 7.5f)) using (var b = new SolidBrush(t.Muted)) g.DrawString(Sub, f, b, 14 * k, Height - 22 * k);
        }
    }

    // ===================================================================== Bouton de navigation
    class Nav : CyberControl
    {
        public string Glyph, Caption, Key; public bool Active; public string Badge = "";
        public Nav(string key, string glyph, string caption) { Key = key; Glyph = glyph; Caption = caption; Height = Theme.S(30); Cursor = Cursors.Hand; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current; float k = Theme.K; var g = Prep(e);
            var r = new RectangleF(6 * k, 2 * k, Width - 12 * k, Height - 4 * k);
            if (Active || hover)
                using (var path = Theme.Round(r, 8 * k)) using (var b = new SolidBrush(Theme.Blend(t.Surface, t.Accent, Active ? 0.18 : 0.08))) g.FillPath(b, path);
            if (Active) using (var b = new SolidBrush(t.Accent)) g.FillRectangle(b, 0, r.Y + 8 * k, 3 * k, r.Height - 16 * k);
            var col = Active ? t.Accent : t.Text;
            using (var f = Theme.IconFont(12f)) using (var b = new SolidBrush(col)) using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center })
                g.DrawString(Glyph, f, b, new RectangleF(14 * k, 0, 24 * k, Height), sf);
            using (var f = new Font("Segoe UI", 9.5f, Active ? FontStyle.Bold : FontStyle.Regular)) using (var b = new SolidBrush(col)) using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
                g.DrawString(Caption, f, b, new RectangleF(46 * k, 0, Width - 46 * k, Height), sf);
            if (Badge.Length > 0)
                using (var f = new Font("Consolas", 7.5f, FontStyle.Bold))
                {
                    var sz = g.MeasureString(Badge, f); var br = new RectangleF(Width - sz.Width - 22 * k, Height / 2f - 9 * k, sz.Width + 10 * k, 18 * k);
                    using (var path = Theme.Round(br, 9 * k)) using (var b = new SolidBrush(t.Bad)) g.FillPath(b, path);
                    using (var b = new SolidBrush(Color.White)) g.DrawString(Badge, f, b, br.X + 5 * k, br.Y + 2 * k);
                }
        }
    }

    // ===================================================================== Page
    abstract class Page : Panel
    {
        public MainForm Shell;
        protected readonly Func<float, int> k = Theme.S;
        public abstract string Title { get; }
        public virtual void OnShow() { }
        public virtual void Open(object arg) { }
        protected Page() { Dock = DockStyle.Fill; DoubleBuffered = true; }
        protected Chip Btn(string text, Func<Theme, Color> acc, int x, int y, EventHandler click, bool primary = false)
        {
            var c = new Chip(text) { AccentOf = acc, Location = new Point(x, y), Primary = primary };
            if (click != null) c.Click += click;
            Controls.Add(c); return c;
        }
        protected Label Lbl(string text, int x, int y, string tag, float size = 9f, bool bold = false)
        {
            var l = new Label { Text = text, AutoSize = true, Location = new Point(x, y), Tag = tag, Font = new Font(size >= 12 ? "Consolas" : "Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
            Controls.Add(l); return l;
        }
        protected void Run(Action a) { try { a(); } catch (Exception ex) { Log.Write(ex.ToString()); CyberForm.Warn(this, "Erreur : " + ex.Message); } }
    }

    // ===================================================================== Palette de commandes (Ctrl+K)
    class Palette : Form
    {
        class Item { public string Text, Sub, Kind; public Action Go; }
        readonly TextBox box; readonly ListBox list; readonly List<Item> all = new List<Item>();

        public Palette(MainForm shell)
        {
            Func<float, int> k = Theme.S; var t = Theme.Current;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; ShowInTaskbar = false; KeyPreview = true;
            Size = new Size(k(680), k(440)); BackColor = t.Accent; Padding = new Padding(2);
            Location = new Point(shell.Left + (shell.Width - Width) / 2, shell.Top + k(110));
            var inner = new Panel { Dock = DockStyle.Fill, BackColor = t.Surface };
            box = new TextBox { Dock = DockStyle.Top, Font = new Font("Consolas", 14f), BorderStyle = BorderStyle.None, BackColor = t.Surface, ForeColor = t.Text };
            var pad = new Panel { Dock = DockStyle.Top, Height = k(48), Padding = new Padding(k(14), k(12), k(14), 0), BackColor = t.Surface }; pad.Controls.Add(box);
            list = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = k(40), BackColor = t.Surface, ForeColor = t.Text, IntegralHeight = false };
            var hint = new Label { Dock = DockStyle.Bottom, Height = k(22), Text = "  ↑↓ naviguer   ⏎ ouvrir   Échap fermer   ·   clients, équipements (IP, n° série, QuickConnect), interventions, actions", ForeColor = t.Muted, BackColor = t.Surface2, Font = new Font("Consolas", 7.5f), TextAlign = ContentAlignment.MiddleLeft };
            inner.Controls.Add(list); inner.Controls.Add(hint); inner.Controls.Add(pad); Controls.Add(inner);

            list.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return; var it = (Item)list.Items[e.Index]; var th = Theme.Current;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Theme.Blend(th.Surface, th.Accent, 0.2) : th.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
                if (sel) using (var b = new SolidBrush(th.Accent)) e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y + 6, 3, e.Bounds.Height - 12);
                using (var f = new Font("Consolas", 7.5f, FontStyle.Bold)) TextRenderer.DrawText(e.Graphics, it.Kind.ToUpperInvariant(), f, new Rectangle(e.Bounds.X + 12, e.Bounds.Y, 100, e.Bounds.Height), th.Accent2, TextFormatFlags.VerticalCenter);
                using (var f = new Font("Segoe UI Semibold", 10f)) TextRenderer.DrawText(e.Graphics, it.Text, f, new Rectangle(e.Bounds.X + 110, e.Bounds.Y + 2, e.Bounds.Width - 120, e.Bounds.Height / 2 + 2), th.Text, TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
                using (var f = new Font("Segoe UI", 8f)) TextRenderer.DrawText(e.Graphics, it.Sub, f, new Rectangle(e.Bounds.X + 110, e.Bounds.Y + e.Bounds.Height / 2 + 1, e.Bounds.Width - 120, e.Bounds.Height / 2 - 2), th.Muted, TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
            };

            // actions
            Action<string, string, Action> act = (txt, sub, go) => all.Add(new Item { Kind = "action", Text = txt, Sub = sub, Go = go });
#if !POSTE
            act("Nouvelle intervention", "Ctrl+N · chrono lancé automatiquement", () => shell.NewIntervention(null));
#endif
            act("PanneScan : analyser ce PC", "écrans bleus, arrêts brutaux, disque, RAM, pilotes", () => shell.Go("panne", "run"));
#if !POSTE
            act("Nouveau client", "fiche client", () => shell.Go("clients", "new"));
#endif
            act("Scanner le réseau", "inventaire + ajout à une fiche client", () => shell.Go("reseau", null));
#if !POSTE
            act("Vérifier tous les domaines", "SPF, DKIM, DMARC, certificats", () => shell.Go("mail", "all"));
#endif
#if !POSTE
            act("Coffre des identifiants", "AES-256", () => shell.Go("coffre", null));
#endif
#if !POSTE
            act("Mode secours", "PC qui ne démarre plus, disque branché en USB", () => shell.Go("secours", null));
#endif
#if !POSTE
            act("Temps passé", "par client, export", () => shell.Go("temps", null));
#endif
            act("Optimisation : bilan du poste", "note /10 par élément + préconisations", () => shell.Go("optim", null));
            act("Audit de sécurité", "note /100, corrections", () => shell.Go("secu", null));
            act("Écrans bleus : pilote en cause", "WinDbg", () => shell.Go("depann", null));
            act("Surveillance des coupures réseau", "box / Internet / DNS", () => shell.Go("netdiag", null));
#if !POSTE
            act("Compte AD verrouillé", "quel PC le bloque", () => shell.Go("ad", null));
#endif
            act(Ed.Full ? "Scénarios (PC lent, écran bleu, PC neuf…)" : "Scénarios (PC lent, écran bleu, réseau, jeu)", "étapes enchaînées en tâche de fond", () => { using (var sf = new ScenarioForm(shell)) sf.ShowDialog(shell); });
            act("Jeu : optimiser le PC pour les jeux", "analyse /100, pilotes, écran, applis, overclocking", () => shell.Go("jeu", null));
            act("Tâches en cours", "centre des tâches", () => shell.Go("taches", null));
#if !POSTE
            act("Multi-PC", "même action sur plusieurs PC (WinRM ou Tactical RMM)", () => shell.Go("multi", null));
#endif
#if !POSTE
            act("Connecteurs GLPI / Tactical RMM", "synchronisation, agents", () => shell.Go("connect", null));
            if (Authenticity.Trusted)
            {
                act("Cyber : scan Nmap", "périmètre autorisé du client", () => shell.Go("cyber", null));
                act("Cyber : chasse aux menaces", "journaux, persistance, connexions", () => shell.Go("cyber", null));
            }
            act("Parc client : recette de livraison", "contrôle du PC avant remise au client", () => shell.Go("parc", null));
            act("Parc client : état du parc", "GLPI + Tactical RMM", () => shell.Go("parc", null));
#endif
            act("Autotest de " + Ed.Name, "tous les modules en lecture seule", () => shell.RunSelfTest());
            act("Changer de thème", "sombre / clair", () => shell.ToggleTheme());
            foreach (var tool in Tools.All) { var tl = tool; all.Add(new Item { Kind = "outil", Text = tl.Name, Sub = tl.Hint, Go = () => tl.Launch(shell) }); }
#if !POSTE
            foreach (var c in Store.D.Clients)
            {
                var cc = c;
                all.Add(new Item { Kind = "client", Text = c.Name, Sub = string.Join(" · ", new[] { c.Address, c.Phone, c.Contract, string.Join(", ", c.Domains) }.Where(x => x.Length > 0)), Go = () => shell.Go("clients", cc.Id) });
                foreach (var a in c.Assets)
                    all.Add(new Item { Kind = a.Kind, Text = a.Label, Sub = cc.Name + " · " + string.Join(" · ", new[] { a.Ip, a.Mac, a.Serial, a.QcId, a.Model }.Where(x => x.Length > 0)), Go = () => shell.Go("clients", cc.Id) });
            }
            foreach (var i in Store.D.Interventions.OrderByDescending(x => x.Date).Take(400))
            {
                var ii = i;
                all.Add(new Item { Kind = "interv.", Text = Store.ClientName(i.ClientId) + " — " + i.Type + " — " + i.Date.ToString("dd/MM/yyyy"), Sub = (i.Ticket.Length > 0 ? "#" + i.Ticket + " · " : "") + Short(i.Demande), Go = () => shell.EditIntervention(ii) });
            }
#endif
            box.TextChanged += (s, e) => Filter();
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.Down && list.Items.Count > 0) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); e.Handled = true; }
                else if (e.KeyCode == Keys.Up && list.Items.Count > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Fire(); }
            };
            list.DoubleClick += (s, e) => Fire();
            Deactivate += (s, e) => Close();
            Filter();
        }

        static string Short(string s) { s = (s ?? "").Replace("\r", " ").Replace("\n", " "); return s.Length > 90 ? s.Substring(0, 90) + "…" : s; }

        void Filter()
        {
            var q = box.Text.Trim();
            var res = q.Length == 0 ? all.Where(i => i.Kind == "action").ToList() : all.Where(i => Store.Match(q, i.Text, i.Sub, i.Kind)).Take(60).ToList();
            list.BeginUpdate(); list.Items.Clear(); foreach (var r in res) list.Items.Add(r); list.EndUpdate();
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }

        void Fire()
        {
            var it = list.SelectedItem as Item; if (it == null) return;
            Close(); BeginInvokeSafe(it.Go);
        }
        void BeginInvokeSafe(Action a) { var o = Owner; if (o != null) o.BeginInvoke(a); else a(); }
    }

    // ===================================================================== Fenêtre principale
    class MainForm : CyberForm
    {
        readonly Panel side, host;
        readonly Dictionary<string, Page> pages = new Dictionary<string, Page>();
        readonly List<Nav> navs = new List<Nav>();
        readonly Label lRun, lVault, banner;
        string current;

        public MainForm() : base(Ed.Name, 1380, 880)
        {
            SetSubtitle((Program.Secours ? "MODE SECOURS  //  " : "") + Ed.Tagline + "  //  " + Environment.MachineName + "  //  Ctrl+K : tout rechercher  //  by alex.b");
            Func<float, int> k = Theme.S;
            side = new Panel { Dock = DockStyle.Left, Width = k(212) };
            host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(k(16), k(6), k(16), k(10)) };
            Body.Controls.Add(host); Body.Controls.Add(side);
            // bandeau : copie non autorisée (prioritaire), sinon poste supervisé — ajouté en dernier pour occuper tout le haut
            var warn = Authenticity.Banner; var bnr = warn ?? Superv.Banner;
            if (bnr != null)
            {
                banner = new Label { Dock = DockStyle.Top, Height = k(26), Text = "   " + bnr, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9f, FontStyle.Bold), AutoEllipsis = true,
                    ForeColor = warn != null ? Color.White : Theme.Current.Bg, BackColor = Authenticity.State == Authenticity.Tampered ? Theme.Current.Bad : warn != null ? Theme.Current.Warn : Theme.Current.Accent };
                Body.Controls.Add(banner);
            }

            var defs = new[]
            {
                new[] { "accueil", Theme.Icon("", "⌂"), "Accueil" },
                new[] { "taches", Theme.Icon("", "☰"), "Tâches" },
                new[] { "clients", Theme.Icon("", "☺"), "Clients" },
                new[] { "parc", Theme.Icon("", "▦"), "Parc client" },
                new[] { "interv", Theme.Icon("", "✎"), "Interventions" },
                new[] { "panne", Theme.Icon("", "⚡"), "PanneScan" },
                new[] { "optim", Theme.Icon("", "⚙"), "Optimisation" },
                new[] { "depann", Theme.Icon("", "⚒"), "Dépannage" },
                new[] { "reseau", Theme.Icon("", "⇄"), "Scan réseau" },
                new[] { "netdiag", Theme.Icon("", "≋"), "Diagnostic réseau" },
                new[] { "jeu", Theme.Icon("\uE7FC", "▶"), "Jeu" },
                new[] { "multi", Theme.Icon("", "⧉"), "Multi-PC" },
                new[] { "mail", Theme.Icon("", "✉"), "Messagerie" },
                new[] { "secu", Theme.Icon("", "⛨"), "Sécurité" },
                new[] { "cyber", Theme.Icon("", "◎"), "Cyber" },
                new[] { "ad", Theme.Icon("", "⌂"), "Serveur AD" },
                new[] { "coffre", Theme.Icon("", "🔒"), "Coffre" },
                new[] { "temps", Theme.Icon("", "◷"), "Temps passé" },
                new[] { "outils", Theme.Icon("", "⚒"), "Outils" },
                new[] { "secours", Theme.Icon("", "✚"), "Secours" },
                new[] { "connect", Theme.Icon("", "⇌"), "Connecteurs" },
                new[] { "params", Theme.Icon("", "⚙"), "Paramètres" },
            };
#if POSTE
            // PC Santé AB : santé et informatique du PC uniquement
            defs = defs.Where(d => new[] { "accueil", "taches", "panne", "optim", "depann", "reseau", "netdiag", "jeu", "secu", "outils", "params" }.Contains(d[0])).ToArray();
#endif
            // copie non authentique : le cyber/labo (Tech IT AB) est masqué (fonctions sensibles désactivées)
            if (!Authenticity.Trusted) defs = defs.Where(d => d[0] != "cyber").ToArray();
            int y = k(6);
            foreach (var d in defs)
            {
                var n = new Nav(d[0], d[1], d[2]) { Location = new Point(0, y), Width = k(212), Height = k(Ed.Full ? 27 : 30) };
                n.Click += (s, e) => Go(((Nav)s).Key, null);
                side.Controls.Add(n); navs.Add(n); y += k(Ed.Full ? 28 : 31);   // 24 onglets dans Tech IT AB : pas resserré
            }
            lRun = new Label { AutoSize = false, Location = new Point(k(14), y + k(14)), Size = new Size(k(190), k(60)), Tag = "ok", Font = new Font("Consolas", 8.5f, FontStyle.Bold), Cursor = Cursors.Hand };
#if !POSTE
            lRun.Click += (s, e) => { var r = Store.Running(); if (r != null) EditIntervention(r); };
#endif
            lVault = new Label { AutoSize = true, Location = new Point(k(14), y + k(78)), Tag = "muted", Font = new Font("Consolas", 8f) };
            var lKey = new Label { AutoSize = true, Location = new Point(k(14), y + k(98)), Tag = "muted", Font = new Font("Consolas", 8f), Text = Ed.Full ? "Ctrl+K  recherche\r\nCtrl+N  intervention\r\nCtrl+L  verrouiller" : "Ctrl+K  recherche" };
            side.Controls.AddRange(new Control[] { lRun, lVault, lKey });
            side.Paint += (s, e) => { using (var p = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height); };

#if POSTE
            Register(new PosteHome()); Register(new PannePage()); Register(new ReseauPage()); Register(new ToolsPage()); Register(new SettingsPage());
            Register(new OptimPage()); Register(new DepannPage()); Register(new NetDiagPage()); Register(new SecuPage()); Register(new TasksPage()); Register(new JeuPage());
#else
            Register(new HomePage()); Register(new ClientsPage()); Register(new InterventionsPage()); Register(new PannePage());
            Register(new ReseauPage()); Register(new MailPage()); Register(new VaultPage()); Register(new TempsPage());
            Register(new ToolsPage()); Register(new SecoursPage()); Register(new SettingsPage());
            Register(new OptimPage()); Register(new DepannPage()); Register(new NetDiagPage()); Register(new SecuPage()); Register(new AdPage());
            Register(new TasksPage()); Register(new MultiPage()); Register(new ConnectPage()); Register(new ParcPage()); Register(new CyberPage()); Register(new JeuPage());
#endif

            var tick = new Timer { Interval = 1000 };
            tick.Tick += (s, e) => Tick();
            tick.Start();
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.K) { e.SuppressKeyPress = true; ShowPalette(); }
#if !POSTE
                else if (e.Control && e.KeyCode == Keys.N) { e.SuppressKeyPress = true; NewIntervention(null); }
                else if (e.Control && e.KeyCode == Keys.L) { e.SuppressKeyPress = true; Vault.Lock(); Tick(); if (current == "coffre") pages[current].OnShow(); }
#endif
            };
            Store.Changed += () => { if (IsHandleCreated) BeginInvoke(new Action(Tick)); };
            Go(Program.Secours ? "secours" : "accueil", null);
            Tick();
            // supervision : un état par jour au démarrage (silencieux), si le poste est enrôlé, l'exe authentique et l'utilisateur consentant
            if (Superv.Active) System.Threading.Tasks.Task.Run(() => Superv.Heartbeat());
            TrayHost.Create(this);
            if (Program.StartHidden) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; Shown += (s, e) => { Hide(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; }; }
            FormClosing += (s, e) =>
            {
                if (!TrayHost.Quitting && e.CloseReason == CloseReason.UserClosing && Prefs.Ini.GetBool("Tray", true))
                {
                    e.Cancel = true; Hide();
                    if (Prefs.Ini.Get("TrayTip", "") != "1") { Notify(Ed.Name + " reste actif", "Près de l'horloge : réseau rapide, chrono, tâches. Clic droit > Quitter pour fermer."); Prefs.Ini.Set("TrayTip", "1"); Prefs.Save(); }
                    return;
                }
                if (TrayHost.Icon != null) { TrayHost.Icon.Visible = false; TrayHost.Icon.Dispose(); }
            };
            Jobs.Changed += () => { if (IsHandleCreated) try { BeginInvoke(new Action(Tick)); } catch { } };
#if !POSTE
            if (!Program.Secours && Prefs.Ini.GetBool("UpdateAuto", true) && Prefs.Ini.Get("UpdateLast", "") != DateTime.Today.ToString("yyyy-MM-dd"))
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { string u, sm, tag; var n = Updater.Check(out u, out sm, out tag); Prefs.Ini.Set("UpdateLast", DateTime.Today.ToString("yyyy-MM-dd")); Prefs.Save(); if (n != null) BeginInvoke(new Action(() => Notify("Mise à jour disponible", Ed.Name + " " + n + " : Connecteurs > Mise à jour de " + Ed.Name))); }
                    catch (Exception ex) { Log.Write("Vérification de mise à jour : " + ex.Message); }
                });
            if (!Program.Secours && Prefs.Ini.GetBool("InfraAuto", true) && (Glpi.Configured || Trmm.Configured) && Prefs.Ini.Get("InfraLast", "") != DateTime.Today.ToString("yyyy-MM-dd"))
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var bad = Infra.All().Where(r => r.Level != "ok").ToList(); Prefs.Ini.Set("InfraLast", DateTime.Today.ToString("yyyy-MM-dd")); Prefs.Save();
                        if (bad.Count > 0) BeginInvoke(new Action(() => Notify("Serveurs : " + bad.Count + " alerte(s)", string.Join("\n", bad.Select(r => new Uri(r.Url).Host + " : " + r.Detail)))));
                    }
                    catch (Exception ex) { Log.Write("Contrôle des serveurs : " + ex.Message); }
                });
#endif
        }

        public void Notify(string title, string text)
        {
            if (TrayHost.Icon == null || !TrayHost.Icon.Visible) return;
            TrayHost.Icon.ShowBalloonTip(5000, title, text.Length > 250 ? text.Substring(0, 250) : text, ToolTipIcon.Info);
        }

        public void RunSelfTest()
        {
            Go("taches", null);
            Jobs.Run("Autotest de " + Ed.Name, null, j =>
            {
                var l = SelfTest.Run(true, r => j.Log("[" + r.Status + "] " + r.Name + " — " + r.Detail, r.Status == "OK" ? "ok" : r.Status == "KO" ? "bad" : "muted"));
                var txt = SelfTest.Text(l); var file = Path.Combine(AppInfo.DataDir, "autotest-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt");
                File.WriteAllText(file, txt, new UTF8Encoding(true));
                j.Log("Rapport : " + file + " (copié dans le presse-papiers : envoie-le à Claude)", "ok");
                BeginInvoke(new Action(() => { Clip.Text(txt); Notify("Autotest terminé", l.Count(r => r.Status == "OK") + " OK · " + l.Count(r => r.Status == "KO") + " KO — rapport copié"); }));
            });
        }

        void Register(Page p) { p.Shell = this; p.Visible = false; host.Controls.Add(p); pages[KeyOf(p)] = p; }
        static string KeyOf(Page p)
        {
#if POSTE
            if (p is PosteHome) return "accueil"; if (p is PannePage) return "panne"; if (p is ReseauPage) return "reseau"; if (p is ToolsPage) return "outils";
            if (p is OptimPage) return "optim"; if (p is DepannPage) return "depann"; if (p is NetDiagPage) return "netdiag"; if (p is SecuPage) return "secu"; if (p is TasksPage) return "taches";
            if (p is JeuPage) return "jeu"; return "params";
#else
            if (p is HomePage) return "accueil"; if (p is ClientsPage) return "clients"; if (p is InterventionsPage) return "interv"; if (p is PannePage) return "panne";
            if (p is ReseauPage) return "reseau"; if (p is MailPage) return "mail"; if (p is VaultPage) return "coffre"; if (p is TempsPage) return "temps";
            if (p is ParcPage) return "parc"; if (p is CyberPage) return "cyber"; if (p is TasksPage) return "taches"; if (p is MultiPage) return "multi"; if (p is ConnectPage) return "connect"; if (p is OptimPage) return "optim"; if (p is DepannPage) return "depann"; if (p is NetDiagPage) return "netdiag"; if (p is SecuPage) return "secu"; if (p is AdPage) return "ad";
            if (p is ToolsPage) return "outils"; if (p is SecoursPage) return "secours"; if (p is JeuPage) return "jeu"; return "params";
#endif
        }

        public void Go(string key, object arg)
        {
            Page p; if (!pages.TryGetValue(key, out p)) return;
            foreach (var kv in pages) if (kv.Key != key) kv.Value.Visible = false;
            p.Visible = true; current = key;
            foreach (var n in navs) { n.Active = n.Key == key; n.Invalidate(); }
            Recolor(p);
            try { p.OnShow(); if (arg != null) p.Open(arg); } catch (Exception ex) { Log.Write(ex.ToString()); Warn(this, ex.Message); }
        }

        public void RefreshPage(string key) { Page p; if (pages.TryGetValue(key, out p) && p.Visible) p.OnShow(); }
        public void RefreshCurrent() { if (current != null) pages[current].OnShow(); }

        void ShowPalette() { using (var p = new Palette(this)) { p.Owner = this; p.ShowDialog(this); } }

        void Tick()
        {
            var r = Store.Running();
            lRun.Text = r != null ? "● EN COURS\r\n" + Store.ClientName(r.ClientId) + "\r\n" + ((int)r.Elapsed.TotalHours).ToString("00") + ":" + r.Elapsed.Minutes.ToString("00") + ":" + r.Elapsed.Seconds.ToString("00") : "";
            // verrouillage automatique du coffre
#if !POSTE
            int min = Prefs.Ini.GetInt("VaultLockMin", 5);
            if (Vault.Unlocked && (DateTime.Now - Vault.LastUse).TotalMinutes >= min) { Vault.Lock(); if (current == "coffre") pages["coffre"].OnShow(); }
            lVault.Text = Vault.Unlocked ? "coffre : ouvert" : Vault.Exists ? "coffre : verrouillé" : "coffre : non créé";
            lVault.Tag = Vault.Unlocked ? "warn" : "muted"; lVault.ForeColor = Theme.Current.ByName((string)lVault.Tag);
#endif
            var jn = navs.FirstOrDefault(n => n.Key == "taches"); var jb = Jobs.Running > 0 ? Jobs.Running.ToString() : "";
            if (jn != null && jn.Badge != jb) { jn.Badge = jb; jn.Invalidate(); }
            if (TrayHost.Icon != null) { var tip = Ed.Name + (r != null ? " · chrono " + Store.ClientName(r.ClientId) : "") + (Jobs.Running > 0 ? " · " + Jobs.Running + " tâche(s)" : ""); TrayHost.Icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; }
            var open = Store.D.Interventions.Count(i => !i.Done);
            var nav = navs.FirstOrDefault(n => n.Key == "interv"); var b = open > 0 ? open.ToString() : "";   // absent de PC Santé AB
            if (nav != null && nav.Badge != b) { nav.Badge = b; nav.Invalidate(); }
            var badMail = Store.D.Mail.Count(m => m.Color == "bad" && Store.D.Clients.Any(c => c.Domains.Contains(m.Domain)));
            var navm = navs.FirstOrDefault(n => n.Key == "mail"); var bm = badMail > 0 ? badMail.ToString() : "";
            if (navm != null && navm.Badge != bm) { navm.Badge = bm; navm.Invalidate(); }
        }

        public override void ApplyTheme()
        {
            base.ApplyTheme();
            foreach (var p in pages.Values) Recolor(p);
            side.Invalidate(true);
        }

        // ---------------------------------------------------------------- interventions (accessibles de partout)
#if !POSTE
        public void NewIntervention(string clientId, string diag = null)
        {
            var running = Store.Running();
            if (running != null && !Ask(this, "Une intervention est déjà en cours (" + Store.ClientName(running.ClientId) + ").\nLa mettre en pause et en démarrer une nouvelle ?")) { EditIntervention(running); return; }
            if (running != null) { running.Minutes = running.TotalMinutes; running.RunningSince = DateTime.MinValue; }
            var i = new Intervention { Id = Store.NewId(), ClientId = clientId ?? "", Date = DateTime.Today, RunningSince = DateTime.Now, Tech = Prefs.Ini.Get("Tech", ""), Diag = diag ?? "", Type = diag != null ? "Dépannage" : "Dépannage" };
            Store.D.Interventions.Add(i);
            Store.Save();
            EditIntervention(i);
        }

        public void EditIntervention(Intervention i)
        {
            using (var f = new InterventionForm(i)) f.ShowDialog(this);
            Store.Save();
            RefreshCurrent();
        }
#else
        // PC Santé AB : pas de fiche d'intervention, le diagnostic est enregistré en rapport texte et ouvert
        public void NewIntervention(string clientId, string diag = null)
        {
            if (string.IsNullOrEmpty(diag)) return;
            var f = Path.Combine(Report.Dir, DateTime.Now.ToString("yyyy-MM-dd_HHmm") + "_rapport.txt");
            File.WriteAllText(f, Ed.Name + " — " + Environment.MachineName + " — " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + Environment.NewLine + Environment.NewLine + diag, new System.Text.UTF8Encoding(true));
            try { Process.Start("notepad.exe", "\"" + f + "\""); } catch { }
        }
#endif
    }
}
