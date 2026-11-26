// Rapport d'intervention - saisie, signature client, export HTML / PDF et Note Station
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

[assembly: AssemblyTitle("Rapport d'intervention")]
[assembly: AssemblyDescription("Rapport d'intervention client")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("Rapport d'intervention")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace RapportInter
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            AppInfo.Name = "Rapport d'intervention"; AppInfo.Id = "RapportInter"; AppInfo.Version = "1.0.0"; AppInfo.MachineWide = false;
            Boot.Run(() => new MainForm(), false, null);
        }
    }

    // ===================================================================== Zone de signature (souris, stylet, doigt)
    class SignPad : Control
    {
        readonly List<List<Point>> strokes = new List<List<Point>>();
        List<Point> cur;
        public bool Empty { get { return strokes.Count == 0; } }
        public SignPad() { DoubleBuffered = true; ResizeRedraw = true; Cursor = Cursors.Cross; BackColor = Color.White; }
        public void Clear() { strokes.Clear(); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { cur = new List<Point> { e.Location }; strokes.Add(cur); Capture = true; }
        protected override void OnMouseMove(MouseEventArgs e) { if (cur != null && e.Button == MouseButtons.Left) { cur.Add(e.Location); Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { cur = null; Capture = false; }
        void Draw(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Color.FromArgb(20, 30, 60), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                foreach (var s in strokes) if (s.Count > 1) g.DrawLines(p, s.ToArray()); else if (s.Count == 1) g.FillEllipse(Brushes.Black, s[0].X - 1, s[0].Y - 1, 2, 2);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.White);
            using (var p = new Pen(Color.FromArgb(200, 205, 215)) { DashStyle = DashStyle.Dash }) e.Graphics.DrawLine(p, 10, Height - 18, Width - 10, Height - 18);
            if (Empty) using (var f = new Font("Segoe UI", 9f)) e.Graphics.DrawString("Signature du client (souris / doigt)", f, Brushes.Gray, 10, 8);
            Draw(e.Graphics);
        }
        public string Png64()
        {
            if (Empty) return null;
            using (var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height)))
            {
                using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); Draw(g); }
                using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return Convert.ToBase64String(ms.ToArray()); }
            }
        }
    }

    // ===================================================================== Fenêtre
    class MainForm : CyberForm
    {
        readonly Dictionary<string, Control> f = new Dictionary<string, Control>();
        readonly SignPad sign;
        readonly ListBox quick;
        readonly Label lDur;
        string savedSig;   // signature rechargée depuis un rapport

        static string Dir
        {
            get
            {
                var d = Prefs.Ini.Get("Dossier", "");
                if (d.Length == 0 || !Directory.Exists(d)) d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rapports d'intervention");
                Directory.CreateDirectory(d); return d;
            }
        }
        static string QuickFile { get { return Path.Combine(AppInfo.DataDir, "actions-frequentes.txt"); } }
        static string ClientsFile { get { return Path.Combine(AppInfo.DataDir, "clients.txt"); } }

        public MainForm() : base("RAPPORT_INTERVENTION", 1180, 840)
        {
            SetSubtitle("Rapport d'intervention client  //  signature, PDF, Note Station  //  by alex.b");
            Func<float, int> k = Theme.S;
            int bx = k(16);
            var bNew = new Chip("Nouveau") { AccentOf = t => t.Muted };
            var bOpen = new Chip("Ouvrir…") { AccentOf = t => t.Muted };
            var bPdf = new Chip("▶  ENREGISTRER + PDF") { Primary = true, AccentOf = t => t.Ok };
            var bNote = new Chip("Copier pour Note Station") { AccentOf = t => t.Accent2 };
            var bPc = new Chip("Infos de ce poste") { AccentOf = t => t.Accent };
            var bSet = new Chip("Société / logo…") { AccentOf = t => t.Muted };
            var bFolder = new Chip("Dossier des rapports") { AccentOf = t => t.Muted };
            foreach (var c in new[] { bPdf, bNote, bPc, bNew, bOpen, bFolder, bSet }) { c.Location = new Point(bx, k(8)); bx = c.Right + k(8); Body.Controls.Add(c); }

            // ---- colonne gauche : identification
            int x1 = k(16), w1 = k(360), y = k(50);
            Action<string, string, Control> add = (key, label, ctl) =>
            {
                Body.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(x1, y), Tag = "muted" });
                ctl.Location = new Point(x1, y + k(17)); ctl.Width = w1; Body.Controls.Add(ctl); f[key] = ctl;
                y += ctl.Height + k(24);
            };
            var cClient = new ComboBox { AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
            if (File.Exists(ClientsFile)) cClient.Items.AddRange(File.ReadAllLines(ClientsFile, Encoding.UTF8).Where(l => l.Trim().Length > 0).Distinct().OrderBy(l => l).ToArray());
            add("client", "Client *", cClient);
            add("site", "Site / adresse", new TextBox());
            add("contact", "Contact sur place", new TextBox());
            add("ticket", "N° de ticket", new TextBox());
            var cType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            cType.Items.AddRange(new object[] { "Dépannage", "Maintenance préventive", "Installation / mise en service", "NAS / sauvegarde", "Réseau / Wi-Fi", "Téléphonie VoIP", "Messagerie", "Sécurité / antivirus", "Autre" });
            cType.SelectedIndex = 0; add("type", "Type d'intervention", cType);
            var cLieu = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            cLieu.Items.AddRange(new object[] { "Sur site", "À distance", "En atelier" }); cLieu.SelectedIndex = 0; add("lieu", "Lieu", cLieu);
            add("tech", "Technicien", new TextBox { Text = Prefs.Ini.Get("Tech", "") });
            var dDate = new DateTimePicker { Format = DateTimePickerFormat.Short }; add("date", "Date", dDate);
            // heures sur une ligne
            Body.Controls.Add(new Label { Text = "Début              Fin", AutoSize = true, Location = new Point(x1, y), Tag = "muted" });
            var tStart = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Location = new Point(x1, y + k(17)), Width = k(90), Value = DateTime.Today.AddHours(Math.Max(8, DateTime.Now.Hour)) };
            var tEnd = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Location = new Point(x1 + k(100), y + k(17)), Width = k(90), Value = DateTime.Now };
            lDur = new Label { AutoSize = true, Location = new Point(x1 + k(200), y + k(20)), Tag = "accent", Font = new Font("Consolas", 10f, FontStyle.Bold) };
            Body.Controls.AddRange(new Control[] { tStart, tEnd, lDur }); f["start"] = tStart; f["end"] = tEnd;
            EventHandler dur = (s, e) => lDur.Text = "Durée " + Duration();
            tStart.ValueChanged += dur; tEnd.ValueChanged += dur; y += k(50);
            var cRes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            cRes.Items.AddRange(new object[] { "Résolu", "Partiellement résolu", "Non résolu — à suivre", "En attente de pièce / devis" }); cRes.SelectedIndex = 0; add("result", "Résultat", cRes);
            add("signer", "Nom du signataire", new TextBox());
            sign = new SignPad { Location = new Point(x1, y), Size = new Size(w1, k(110)), Anchor = AnchorStyles.Top | AnchorStyles.Left };
            var bClr = new Chip("Effacer la signature") { AccentOf = t => t.Muted, Location = new Point(x1, y + k(116)) };
            bClr.Click += (s, e) => { sign.Clear(); savedSig = null; };
            Body.Controls.Add(sign); Body.Controls.Add(bClr);

            // ---- colonne droite : contenu
            int x2 = x1 + w1 + k(20), w2 = Body.Width - x2 - k(16) - k(230);
            y = k(50);
            Action<string, string, int> big = (key, label, h) =>
            {
                Body.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(x2, y), Tag = "muted" });
                var t = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Location = new Point(x2, y + k(17)), Size = new Size(w2, k(h)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
                Body.Controls.Add(t); f[key] = t; y += k(h) + k(26);
            };
            big("demande", "Demande / symptôme signalé *", 70);
            big("diag", "Diagnostic", 90);
            big("actions", "Actions réalisées *  (double-clic sur une action fréquente à droite pour l'ajouter)", 170);
            big("materiel", "Matériel / pièces / licences", 60);
            big("reco", "Recommandations / suite à donner", 80);

            // ---- actions fréquentes
            int x3 = Body.Width - k(16) - k(220);
            Body.Controls.Add(new Label { Text = "Actions fréquentes", AutoSize = true, Location = new Point(x3, k(50)), Tag = "accent", Anchor = AnchorStyles.Top | AnchorStyles.Right });
            quick = new ListBox { Location = new Point(x3, k(67)), Size = new Size(k(220), k(420)), Anchor = AnchorStyles.Top | AnchorStyles.Right, IntegralHeight = false, HorizontalScrollbar = true };
            LoadQuick();
            quick.DoubleClick += (s, e) =>
            {
                if (quick.SelectedItem == null) return;
                var t = (TextBox)f["actions"];
                t.AppendText((t.TextLength > 0 && !t.Text.EndsWith("\n") ? "\r\n" : "") + "- " + quick.SelectedItem + "\r\n");
                t.Focus();
            };
            var bQ = new Chip("Modifier la liste") { AccentOf = t => t.Muted, Location = new Point(x3, k(494)), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            bQ.Click += (s, e) => { LoadQuick(); var p = Process.Start("notepad.exe", "\"" + QuickFile + "\""); if (p != null) { p.EnableRaisingEvents = true; p.Exited += (a, b) => BeginInvoke(new Action(LoadQuick)); } };
            Body.Controls.Add(quick); Body.Controls.Add(bQ);

            bNew.Click += (s, e) => { if (Ask(this, "Vider le formulaire ?")) Reset(); };
            bPdf.Click += (s, e) => SaveAndOpen();
            bNote.Click += (s, e) => { if (Valid()) { Clipboard.SetText(NoteText()); bNote.SetText("✓ Copié"); } };
            bPc.Click += (s, e) => { var t = (TextBox)f["diag"]; t.AppendText((t.TextLength > 0 ? "\r\n" : "") + PcInfo() + "\r\n"); };
            bFolder.Click += (s, e) => Process.Start("explorer.exe", "\"" + Dir + "\"");
            bOpen.Click += (s, e) => OpenReport();
            bSet.Click += (s, e) => Settings();
            dur(null, null);
        }

        void LoadQuick()
        {
            if (!File.Exists(QuickFile))
                File.WriteAllText(QuickFile,
                    "# Rapport d'intervention (by alex.b) - une action par ligne\r\n" +
                    "Diagnostic matériel et logiciel du poste\r\nNettoyage des fichiers temporaires et du disque\r\nMises à jour Windows installées\r\nMise à jour des pilotes\r\n" +
                    "Analyse antivirus complète, aucune menace\r\nVérification de l'état des disques (SMART)\r\nRéparation du démarrage (bootrec / bcdboot)\r\n" +
                    "Réparation des fichiers système (DISM + SFC)\r\nSauvegarde du profil utilisateur\r\nRestauration des données utilisateur\r\nInstallation et paramétrage de la messagerie\r\n" +
                    "Configuration du profil Outlook\r\nRemplacement de la charnière\r\nRemplacement du disque par un SSD + clonage\r\nAjout de mémoire vive\r\n" +
                    "Vérification de la sauvegarde du NAS (Hyper Backup)\r\nMise à jour DSM du NAS\r\nRemplacement d'un disque du NAS + reconstruction du RAID\r\n" +
                    "Paramétrage du téléphone IP (compte SIP)\r\nMise à jour du firmware du téléphone\r\nConfiguration du Wi-Fi\r\nPassage en IP fixe\r\n" +
                    "Installation de l'imprimante réseau\r\nTest de fonctionnement avec l'utilisateur\r\nExplications données à l'utilisateur\r\n", new UTF8Encoding(false));
            quick.Items.Clear();
            quick.Items.AddRange(File.ReadAllLines(QuickFile, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToArray());
        }

        string V(string key) { var c = f[key]; return (c is DateTimePicker ? ((DateTimePicker)c).Value.ToString("dd/MM/yyyy") : c.Text ?? "").Trim(); }

        string Duration()
        {
            var a = ((DateTimePicker)f["start"]).Value.TimeOfDay; var b = ((DateTimePicker)f["end"]).Value.TimeOfDay;
            var d = b - a; if (d < TimeSpan.Zero) d += TimeSpan.FromDays(1);
            return (int)d.TotalHours + " h " + d.Minutes.ToString("00");
        }

        bool Valid()
        {
            if (V("client").Length == 0) { Warn(this, "Indique le client."); f["client"].Focus(); return false; }
            if (V("demande").Length == 0) { Warn(this, "Indique la demande / le symptôme."); f["demande"].Focus(); return false; }
            if (V("actions").Length == 0) { Warn(this, "Indique les actions réalisées."); f["actions"].Focus(); return false; }
            return true;
        }

        void Reset()
        {
            foreach (var key in new[] { "client", "site", "contact", "ticket", "signer", "demande", "diag", "actions", "materiel", "reco" }) f[key].Text = "";
            ((ComboBox)f["result"]).SelectedIndex = 0; ((ComboBox)f["type"]).SelectedIndex = 0;
            ((DateTimePicker)f["date"]).Value = DateTime.Today; ((DateTimePicker)f["end"]).Value = DateTime.Now;
            sign.Clear(); savedSig = null;
        }

        static string PcInfo()
        {
            string model = "", serial = "", os = "", cpu = ""; long ram = 0;
            try { using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem")) foreach (ManagementObject o in s.Get()) { model = (o["Manufacturer"] + " " + o["Model"]).Trim(); ram = Convert.ToInt64(o["TotalPhysicalMemory"]); } } catch { }
            try { using (var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS")) foreach (ManagementObject o in s.Get()) serial = (o["SerialNumber"] ?? "").ToString().Trim(); } catch { }
            try { using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor")) foreach (ManagementObject o in s.Get()) cpu = (o["Name"] ?? "").ToString().Trim(); } catch { }
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build; int.TryParse((k.GetValue("CurrentBuildNumber") ?? "0").ToString(), out build);
                    var name = (k.GetValue("ProductName") ?? "Windows").ToString();
                    if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");   // ProductName indique encore « Windows 10 » sur Windows 11
                    os = name + " " + (k.GetValue("DisplayVersion") ?? "") + " (build " + build + ")";
                }
            }
            catch { }
            return "Poste : " + Environment.MachineName + " — " + model + (serial.Length > 0 ? " — N° série " + serial : "") + " — " + os + " — " + cpu + " — " + Fmt.Size(ram);
        }

        string NoteText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("RAPPORT D'INTERVENTION — " + V("client") + (V("ticket").Length > 0 ? " — ticket " + V("ticket") : ""));
            sb.AppendLine(V("date") + " · " + V("lieu") + " · " + V("type") + " · " + Duration() + " (" + ((DateTimePicker)f["start"]).Value.ToString("HH:mm") + " → " + ((DateTimePicker)f["end"]).Value.ToString("HH:mm") + ")" + (V("tech").Length > 0 ? " · " + V("tech") : ""));
            if (V("contact").Length > 0) sb.AppendLine("Contact : " + V("contact"));
            Action<string, string> part = (t, key) => { if (V(key).Length > 0) { sb.AppendLine(); sb.AppendLine(t.ToUpperInvariant()); sb.AppendLine(V(key)); } };
            part("Demande", "demande"); part("Diagnostic", "diag"); part("Actions réalisées", "actions"); part("Matériel", "materiel"); part("Recommandations", "reco");
            sb.AppendLine(); sb.AppendLine("RÉSULTAT : " + V("result"));
            return sb.ToString();
        }

        static string H(string s) { return Fmt.Html(s).Replace("\r\n", "\n").Replace("\n", "<br>"); }

        string Html()
        {
            var co = Prefs.Ini.Get("Societe", ""); var coLine = Prefs.Ini.Get("SocieteInfos", ""); var logo = Prefs.Ini.Get("Logo", "");
            string logoTag = "";
            if (logo.Length > 0 && File.Exists(logo))
                try
                {
                    var ext = Path.GetExtension(logo).ToLowerInvariant();
                    var mime = ext == ".png" ? "image/png" : ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : null;
                    if (mime != null) logoTag = "<img class=logo src=\"data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(logo)) + "\">";
                }
                catch { }
            var sig = sign.Png64() ?? savedSig;
            string res = V("result"); string resCls = res.StartsWith("Résolu") ? "ok" : res.StartsWith("Partiellement") || res.StartsWith("En attente") ? "warn" : "bad";
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=fr><head><meta charset=utf-8><title>Rapport d'intervention - ").Append(Fmt.Html(V("client"))).Append(" - ").Append(V("date")).Append("</title><style>")
              .Append("@page{size:A4;margin:14mm}body{font-family:'Segoe UI',Arial,sans-serif;color:#1a2230;font-size:10.5pt;max-width:780px;margin:24px auto;padding:0 16px}")
              .Append("header{display:flex;justify-content:space-between;align-items:flex-start;border-bottom:3px solid #0084a8;padding-bottom:10px}.logo{max-height:64px;max-width:220px}")
              .Append("h1{font-size:18pt;margin:0;color:#0084a8;letter-spacing:.5px}.co{font-weight:600}.muted{color:#5f6f80;font-size:9pt}")
              .Append("table.id{width:100%;border-collapse:collapse;margin:14px 0}table.id td{padding:5px 8px;border:1px solid #d6dee8;vertical-align:top}table.id td.k{background:#f1f5f9;width:22%;font-weight:600}")
              .Append("h2{font-size:11pt;margin:16px 0 4px;color:#0084a8;text-transform:uppercase;border-bottom:1px solid #d6dee8;padding-bottom:2px}.bx{white-space:normal;line-height:1.45}")
              .Append(".res{display:inline-block;padding:4px 12px;border-radius:4px;font-weight:700}.ok{background:#e3f6ec;color:#007a43}.warn{background:#fff3dc;color:#a35d00}.bad{background:#fde6ea;color:#b0102f}")
              .Append(".sig{display:flex;gap:30px;margin-top:22px}.sig div{flex:1;border:1px solid #d6dee8;padding:8px;min-height:110px}.sig img{max-width:100%;max-height:110px}")
              .Append("footer{margin-top:26px;border-top:1px solid #d6dee8;padding-top:6px;font-size:8pt;color:#8a97a6;text-align:center}@media print{.noprint{display:none}body{margin:0}}")
              .Append(".noprint{background:#0084a8;color:#fff;border:0;padding:8px 16px;border-radius:4px;font-size:10pt;cursor:pointer;float:right}</style></head><body>");
            sb.Append("<button class=noprint onclick=\"window.print()\">Imprimer / Enregistrer en PDF</button>");
            sb.Append("<header><div>").Append(logoTag).Append(co.Length > 0 ? "<div class=co>" + Fmt.Html(co) + "</div>" : "").Append(coLine.Length > 0 ? "<div class=muted>" + H(coLine) + "</div>" : "")
              .Append("</div><div style=\"text-align:right\"><h1>RAPPORT D'INTERVENTION</h1><div class=muted>").Append(V("ticket").Length > 0 ? "Ticket n° " + Fmt.Html(V("ticket")) + " · " : "").Append(V("date")).Append("</div></div></header>");
            sb.Append("<table class=id>")
              .Append("<tr><td class=k>Client</td><td>").Append(Fmt.Html(V("client"))).Append("</td><td class=k>Date</td><td>").Append(V("date")).Append("</td></tr>")
              .Append("<tr><td class=k>Site</td><td>").Append(H(V("site"))).Append("</td><td class=k>Horaires</td><td>").Append(((DateTimePicker)f["start"]).Value.ToString("HH:mm")).Append(" → ").Append(((DateTimePicker)f["end"]).Value.ToString("HH:mm")).Append(" (").Append(Duration()).Append(")</td></tr>")
              .Append("<tr><td class=k>Contact</td><td>").Append(Fmt.Html(V("contact"))).Append("</td><td class=k>Lieu</td><td>").Append(Fmt.Html(V("lieu"))).Append("</td></tr>")
              .Append("<tr><td class=k>Type</td><td>").Append(Fmt.Html(V("type"))).Append("</td><td class=k>Technicien</td><td>").Append(Fmt.Html(V("tech"))).Append("</td></tr></table>");
            Action<string, string> part = (t, key) => { if (V(key).Length > 0) sb.Append("<h2>").Append(t).Append("</h2><div class=bx>").Append(H(V(key))).Append("</div>"); };
            part("Demande / symptôme", "demande"); part("Diagnostic", "diag"); part("Actions réalisées", "actions"); part("Matériel / pièces", "materiel"); part("Recommandations", "reco");
            sb.Append("<h2>Résultat</h2><span class=\"res ").Append(resCls).Append("\">").Append(Fmt.Html(res)).Append("</span>");
            sb.Append("<div class=sig><div><b>Le technicien</b><br>").Append(Fmt.Html(V("tech"))).Append("</div><div><b>Le client</b> — ").Append(Fmt.Html(V("signer")))
              .Append(sig != null ? "<br><img src=\"data:image/png;base64," + sig + "\">" : "<br><span class=muted>(non signé)</span>").Append("</div></div>");
            sb.Append("<footer>").Append(co.Length > 0 ? Fmt.Html(co) + " · " : "").Append("Rapport généré le ").Append(DateTime.Now.ToString("dd/MM/yyyy à HH:mm")).Append("</footer></body></html>");
            return sb.ToString();
        }

        static string Safe(string s) { s = Regex.Replace(s ?? "", @"[^\p{L}\p{N}\-_ ]", "").Trim(); return s.Length > 40 ? s.Substring(0, 40) : s; }

        void SaveAndOpen()
        {
            if (!Valid()) return;
            Prefs.Ini.Set("Tech", V("tech")); Prefs.Save();
            try
            {
                var date = ((DateTimePicker)f["date"]).Value;
                var baseName = date.ToString("yyyy-MM-dd") + "_" + Safe(V("client")) + (V("ticket").Length > 0 ? "_" + Safe(V("ticket")) : "");
                var html = Path.Combine(Dir, baseName + ".html");
                File.WriteAllText(html, Html(), new UTF8Encoding(false));
                // données brutes pour rouvrir / corriger le rapport plus tard
                var ini = new Ini(Path.Combine(Dir, baseName + ".rapport"));
                foreach (var key in f.Keys)
                {
                    var c = f[key];
                    ini.Set(key, c is DateTimePicker ? ((DateTimePicker)c).Value.ToString("o") : Convert.ToBase64String(Encoding.UTF8.GetBytes(c.Text ?? "")));
                }
                var sg = sign.Png64() ?? savedSig; if (sg != null) ini.Set("signature", sg);
                ini.Save("Rapport d'intervention (by alex.b) - données");
                var clients = File.Exists(ClientsFile) ? File.ReadAllLines(ClientsFile, Encoding.UTF8).ToList() : new List<string>();
                if (!clients.Contains(V("client"))) { clients.Add(V("client")); File.WriteAllLines(ClientsFile, clients, new UTF8Encoding(false)); ((ComboBox)f["client"]).Items.Add(V("client")); }
                Log.Write("Rapport enregistré : " + html);
                Process.Start(html);
            }
            catch (Exception ex) { Warn(this, "Enregistrement impossible : " + ex.Message); Log.Write(ex.ToString()); }
        }

        void OpenReport()
        {
            using (var d = new OpenFileDialog { Filter = "Rapport d'intervention|*.rapport", InitialDirectory = Dir })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var ini = new Ini(d.FileName);
                foreach (var key in f.Keys)
                {
                    var v = ini.Get(key, null); if (v == null) continue;
                    var c = f[key];
                    try
                    {
                        if (c is DateTimePicker) ((DateTimePicker)c).Value = DateTime.Parse(v, null, System.Globalization.DateTimeStyles.RoundtripKind);
                        else
                        {
                            var txt = Encoding.UTF8.GetString(Convert.FromBase64String(v));
                            var cb = c as ComboBox;
                            if (cb != null && cb.DropDownStyle == ComboBoxStyle.DropDownList) { int i = cb.Items.IndexOf(txt); if (i >= 0) cb.SelectedIndex = i; }
                            else c.Text = txt;
                        }
                    }
                    catch { }
                }
                sign.Clear(); savedSig = ini.Get("signature", null);
                if (savedSig != null && !Regex.IsMatch(savedSig, @"^[A-Za-z0-9+/=]+$")) savedSig = null;
                Info(this, "Rapport rechargé." + (savedSig != null ? "\nLa signature enregistrée est conservée (« Effacer la signature » pour la refaire)." : ""));
            }
        }

        void Settings()
        {
            using (var dlg = new Form { Text = "Société émettrice · by alex.b", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(Theme.S(460), Theme.S(270)), BackColor = Theme.Current.Bg, Font = Font })
            {
                Func<float, int> k = Theme.S;
                var l1 = new Label { Text = "Nom de la société (en-tête du rapport)", AutoSize = true, Location = new Point(k(14), k(12)) };
                var t1 = new TextBox { Text = Prefs.Ini.Get("Societe", ""), Location = new Point(k(14), k(30)), Width = k(430) };
                var l2 = new Label { Text = "Adresse / téléphone / e-mail (plusieurs lignes possibles)", AutoSize = true, Location = new Point(k(14), k(62)) };
                var t2 = new TextBox { Text = Prefs.Ini.Get("SocieteInfos", "").Replace("\\n", "\r\n"), Multiline = true, Location = new Point(k(14), k(80)), Size = new Size(k(430), k(60)) };
                var l3 = new Label { Text = "Logo (PNG ou JPG)", AutoSize = true, Location = new Point(k(14), k(150)) };
                var t3 = new TextBox { Text = Prefs.Ini.Get("Logo", ""), Location = new Point(k(14), k(168)), Width = k(340) };
                var bb = new Button { Text = "Parcourir…", Location = new Point(k(362), k(166)), Width = k(82) };
                bb.Click += (s, e) => { using (var o = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg" }) if (o.ShowDialog(dlg) == DialogResult.OK) t3.Text = o.FileName; };
                var ok = new Button { Text = "Enregistrer", DialogResult = DialogResult.OK, Location = new Point(k(270), k(222)), Width = k(84) };
                var ca = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Location = new Point(k(360), k(222)), Width = k(84) };
                dlg.Controls.AddRange(new Control[] { l1, t1, l2, t2, l3, t3, bb, ok, ca });
                dlg.AcceptButton = ok; dlg.CancelButton = ca; Recolor(dlg);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Prefs.Ini.Set("Societe", t1.Text.Trim()); Prefs.Ini.Set("SocieteInfos", t2.Text.Trim().Replace("\r\n", "\\n")); Prefs.Ini.Set("Logo", t3.Text.Trim()); Prefs.Save();
            }
        }
    }
}
