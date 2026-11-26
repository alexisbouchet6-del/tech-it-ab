// AMI·OPS - réseau, messagerie, coffre, outils intégrés, paramètres
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlexB;

namespace AmiOps
{
    // ===================================================================== Réseau (moteur ScanParc) → fiche client
    class ReseauPage : Page
    {
        readonly TextBox range; readonly CheckBox deep; readonly Chip bScan; readonly NeonBar bar; readonly Label stat; readonly Grid grid; readonly ComboBox client;
        CancellationTokenSource cts; List<ScanParc.Device> last = new List<ScanParc.Device>();
        public override string Title { get { return "Réseau"; } }

        public ReseauPage()
        {
            Lbl("RÉSEAU", 0, k(2), "accent", 13f, true);
            string adapter; var def = ScanParc.Scanner.DefaultRange(out adapter);
            Lbl(adapter.Length > 0 ? "carte active : " + adapter : "aucune carte active", k(110), k(10), "muted");
            range = new TextBox { Text = Prefs.Ini.Get("Range", def), Location = new Point(0, k(40)), Width = k(220), Font = new Font("Consolas", 10f) };
            deep = new CheckBox { Text = "Ports + page web", Checked = true, AutoSize = true, Location = new Point(k(230), k(42)) };
            Controls.Add(range); Controls.Add(deep);
            bScan = Btn("▶  SCANNER", t => t.Ok, k(370), k(36), (s, e) => Scan(), true);
            client = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(bScan.Right + k(24), k(40)), Width = k(220) };
            Controls.Add(client);
            Btn("Ajouter la sélection à ce client", t => t.Accent2, client.Right + k(8), k(36), (s, e) => AddToClient());
            bar = new NeonBar { Location = new Point(0, k(78)), Height = k(6), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            stat = Lbl("Coche les appareils puis « Ajouter la sélection à ce client » : ils rejoignent son parc (type deviné, IP, MAC).", 0, k(90), "muted");
            grid = new Grid { CheckBoxes = true }; grid.Col("IP", 115).Col("Nom", 170).Col("MAC", 135).Col("Fabricant", 110).Col("Type", 190).Col("Ports", 140).Col("Page web / info", 220);
            grid.Location = new Point(0, k(114)); grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            grid.DoubleClick += (s, e) => { var d = grid.Sel as ScanParc.Device; if (d != null) try { Process.Start(d.Ports.Contains(5001) ? "https://" + d.Ip + ":5001" : d.Ports.Contains(443) ? "https://" + d.Ip : "http://" + d.Ip); } catch { } };
            Controls.Add(bar); Controls.Add(grid);
            Resize += (s, e) => { bar.Width = ClientSize.Width; grid.Size = new Size(ClientSize.Width, ClientSize.Height - grid.Top); };
            range.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Scan(); } };
        }

        public override void OnShow()
        {
            var sel = client.SelectedItem as string;
            client.Items.Clear(); client.Items.Add("(choisir le client)");
            foreach (var c in Store.D.Clients.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)) client.Items.Add(c.Name);
            client.SelectedIndex = sel != null && client.Items.Contains(sel) ? client.Items.IndexOf(sel) : 0;
        }

        void Scan()
        {
            if (cts != null) { cts.Cancel(); return; }
            string err; var ips = ScanParc.Scanner.Parse(range.Text, out err);
            if (err != null) { CyberForm.Warn(this, err); return; }
            Prefs.Ini.Set("Range", range.Text.Trim()); Prefs.Save();
            cts = new CancellationTokenSource(); var ct = cts.Token; bool dp = deep.Checked;
            bScan.SetText("■  ARRÊTER"); grid.Items.Clear(); bar.Value = 0;
            Task.Run(() =>
            {
                List<ScanParc.Device> res = null;
                try { res = ScanParc.Scanner.Run(ips, dp, (done, total, d) => { if (done > 0 && done % 8 == 0) BeginInvoke(new Action(() => { bar.Value = (double)done / total * 0.7; stat.Text = "Recherche… " + done + " / " + total; })); }, ct); }
                catch (Exception ex) { Log.Write(ex.ToString()); }
                BeginInvoke(new Action(() =>
                {
                    cts = null; bScan.SetText("▶  SCANNER"); bar.Value = 1; last = res ?? new List<ScanParc.Device>();
                    var t = Theme.Current; grid.BeginUpdate(); grid.Items.Clear();
                    foreach (var d in last)
                    {
                        var known = Store.D.Clients.SelectMany(c => c.Assets.Select(a => Tuple.Create(c, a))).FirstOrDefault(x => (d.Mac.Length > 0 && x.Item2.Mac.Equals(d.Mac, StringComparison.OrdinalIgnoreCase)));
                        grid.Items.Add(new ListViewItem(new[] { d.Ip.ToString(), d.Name, d.Mac, d.Vendor, d.Type + (known != null ? "  ✓ " + known.Item1.Name : ""), string.Join(",", d.Ports), d.Info }) { Tag = d, ForeColor = known != null ? t.Muted : d.Type.StartsWith("NAS") ? t.Accent : t.Text });
                    }
                    grid.EndUpdate();
                    stat.Text = last.Count + " appareil(s) · ✓ = déjà dans une fiche client · double-clic = page d'admin";
                }));
            });
        }

        static string KindOf(string type)
        {
            if (type.StartsWith("NAS")) return "NAS"; if (type.Contains("phone") || type.Contains("VoIP") || type.Contains("Gigaset")) return "Téléphone IP";
            if (type.StartsWith("Imprimante")) return "Imprimante"; if (type.StartsWith("Box")) return "Box / routeur"; if (type.StartsWith("Équipement réseau")) return "Switch / Wi-Fi";
            if (type.Contains("serveur") || type.Contains("Linux")) return "Serveur"; if (type.StartsWith("PC")) return "PC"; return "Autre";
        }

        void AddToClient()
        {
            if (client.SelectedIndex <= 0) { CyberForm.Warn(this, "Choisis le client dans la liste."); return; }
            var c = Store.D.Clients.FirstOrDefault(x => x.Name == (string)client.SelectedItem); if (c == null) return;
            var sel = grid.CheckedItems.Cast<ListViewItem>().Select(i => (ScanParc.Device)i.Tag).ToList();
            if (sel.Count == 0) { CyberForm.Info(this, "Coche les appareils à ajouter."); return; }
            int added = 0, upd = 0;
            foreach (var d in sel)
            {
                var a = c.Assets.FirstOrDefault(x => (d.Mac.Length > 0 && x.Mac.Equals(d.Mac, StringComparison.OrdinalIgnoreCase)) || (d.Mac.Length == 0 && x.Ip == d.Ip.ToString()));
                if (a != null) { a.Ip = d.Ip.ToString(); if (a.Name.Length == 0) a.Name = d.Name; upd++; continue; }
                c.Assets.Add(new Asset { Id = Store.NewId(), Kind = KindOf(d.Type), Name = d.Name.Length > 0 ? d.Name.Split('.')[0] : d.Info, Ip = d.Ip.ToString(), Mac = d.Mac, Model = d.Vendor, Notes = d.Info.Length > 0 ? "Page web : " + d.Info : "", Added = DateTime.Now });
                added++;
            }
            Store.Save();
            CyberForm.Info(this, added + " équipement(s) ajouté(s), " + upd + " mis à jour dans la fiche « " + c.Name + " ».");
            Scan();
        }
    }

    // ===================================================================== Messagerie (moteur MailCheck) sur tous les domaines clients
    class MailPage : Page
    {
        readonly Grid grid; readonly Label stat; readonly NeonBar bar; bool busy;
        public override string Title { get { return "Messagerie"; } }

        public static string CleanDomain(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.Contains("@")) s = s.Substring(s.IndexOf('@') + 1);
            s = Regex.Replace(s, @"^https?://", ""); s = s.Split('/')[0].TrimEnd('.');
            return Regex.IsMatch(s, @"^(?=.{4,253}$)([a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$") ? s : "";
        }

        public MailPage()
        {
            Lbl("MESSAGERIE", 0, k(2), "accent", 13f, true);
            Lbl("MX, SPF, DKIM, DMARC, autodiscover et certificats de tous les domaines de tes clients. Lecture seule, aucun identifiant.", k(150), k(10), "muted");
            int x = 0;
            x = Btn("▶  TOUT VÉRIFIER", t => t.Ok, x, k(36), (s, e) => Check(All()), true).Right + k(6);
            x = Btn("Vérifier la sélection", t => t.Accent, x, k(36), (s, e) => Check(grid.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList())).Right + k(6);
            x = Btn("Rapport complet", t => t.Accent2, x, k(36), (s, e) => { var d = grid.Sel as string; if (d != null) ShowReport(this, d); }).Right + k(6);
            x = Btn("Domaine hors client…", t => t.Muted, x, k(36), (s, e) => { var d = Dlg.Ask(this, "Vérifier un domaine", "Domaine ou adresse mail", "", false); d = CleanDomain(d); if (d.Length > 0) Check(new List<string> { d }); }).Right + k(6);
            Btn("MXToolbox (listes noires)", t => t.Muted, x, k(36), (s, e) => { var d = grid.Sel as string; if (d != null) Process.Start("https://mxtoolbox.com/SuperTool.aspx?action=blacklist%3a" + Uri.EscapeDataString(d)); });
            bar = new NeonBar { Location = new Point(0, k(78)), Height = k(6), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            stat = Lbl("", 0, k(90), "muted");
            grid = new Grid().Col("Domaine", 190).Col("Client", 180).Col("Verdict", 280).Col("Vérifié le", 110).Col("Détail", 400);
            grid.Location = new Point(0, k(114)); grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            grid.DoubleClick += (s, e) => { var d = grid.Sel as string; if (d != null) ShowReport(this, d); };
            Controls.Add(bar); Controls.Add(grid);
            Resize += (s, e) => { bar.Width = ClientSize.Width; grid.Size = new Size(ClientSize.Width, ClientSize.Height - grid.Top); };
        }

        List<string> All() { return Store.D.Clients.SelectMany(c => c.Domains).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }

        public override void OnShow()
        {
            var t = Theme.Current; var doms = All();
            foreach (var m in Store.D.Mail) if (!doms.Contains(m.Domain)) doms.Add(m.Domain);
            grid.BeginUpdate(); grid.Items.Clear();
            foreach (var d in doms.OrderBy(d => { var m = Store.MailOf(d); return m == null ? 1 : m.Color == "bad" ? 0 : m.Color == "warn" ? 1 : 2; }).ThenBy(d => d))
            {
                var m = Store.MailOf(d); var owner = Store.D.Clients.FirstOrDefault(c => c.Domains.Contains(d));
                grid.Items.Add(new ListViewItem(new[] { d, owner != null ? owner.Name : "(hors client)", m != null ? m.Verdict : "jamais vérifié", m != null ? m.At.ToString("dd/MM/yyyy HH:mm") : "", m != null ? m.Detail : "" }) { Tag = d, ForeColor = m != null ? t.ByName(m.Color) : t.Muted });
            }
            grid.EndUpdate();
            if (!busy) stat.Text = doms.Count + " domaine(s) · " + Store.D.Mail.Count(m => m.Color == "bad") + " en erreur · " + Store.D.Mail.Count(m => m.Color == "warn") + " à améliorer · double-clic = rapport complet";
        }

        public override void Open(object arg)
        {
            var s = arg as string; if (s == null) return;
            if (s == "all") { Check(All()); return; }
            if (s.StartsWith("client:")) { var c = Store.ClientById(s.Substring(7)); if (c != null) Check(c.Domains.ToList()); return; }
            foreach (ListViewItem it in grid.Items) if ((string)it.Tag == s) { it.Selected = true; it.EnsureVisible(); }
            Check(new List<string> { s });
        }

        void Check(List<string> doms)
        {
            if (busy || doms.Count == 0) return;
            busy = true; bar.Value = 0; int done = 0;
            stat.Text = "Vérification de " + doms.Count + " domaine(s)…";
            Task.Run(() =>
            {
                Parallel.ForEach(doms, new ParallelOptions { MaxDegreeOfParallelism = 4 }, d =>
                {
                    LogBox lb = null;
                    Invoke(new Action(() => { lb = new LogBox(); lb.CreateControl(); var h = lb.Handle; }));
                    var ch = new MailCheck.Checker(lb, s => { });
                    try { ch.Run(d, ""); } catch (Exception ex) { Log.Write("MailCheck " + d + " : " + ex.Message); }
                    // les lignes sont ajoutées via BeginInvoke : on lit le texte après elles, dans le même ordre
                    BeginInvoke(new Action(() =>
                    {
                        var m = Store.MailOf(d); if (m == null) { m = new MailResult { Domain = d }; Store.D.Mail.Add(m); }
                        m.Verdict = ch.Verdict.Length > 0 ? ch.Verdict : "CONTRÔLE INTERROMPU"; m.Color = ch.VColor; m.Detail = ch.Detail; m.At = DateTime.Now; m.Report = lb.Text;
                        lb.Dispose();
                        done++; bar.Value = (double)done / doms.Count; stat.Text = "Vérifié : " + done + " / " + doms.Count + " (" + d + ")";
                        OnShow();
                    }));
                });
                BeginInvoke(new Action(() => { busy = false; try { Store.Save(); } catch { } OnShow(); }));
            });
        }

        public static void ShowReport(IWin32Window owner, string domain)
        {
            var m = Store.MailOf(domain);
            if (m == null || m.Report.Length == 0) { CyberForm.Info(owner, "Domaine jamais vérifié : lance « Vérifier »."); return; }
            using (var f = new Form { Text = "MailCheck — " + domain + " · by alex.b", Size = new Size(Theme.S(900), Theme.S(700)), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Current.Bg, ShowInTaskbar = false })
            {
                var lb = new LogBox { Dock = DockStyle.Fill }; f.Controls.Add(lb);
                var copy = new Chip("Copier le rapport") { AccentOf = t => t.Accent2, Dock = DockStyle.Bottom }; copy.Click += (s, e) => Clip.Text(lb.Text.Replace("\n", "\r\n"));
                f.Controls.Add(copy);
                foreach (var l in m.Report.Split('\n'))
                {
                    var line = l.TrimEnd('\r');
                    string c = line.StartsWith("//") ? "accent" : line.StartsWith("VERDICT") ? m.Color : line.Contains("ABSENT") || line.Contains("INVALIDE") || line.Contains("DÉPASSÉ") || line.Contains("NXDOMAIN") ? "bad" : "text";
                    lb.Line(line, c, line.StartsWith("//") || line.StartsWith("VERDICT"));
                }
                CyberForm.Recolor(f); f.ShowDialog(owner);
            }
        }
    }

    // ===================================================================== Coffre des identifiants
    class VaultPage : Page
    {
        readonly Panel locked, open; readonly TextBox pw, search; readonly Label lMsg; readonly Grid grid; readonly Chip bUnlock;
        public override string Title { get { return "Coffre"; } }

        public VaultPage()
        {
            Lbl("COFFRE DES IDENTIFIANTS", 0, k(2), "accent", 13f, true);
            Lbl("AES-256 + HMAC, clé dérivée du mot de passe maître (PBKDF2-SHA256, 300 000 itérations). Verrouillage auto. Rien en clair sur le disque.", k(280), k(10), "muted");
            locked = new Panel { Location = new Point(0, k(44)), Size = new Size(k(600), k(200)) };
            lMsg = new Label { AutoSize = true, Location = new Point(0, 0), Tag = "text", Font = new Font("Segoe UI", 10f) };
            pw = new TextBox { UseSystemPasswordChar = true, Location = new Point(0, k(34)), Width = k(300), Font = new Font("Consolas", 11f) };
            bUnlock = new Chip("Déverrouiller") { Primary = true, AccentOf = t => t.Ok, Location = new Point(k(310), k(30)) };
            bUnlock.Click += (s, e) => DoUnlock();
            pw.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoUnlock(); } };
            locked.Controls.AddRange(new Control[] { lMsg, pw, bUnlock });
            open = new Panel { Location = new Point(0, k(44)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            search = new TextBox { Location = new Point(0, k(4)), Width = k(220) }; search.TextChanged += (s, e) => Fill();
            open.Controls.Add(search);
            int x = k(230);
            foreach (var b in new[] {
                Tuple.Create("＋ Ajouter", (Action)(() => { if (Edit(this, null, "")) Fill(); })), Tuple.Create("Copier le mot de passe", (Action)(() => CopyPass(this, grid.Sel as Secret))),
                Tuple.Create("Copier l'identifiant", (Action)(() => { var s = grid.Sel as Secret; if (s != null) Clip.Text(s.User); })), Tuple.Create("Afficher", (Action)Reveal),
                Tuple.Create("Modifier", (Action)(() => { if (Edit(this, grid.Sel as Secret, null)) Fill(); })), Tuple.Create("Supprimer", (Action)(() => { if (Delete(this, grid.Sel as Secret)) Fill(); })),
                Tuple.Create("Générer un mot de passe", (Action)(() => { var g = Vault.Generate(20); Clip.Private(g, 30); CyberForm.Info(this, "Mot de passe généré et copié (30 s) :\n\n" + g); })),
                Tuple.Create("Changer le mot de passe maître", (Action)ChangeMaster), Tuple.Create("🔒 Verrouiller", (Action)(() => { Vault.Lock(); OnShow(); })) })
            {
                var go = b.Item2; var c = new Chip(b.Item1) { AccentOf = b.Item1.StartsWith("＋") ? (Func<Theme, Color>)(t => t.Ok) : b.Item1 == "Supprimer" ? (Func<Theme, Color>)(t => t.Bad) : (Func<Theme, Color>)(t => t.Accent), Location = new Point(x, 0) };
                c.Click += (s, e) => Run(go); open.Controls.Add(c); x = c.Right + k(6);
            }
            grid = new Grid().Col("Client", 180).Col("Libellé", 200).Col("Identifiant", 200).Col("URL", 260).Col("Modifié", 100);
            grid.Location = new Point(0, k(40)); grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            grid.DoubleClick += (s, e) => CopyPass(this, grid.Sel as Secret);
            open.Controls.Add(grid);
            Controls.Add(locked); Controls.Add(open);
            Resize += (s, e) => { open.Size = new Size(ClientSize.Width, ClientSize.Height - open.Top); grid.Size = new Size(open.Width, open.Height - grid.Top); };
        }

        public override void OnShow()
        {
            locked.Visible = !Vault.Unlocked; open.Visible = Vault.Unlocked;
            if (!Vault.Unlocked)
            {
                lMsg.Text = Vault.Exists ? "Coffre verrouillé : mot de passe maître." : "Crée le coffre : choisis un mot de passe maître (14 caractères minimum).\nSans lui, le contenu est irrécupérable : note-le dans un endroit sûr.";
                bUnlock.SetText(Vault.Exists ? "Déverrouiller" : "Créer le coffre"); pw.Top = Vault.Exists ? k(34) : k(50); bUnlock.Top = pw.Top - k(4);
                pw.Text = ""; pw.Focus();
            }
            else Fill();
        }

        void DoUnlock()
        {
            if (!Vault.Exists)
            {
                if (pw.Text.Length < 14) { CyberForm.Warn(this, "14 caractères minimum."); return; }
                var again = Dlg.Ask(this, "Confirmation", "Retape le mot de passe maître", "", true);
                if (again != pw.Text) { CyberForm.Warn(this, "Les deux saisies ne correspondent pas."); return; }
                Vault.Create(pw.Text);
            }
            else if (!Vault.Unlock(pw.Text)) { pw.Text = ""; CyberForm.Warn(this, "Mot de passe maître incorrect."); return; }
            pw.Text = ""; OnShow();
        }

        void Fill()
        {
            var q = search.Text.Trim();
            grid.BeginUpdate(); grid.Items.Clear();
            foreach (var s in Vault.Items.Where(x => q.Length == 0 || Store.Match(q, Store.ClientName(x.ClientId), x.Label, x.User, x.Url, x.Notes)).OrderBy(x => Store.ClientName(x.ClientId)).ThenBy(x => x.Label))
                grid.Items.Add(new ListViewItem(new[] { s.ClientId.Length > 0 ? Store.ClientName(s.ClientId) : "(général)", s.Label, s.User, s.Url, s.Updated.ToString("dd/MM/yyyy") }) { Tag = s });
            grid.EndUpdate();
        }

        void Reveal()
        {
            var s = grid.Sel as Secret; if (s == null) return;
            try { CyberForm.Info(this, s.Label + "\n\nIdentifiant : " + s.User + "\nMot de passe : " + Vault.Decrypt(s.Cipher) + (s.Notes.Length > 0 ? "\n\nNotes : " + s.Notes : "")); }
            catch (Exception ex) { CyberForm.Warn(this, "Déchiffrement impossible : " + ex.Message); }
        }

        void ChangeMaster()
        {
            var a = Dlg.Ask(this, "Mot de passe maître", "Nouveau mot de passe maître (14 caractères minimum)", "", true); if (a == null) return;
            if (a.Length < 14) { CyberForm.Warn(this, "14 caractères minimum."); return; }
            var b = Dlg.Ask(this, "Mot de passe maître", "Confirmation", "", true); if (b != a) { CyberForm.Warn(this, "Les deux saisies ne correspondent pas."); return; }
            Vault.ChangePassword(a); CyberForm.Info(this, "Mot de passe maître changé, tout le coffre a été rechiffré.");
        }

        public static bool EnsureOpen(Page from)
        {
            if (Vault.Unlocked) return true;
            if (!Vault.Exists) { from.Shell.Go("coffre", null); return false; }
            var p = Dlg.Ask(from, "Coffre", "Mot de passe maître", "", true);
            if (p == null) return false;
            if (!Vault.Unlock(p)) { CyberForm.Warn(from, "Mot de passe maître incorrect."); return false; }
            return true;
        }

        public static void CopyPass(Page from, Secret s)
        {
            if (s == null) { if (!Vault.Unlocked) EnsureOpen(from); return; }
            if (!EnsureOpen(from)) return;
            try { Clip.Private(Vault.Decrypt(s.Cipher), 30); if (s.Url.StartsWith("http") && CyberForm.Ask(from, "Mot de passe copié pour 30 s (hors historique Win+V).\n\nOuvrir " + s.Url + " ?")) Process.Start(s.Url); }
            catch (Exception ex) { CyberForm.Warn(from, "Déchiffrement impossible : " + ex.Message); }
        }

        public static bool Edit(Page from, Secret s, string clientId)
        {
            if (!EnsureOpen(from)) return false;
            bool isNew = s == null; if (isNew) s = new Secret { Id = Store.NewId(), ClientId = clientId ?? "" };
            var names = new[] { "(général)" }.Concat(Store.D.Clients.OrderBy(c => c.Name).Select(c => c.Name)).ToArray();
            var fc = new F("Client", s.ClientId.Length > 0 ? Store.ClientName(s.ClientId) : "(général)") { Options = names, Editable = false };
            var fl = new F("Libellé (ex. DSM NAS-COMPTA, box Orange, Microsoft 365 admin)", s.Label) { Required = true };
            var fu = new F("Identifiant", s.User);
            string current = ""; if (!isNew) try { current = Vault.Decrypt(s.Cipher); } catch { }
            var fp = new F("Mot de passe" + (isNew ? " (prérempli par un mot de passe fort généré)" : ""), isNew ? Vault.Generate(20) : current) { Password = true };
            var fw = new F("URL", s.Url); var fn = new F("Notes", s.Notes) { Multi = true };
            if (!Dlg.Edit(from, isNew ? "Nouvel identifiant" : "Modifier " + s.Label, fc, fl, fu, fp, fw, fn)) return false;
            var cl = Store.D.Clients.FirstOrDefault(c => c.Name == fc.Value);
            s.ClientId = cl != null ? cl.Id : ""; s.Label = fl.Value; s.User = fu.Value; s.Url = fw.Value; s.Notes = fn.Value; s.Cipher = Vault.Encrypt(fp.Value); s.Updated = DateTime.Now;
            if (isNew) Vault.Items.Add(s);
            Vault.Save(); return true;
        }

        public static bool Delete(Page from, Secret s)
        {
            if (s == null || !CyberForm.Ask(from, "Supprimer l'identifiant « " + s.Label + " » ?")) return false;
            Vault.Items.Remove(s); Vault.Save(); return true;
        }
    }

    // ===================================================================== Outils intégrés (toute la suite alex.b dans un seul exe)
    class Tool
    {
        public string Name, Hint, Glyph; public Func<Form> Make;
        public bool Available { get { return Make != null; } }
        public void Launch(MainForm shell)
        {
            try
            {
                var f = Make(); if (f != null) f.Show();
            }
            catch (Exception ex) { Log.Write(ex.ToString()); CyberForm.Warn(shell, Name + " : " + ex.Message); }
        }
    }

    static class Tools
    {
        public static readonly List<Tool> All = new List<Tool>
        {
            new Tool { Name = "DiagPoste", Glyph = Theme.Icon("", "⚕"), Hint = "diagnostic + rapport ticket", Make = () => new DiagPoste.MainForm() },
            new Tool { Name = "MaintenancePC", Glyph = Theme.Icon("", "⚒"), Hint = "rapide / complète + pilotes", Make = () => new MaintenancePC.MainForm() },
            new Tool { Name = "PrépaPoste", Glyph = Theme.Icon("", "▣"), Hint = "PC neuf en un clic", Make = () => new PrepaPoste.MainForm() },
            new Tool { Name = "ProfilSave", Glyph = Theme.Icon("", "☺"), Hint = "sauvegarde profil (chiffrée)", Make = () => new ProfilSave.MainForm() },
            new Tool { Name = "TestReseau", Glyph = Theme.Icon("", "⇄"), Hint = "verdict réseau en 20 s", Make = () => new TestReseau.MainForm() },
            new Tool { Name = "ScanParc", Glyph = Theme.Icon("", "⌖"), Hint = "inventaire réseau complet", Make = () => new ScanParc.MainForm() },
            new Tool { Name = "MailCheck", Glyph = Theme.Icon("", "✉"), Hint = "un domaine en détail", Make = () => new MailCheck.MainForm() },
            new Tool { Name = "TriFichiers", Glyph = Theme.Icon("", "▤"), Hint = "doublons + rangement", Make = () => new TriFichiers.MainForm() },
            new Tool { Name = "NasConnect", Glyph = Theme.Icon("", "◫"), Hint = "parc NAS, santé, lecteurs", Make = () => { Modules.Nas(); return null; } },
            new Tool { Name = "NetSwitch", Glyph = Theme.Icon("", "⇆"), Hint = "DHCP / IP fixe (menu de l'icône)", Make = () => { if (TrayHost.Icon != null) TrayHost.Icon.ContextMenuStrip.Show(Cursor.Position); return null; } },
            new Tool { Name = "Outils VTx", Glyph = Theme.Icon("", "V"), Hint = "VMware / Hyper-V / VBS", Make = () => { Modules.Vtx(); return null; } },
            new Tool { Name = "PowerMenu", Glyph = Theme.Icon("", "⏻"), Hint = "arrêt, redémarrage, programmation", Make = () => { Modules.Power(); return null; } },
        };
    }

    class ToolsPage : Page
    {
        readonly FlowLayoutPanel tiles, win;
        public override string Title { get { return "Outils"; } }

        public ToolsPage()
        {
            Lbl("OUTILS", 0, k(2), "accent", 13f, true);
            Lbl("Toute la suite alex.b est intégrée dans AMI·OPS : un seul exe.", k(110), k(10), "muted");
            tiles = new FlowLayoutPanel { Location = new Point(0, k(40)), AutoScroll = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            foreach (var t in Tools.All)
            {
                var tl = t; var tile = new Tile { Glyph = t.Glyph, Title = t.Name, Hint = t.Hint, Size = new Size(k(170), k(120)) };
                tile.Click += (s, e) => tl.Launch(Shell); tiles.Controls.Add(tile);
            }
            Lbl("// RACCOURCIS WINDOWS", 0, k(320), "accent", 9f, true);
            win = new FlowLayoutPanel { Location = new Point(0, k(344)), AutoSize = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            foreach (var w in new[] {
                new[] { "Gestion de l'ordinateur", "compmgmt.msc", "" }, new[] { "Observateur d'événements", "eventvwr.msc", "" }, new[] { "Moniteur de fiabilité", "perfmon.exe", "/rel" },
                new[] { "Services", "services.msc", "" }, new[] { "Périphériques", "devmgmt.msc", "" }, new[] { "Disques", "diskmgmt.msc", "" }, new[] { "Cartes réseau", "ncpa.cpl", "" },
                new[] { "Programmes", "appwiz.cpl", "" }, new[] { "Imprimantes", "control.exe", "printers" }, new[] { "Pare-feu", "wf.msc", "" }, new[] { "Informations système", "msinfo32.exe", "" },
                new[] { "Gestionnaire des tâches", "taskmgr.exe", "" }, new[] { "Moniteur de ressources", "resmon.exe", "" }, new[] { "Invite de commandes (admin)", "cmd.exe", "" },
                new[] { "PowerShell (admin)", "powershell.exe", "" }, new[] { "Test mémoire Windows", "mdsched.exe", "" }, new[] { "Utilisateurs locaux", "lusrmgr.msc", "" } })
            {
                var ww = w; var c = new Chip(w[0]) { AccentOf = t => t.Accent, Margin = new Padding(0, 0, k(6), k(6)) };
                c.Click += (s, e) => { try { Process.Start(ww[1], ww[2]); } catch (Exception ex) { CyberForm.Warn(this, ex.Message); } };
                win.Controls.Add(c);
            }
            Controls.Add(tiles); Controls.Add(win);
            Resize += (s, e) => { tiles.Size = new Size(ClientSize.Width, k(270)); win.Size = new Size(ClientSize.Width, ClientSize.Height - win.Top); };
        }
    }

    // ===================================================================== Paramètres
    class SettingsPage : Page
    {
        readonly Dictionary<string, TextBox> f = new Dictionary<string, TextBox>();
        readonly NumericUpDown lockMin;
        public override string Title { get { return "Paramètres"; } }

        public SettingsPage()
        {
            Lbl("PARAMÈTRES", 0, k(2), "accent", 13f, true);
            int y = k(44), w = k(520);
            Action<string, string, string, bool> add = (key, label, def, multi) =>
            {
                Lbl(label, 0, y, "muted"); var t = new TextBox { Location = new Point(0, y + k(18)), Width = w, Multiline = multi, Height = multi ? k(54) : k(24), Text = Prefs.Ini.Get(key, def).Replace("\\n", "\r\n") };
                Controls.Add(t); f[key] = t; y += t.Height + k(26);
            };
            add("Tech", "Ton nom (technicien sur les rapports)", "", false);
            add("Societe", "Société émettrice (en-tête des rapports)", "", false);
            add("SocieteInfos", "Adresse / téléphone / e-mail de la société", "", true);
            add("Logo", "Logo (chemin d'un PNG ou JPG)", "", false);
            add("ReportDir", "Dossier des rapports (vide = Documents\\Rapports d'intervention)", "", false);
            add("DataDir", "Dossier des données (vide = profil local ; un partage NAS permet de partager la base)", "", false);
            Lbl("Verrouillage auto du coffre (minutes)", 0, y, "muted");
            lockMin = new NumericUpDown { Minimum = 1, Maximum = 120, Value = Prefs.Ini.GetInt("VaultLockMin", 5), Location = new Point(0, y + k(18)), Width = k(80) };
            Controls.Add(lockMin); y += k(60);
            int x = 0;
            x = Btn("Enregistrer", t => t.Ok, x, y, (s, e) => Save(), true).Right + k(6);
            x = Btn("Logo…", t => t.Muted, x, y, (s, e) => { using (var o = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg" }) if (o.ShowDialog(this) == DialogResult.OK) f["Logo"].Text = o.FileName; }).Right + k(6);
            x = Btn("Ouvrir le dossier des données", t => t.Accent, x, y, (s, e) => Process.Start("explorer.exe", "\"" + Store.Dir + "\"")).Right + k(6);
            x = Btn("Restaurer une sauvegarde…", t => t.Warn, x, y, (s, e) => Restore()).Right + k(6);
            Btn("Journal", t => t.Muted, x, y, (s, e) => Log.Open());
            var cTray = new CheckBox { Text = "Icône près de l'horloge (fermer la fenêtre = réduire)", Checked = Prefs.Ini.GetBool("Tray", true), AutoSize = true, Location = new Point(k(560), k(44)) };
            cTray.CheckedChanged += (s, e) => { Prefs.Ini.Set("Tray", cTray.Checked); Prefs.Save(); if (TrayHost.Icon != null) TrayHost.Icon.Visible = cTray.Checked; };
            var cAuto = new CheckBox { Text = "Démarrer avec Windows (dans la zone de notification, sans invite UAC)", AutoSize = true, Location = new Point(k(560), k(72)) };
            try { cAuto.Checked = TrayHost.AutoStart; } catch { }
            bool autoBusy = false;
            cAuto.CheckedChanged += (s, e) =>
            {
                if (autoBusy) return;
                try { TrayHost.AutoStart = cAuto.Checked; }
                catch (Exception ex) { CyberForm.Warn(this, ex.Message); autoBusy = true; try { cAuto.Checked = TrayHost.AutoStart; } catch { cAuto.Checked = false; } autoBusy = false; }
            };
            var cLink = new CheckBox { Text = "Lecteurs réseau partagés avec l'Explorateur (EnableLinkedConnections, redémarrage)", Checked = TrayHost.LinkedConnections, AutoSize = true, Location = new Point(k(560), k(100)) };
            cLink.CheckedChanged += (s, e) => { try { TrayHost.LinkedConnections = cLink.Checked; CyberForm.Info(this, "Redémarre le PC pour appliquer."); } catch (Exception ex) { CyberForm.Warn(this, ex.Message); } };
            var cUpd = new CheckBox { Text = "Vérifier les mises à jour d'AMI·OPS une fois par jour", Checked = Prefs.Ini.GetBool("UpdateAuto", true), AutoSize = true, Location = new Point(k(560), k(128)) };
            cUpd.CheckedChanged += (s, e) => { Prefs.Ini.Set("UpdateAuto", cUpd.Checked); Prefs.Save(); };
            Controls.AddRange(new Control[] { cTray, cAuto, cLink, cUpd });
            Btn("▶ Autotest d'AMI·OPS (lecture seule)", t => t.Ok, k(560), k(160), (s, e) => Shell.RunSelfTest(), true);
            Lbl("Sauvegarde automatique : une copie par jour (60 jours) dans « sauvegardes » du dossier des données. Écriture atomique : pas de base corrompue en cas de coupure.", 0, y + k(48), "muted");
            Lbl("Base partagée sur un NAS : chaque enregistrement garde la version de l'autre poste en « conflit_… » si deux techniciens modifient en même temps.", 0, y + k(68), "muted");
        }

        void Save()
        {
            var newDir = f["DataDir"].Text.Trim();
            if (newDir.Length > 0 && !Directory.Exists(newDir)) { CyberForm.Warn(this, "Dossier des données introuvable : " + newDir); return; }
            var oldDir = Store.Dir;
            foreach (var kv in f) Prefs.Ini.Set(kv.Key, kv.Value.Text.Trim().Replace("\r\n", "\\n"));
            Prefs.Ini.Set("VaultLockMin", ((int)lockMin.Value).ToString()); Prefs.Save();
            if (!string.Equals(oldDir.TrimEnd('\\'), Store.Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                bool exists = File.Exists(Store.FilePath);
                if (!exists && CyberForm.Ask(this, "Copier tes données actuelles vers le nouveau dossier ?\n" + Store.Dir))
                {
                    foreach (var fl in new[] { "amiops.json", "coffre.json" }) { var src = Path.Combine(oldDir, fl); if (File.Exists(src)) File.Copy(src, Path.Combine(Store.Dir, fl), false); }
                }
                Vault.Lock(); Store.Load();
                CyberForm.Info(this, exists ? "Base existante chargée depuis le nouveau dossier." : "Dossier des données changé.");
            }
            else CyberForm.Info(this, "Paramètres enregistrés.");
        }

        void Restore()
        {
            using (var o = new OpenFileDialog { InitialDirectory = Store.BackupDir, Filter = "Sauvegarde AMI·OPS|amiops_*.json;conflit_*.json" })
            {
                if (o.ShowDialog(this) != DialogResult.OK) return;
                if (!CyberForm.Ask(this, "Remplacer les données actuelles par :\n" + Path.GetFileName(o.FileName) + " ?\n\nLa version actuelle est d'abord copiée dans « sauvegardes »." )) return;
                try
                {
                    Store.Json().Deserialize<Data>(File.ReadAllText(o.FileName, Encoding.UTF8));   // fichier lisible ?
                    File.Copy(Store.FilePath, Path.Combine(Store.BackupDir, "avant-restauration_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".json"), true);
                    File.Copy(o.FileName, Store.FilePath, true); Store.Load();
                    CyberForm.Info(this, "Sauvegarde restaurée.");
                }
                catch (Exception ex) { CyberForm.Warn(this, "Restauration impossible : " + ex.Message); }
            }
        }
    }
}
