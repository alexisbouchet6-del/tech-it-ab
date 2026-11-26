// Tech IT AB - accueil, clients, interventions, temps passé
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

namespace AmiOps
{
    // ===================================================================== Accueil
    class HomePage : Page
    {
        class Alert { public string Level, Title, Detail; public Action Go; }
        readonly Label hello, panne;
        readonly Stat[] stats = new Stat[6];
        readonly Grid todo, recent;
        readonly Label lTodo, lRecent;
        readonly FlowLayoutPanel quick;

        public override string Title { get { return "Accueil"; } }

        public HomePage()
        {
            hello = Lbl("", 0, 0, "text", 15f, true);
            var caps = new[] { "Clients", "Équipements", "Interventions ce mois", "Heures ce mois", "Alertes", "En cours" };
            var keys = new[] { "clients", "clients", "interv", "temps", "accueil", "interv" };
            for (int i = 0; i < 6; i++)
            {
                int ii = i;
                stats[i] = new Stat { Caption = caps[i], Size = new Size(k(170), k(96)) };
                stats[i].Click += (s, e) => { if (ii == 5) { var r = Store.Running(); if (r != null) Shell.EditIntervention(r); else Shell.NewIntervention(null); } else Shell.Go(keys[ii], null); };
                Controls.Add(stats[i]);
            }
            stats[0].AccentOf = t => t.Accent; stats[1].AccentOf = t => t.Accent2; stats[2].AccentOf = t => t.Ok; stats[3].AccentOf = t => t.Accent; stats[4].AccentOf = t => t.Warn; stats[5].AccentOf = t => t.Ok;
            quick = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            Action<string, Func<Theme, Color>, Action, bool> q = (txt, acc, go, prim) => { var c = new Chip(txt) { AccentOf = acc, Primary = prim, Margin = new Padding(0, 0, k(8), 0) }; c.Click += (s, e) => go(); quick.Controls.Add(c); };
            q("＋ Nouvelle intervention", t => t.Ok, () => Shell.NewIntervention(null), true);
            q("⚡ PanneScan de ce PC", t => t.Warn, () => Shell.Go("panne", "run"), false);
            q("▶ Scénarios…", t => t.Accent2, () => { using (var sf = new ScenarioForm(Shell)) sf.ShowDialog(this); }, false);
            q("＋ Client", t => t.Accent, () => Shell.Go("clients", "new"), false);
            q("Scanner le réseau", t => t.Accent, () => Shell.Go("reseau", null), false);
            q("Vérifier tous les domaines", t => t.Accent2, () => Shell.Go("mail", "all"), false);
            q("Ctrl+K", t => t.Muted, () => SendKeys.Send("^k"), false);
            Controls.Add(quick);
            lTodo = Lbl("// À TRAITER", 0, 0, "accent", 9f, true);
            lRecent = Lbl("// DERNIÈRES INTERVENTIONS", 0, 0, "accent", 9f, true);
            todo = new Grid().Col("", 36).Col("Sujet", 300).Col("Détail", 300);
            recent = new Grid().Col("Date", 90).Col("Client", 170).Col("Type", 150).Col("Durée", 70).Col("Résultat", 120);
            todo.DoubleClick += (s, e) => { var a = todo.Sel as Alert; if (a != null && a.Go != null) a.Go(); };
            recent.DoubleClick += (s, e) => { var i = recent.Sel as Intervention; if (i != null) Shell.EditIntervention(i); };
            panne = Lbl("", 0, 0, "muted", 9f);
            panne.Cursor = Cursors.Hand; panne.Click += (s, e) => Shell.Go("panne", null);
            Controls.AddRange(new Control[] { todo, recent });
            Resize += (s, e) => DoLayout();
        }

        void DoLayout()
        {
            int w = ClientSize.Width, y = k(4);
            hello.Location = new Point(0, y); y += k(40);
            int sw = Math.Max(k(130), (w - k(10) * 5) / 6);
            for (int i = 0; i < 6; i++) { stats[i].Location = new Point(i * (sw + k(10)), y); stats[i].Size = new Size(sw, k(96)); }
            y += k(110);
            quick.Location = new Point(0, y); y += k(46);
            int half = (w - k(16)) / 2;
            lTodo.Location = new Point(0, y); lRecent.Location = new Point(half + k(16), y); y += k(22);
            int h = Math.Max(k(120), ClientSize.Height - y - k(30));
            todo.SetBounds(0, y, half, h); recent.SetBounds(half + k(16), y, w - half - k(16), h);
            panne.Location = new Point(0, y + h + k(8));
        }

        static readonly string[][] EndOfSupport =
        {
            new[] { "windows 7", "bad", "Windows 7 : sans mise à jour de sécurité depuis 2020" },
            new[] { "windows 8", "bad", "Windows 8 / 8.1 : sans mise à jour de sécurité depuis janvier 2023" },
            new[] { "windows 10", "bad", "Windows 10 : fin de support le 14/10/2025 (sauf ESU)" },
            new[] { "server 2008", "bad", "Windows Server 2008 : fin de support" },
            new[] { "server 2012", "bad", "Windows Server 2012 / R2 : fin de support le 10/10/2023 (sauf ESU)" },
            new[] { "server 2016", "warn", "Windows Server 2016 : fin de support le 12/01/2027" },
        };

        List<Alert> Alerts()
        {
            var l = new List<Alert>();
            foreach (var i in Store.D.Interventions.Where(x => !x.Done).OrderBy(x => x.Date))
            { var ii = i; l.Add(new Alert { Level = i.Running ? "ok" : "warn", Title = (i.Running ? "En cours : " : "Non clôturée : ") + Store.ClientName(i.ClientId), Detail = i.Date.ToString("dd/MM") + " · " + i.Type + " · " + Fmt(i.TotalMinutes), Go = () => Shell.EditIntervention(ii) }); }
            foreach (var c in Store.D.Clients)
            {
                var cc = c;
                foreach (var d in c.Domains)
                {
                    var m = Store.MailOf(d);
                    if (m == null) l.Add(new Alert { Level = "muted", Title = "Domaine jamais vérifié : " + d, Detail = c.Name, Go = () => Shell.Go("mail", d) });
                    else if (m.Color == "bad" || m.Color == "warn") l.Add(new Alert { Level = m.Color, Title = "Messagerie " + d + " : " + m.Verdict, Detail = c.Name + " · " + m.Detail, Go = () => Shell.Go("mail", d) });
                    else if ((DateTime.Now - m.At).TotalDays > 30) l.Add(new Alert { Level = "muted", Title = "Domaine à revérifier : " + d, Detail = "dernier contrôle le " + m.At.ToString("dd/MM/yyyy"), Go = () => Shell.Go("mail", d) });
                }
                foreach (var a in c.Assets)
                {
                    var os = (a.Os ?? "").ToLowerInvariant();
                    foreach (var e in EndOfSupport)
                        if (os.Contains(e[0]) && !(e[0] == "windows 8" && os.Contains("windows 10")))
                        { l.Add(new Alert { Level = e[1], Title = a.Label + " (" + c.Name + ")", Detail = e[2], Go = () => Shell.Go("clients", cc.Id) }); break; }
                }
                if (c.Contract.Length > 0)
                {
                    var last = Store.LastVisit(c);
                    if ((DateTime.Now - last).TotalDays > 180)
                        l.Add(new Alert { Level = "muted", Title = "Visite de maintenance à planifier : " + c.Name, Detail = c.Contract + " · " + (last.Year > 2000 ? "dernière intervention le " + last.ToString("dd/MM/yyyy") : "aucune intervention"), Go = () => Shell.Go("clients", cc.Id) });
                }
            }
            if (Store.D.LastPanneColor == "bad") l.Add(new Alert { Level = "bad", Title = "Ce PC : " + Store.D.LastPanne, Detail = "PanneScan du " + Store.D.LastPanneAt.ToString("dd/MM HH:mm"), Go = () => Shell.Go("panne", null) });
            if (!Vault.Exists) l.Add(new Alert { Level = "muted", Title = "Coffre des identifiants non créé", Detail = "mots de passe clients chiffrés AES-256", Go = () => Shell.Go("coffre", null) });
            var order = new Dictionary<string, int> { { "bad", 0 }, { "warn", 1 }, { "ok", 2 }, { "muted", 3 } };
            return l.OrderBy(a => order.ContainsKey(a.Level) ? order[a.Level] : 9).ToList();
        }

        public static string Fmt(int minutes) { return (minutes / 60) + " h " + (minutes % 60).ToString("00"); }

        public override void OnShow()
        {
            var now = DateTime.Now; var t = Theme.Current;
            var tech = Prefs.Ini.Get("Tech", "");
            hello.Text = (now.Hour < 18 ? "Bonjour" : "Bonsoir") + (tech.Length > 0 ? " " + tech : "") + "  ·  " + now.ToString("dddd d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
            var month = Store.D.Interventions.Where(i => i.Date.Year == now.Year && i.Date.Month == now.Month).ToList();
            var alerts = Alerts();
            stats[0].Value = Store.D.Clients.Count.ToString(); stats[0].Sub = Store.D.Clients.Count(c => c.Contract.Length > 0) + " sous contrat";
            stats[1].Value = Store.D.Clients.Sum(c => c.Assets.Count).ToString(); stats[1].Sub = Store.D.Clients.Sum(c => c.Assets.Count(a => a.Kind == "NAS")) + " NAS";
            stats[2].Value = month.Count.ToString(); stats[2].Sub = month.Count(i => i.Result.StartsWith("Résolu")) + " résolues";
            stats[3].Value = (month.Sum(i => i.TotalMinutes) / 60.0).ToString("0.#"); stats[3].Sub = Fmt(Store.D.Interventions.Where(i => (now - i.Date).TotalDays < 7).Sum(i => i.TotalMinutes)) + " sur 7 jours";
            stats[4].Value = alerts.Count(a => a.Level == "bad" || a.Level == "warn").ToString(); stats[4].Sub = alerts.Count(a => a.Level == "bad") + " critiques";
            stats[4].AccentOf = alerts.Any(a => a.Level == "bad") ? (Func<Theme, Color>)(x => x.Bad) : (Func<Theme, Color>)(x => x.Warn);
            var r = Store.Running(); stats[5].Value = r != null ? HomePage.Fmt(r.TotalMinutes) : "—"; stats[5].Sub = r != null ? Store.ClientName(r.ClientId) : "clic = démarrer";
            foreach (var s in stats) s.Invalidate();
            todo.BeginUpdate(); todo.Items.Clear();
            foreach (var a in alerts.Take(300))
                todo.Items.Add(new ListViewItem(new[] { a.Level == "bad" ? "●" : a.Level == "warn" ? "▲" : a.Level == "ok" ? "▶" : "·", a.Title, a.Detail }) { Tag = a, ForeColor = t.ByName(a.Level == "muted" ? "text" : a.Level) });
            if (alerts.Count == 0) todo.Items.Add(new ListViewItem(new[] { "✓", "Rien à traiter", "" }) { ForeColor = t.Ok });
            todo.EndUpdate();
            recent.BeginUpdate(); recent.Items.Clear();
            foreach (var i in Store.D.Interventions.OrderByDescending(x => x.Date).ThenByDescending(x => x.Id).Take(40))
                recent.Items.Add(new ListViewItem(new[] { i.Date.ToString("dd/MM/yyyy"), Store.ClientName(i.ClientId), i.Type, Fmt(i.TotalMinutes), i.Done ? i.Result : "en cours" }) { Tag = i, ForeColor = !i.Done ? t.Warn : i.Result.StartsWith("Résolu") ? t.Text : t.Accent2 });
            recent.EndUpdate();
            panne.Text = Store.D.LastPanneAt.Year > 2000 ? "Dernier PanneScan de ce PC (" + Store.D.LastPanneAt.ToString("dd/MM HH:mm") + ") : " + Store.D.LastPanne : "PanneScan jamais lancé sur ce PC — clic pour l'ouvrir";
            panne.ForeColor = t.ByName(Store.D.LastPanneColor.Length > 0 ? Store.D.LastPanneColor : "muted");
            DoLayout();
        }
    }

    // ===================================================================== Clients
    class ClientsPage : Page
    {
        readonly TextBox search;
        readonly Grid list, assets, inters, doms, secrets;
        readonly Label name, sub, empty;
        readonly Panel detail, pFiche, pAssets, pInters, pDoms, pSecrets;
        readonly LogBox fiche;
        readonly Chip[] tabs;
        Client cur;

        public override string Title { get { return "Clients"; } }

        public ClientsPage()
        {
            Lbl("CLIENTS", 0, k(2), "accent", 13f, true);
            search = new TextBox { Location = new Point(0, k(34)), Width = k(250), Font = new Font("Segoe UI", 10f) };
            search.TextChanged += (s, e) => Fill();
            Controls.Add(search);
            Btn("＋ Client", t => t.Ok, k(258), k(30), (s, e) => NewClient(), true);
            Btn("Importer…", t => t.Muted, k(350), k(30), (s, e) => Import());
            list = new Grid().Col("Client", 190).Col("Éq.", 40).Col("Dernière visite", 100);
            list.Location = new Point(0, k(70)); list.Width = k(440); list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            list.SelectedIndexChanged += (s, e) => { var c = list.Sel as Client; if (c != null) ShowClient(c); };
            Controls.Add(list);

            detail = new Panel { Location = new Point(k(456), 0), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Controls.Add(detail);
            empty = new Label { Text = "Sélectionne un client, ou crée le premier avec « ＋ Client ».\r\nAstuce : « Importer… » reprend ta liste de NAS NasConnect ou un CSV de clients.", AutoSize = true, Location = new Point(0, k(40)), Tag = "muted" };
            detail.Controls.Add(empty);
            name = new Label { AutoSize = true, Location = new Point(0, k(2)), Font = new Font("Consolas", 15f, FontStyle.Bold), Tag = "text" };
            sub = new Label { AutoSize = true, Location = new Point(k(2), k(34)), Tag = "muted" };
            detail.Controls.Add(name); detail.Controls.Add(sub);
            int x = 0;
            foreach (var b in new[] { Tuple.Create("Modifier", (Func<Theme, Color>)(t => t.Accent), (Action)EditClient), Tuple.Create("＋ Intervention", (Func<Theme, Color>)(t => t.Ok), (Action)(() => Shell.NewIntervention(cur.Id))), Tuple.Create("Supprimer", (Func<Theme, Color>)(t => t.Bad), (Action)DeleteClient) })
            {
                var c = new Chip(b.Item1) { AccentOf = b.Item2, Location = new Point(x, k(58)) }; var go = b.Item3; c.Click += (s, e) => { if (cur != null) Run(go); };
                detail.Controls.Add(c); x = c.Right + k(8);
            }
            var tn = new[] { "Fiche", "Équipements", "Interventions", "Domaines", "Identifiants" };
            tabs = new Chip[tn.Length]; x = 0;
            for (int i = 0; i < tn.Length; i++)
            {
                int ii = i; tabs[i] = new Chip(tn[i]) { AccentOf = t => t.Accent2, Location = new Point(x, k(98)) };
                tabs[i].Click += (s, e) => Tab(ii); detail.Controls.Add(tabs[i]); x = tabs[i].Right + k(6);
            }
            Func<Panel> mk = () => { var p = new Panel { Location = new Point(0, k(138)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, Visible = false }; detail.Controls.Add(p); return p; };
            pFiche = mk(); pAssets = mk(); pInters = mk(); pDoms = mk(); pSecrets = mk();
            fiche = new LogBox { Dock = DockStyle.Fill }; pFiche.Controls.Add(fiche);

            assets = MkGrid(pAssets, new[] { "Type", "Nom", "IP", "MAC", "Modèle", "N° série", "OS / QC" }, new[] { 90, 140, 105, 125, 130, 110, 160 });
            Bar(pAssets, new[] { "＋ Ajouter", "Modifier", "Supprimer", "Ouvrir (web / DSM)", "Bureau à distance", "Copier l'IP" },
                new Action[] { () => EditAsset(null), () => EditAsset(assets.Sel as Asset), DeleteAsset, () => OpenAsset(assets.Sel as Asset), () => { var a = assets.Sel as Asset; if (a != null && a.Ip.Length > 0) Process.Start("mstsc.exe", "/v:" + a.Ip); }, () => { var a = assets.Sel as Asset; if (a != null) Clip.Text(a.Ip); } });
            assets.DoubleClick += (s, e) => EditAsset(assets.Sel as Asset);

            inters = MkGrid(pInters, new[] { "Date", "Type", "Durée", "Résultat", "Ticket", "Demande" }, new[] { 90, 150, 70, 130, 80, 300 });
            Bar(pInters, new[] { "＋ Nouvelle", "Ouvrir" }, new Action[] { () => Shell.NewIntervention(cur.Id), () => { var i = inters.Sel as Intervention; if (i != null) Shell.EditIntervention(i); } });
            inters.DoubleClick += (s, e) => { var i = inters.Sel as Intervention; if (i != null) Shell.EditIntervention(i); };

            doms = MkGrid(pDoms, new[] { "Domaine", "Verdict", "Vérifié le", "Détail" }, new[] { 180, 260, 110, 300 });
            Bar(pDoms, new[] { "＋ Ajouter", "Retirer", "Vérifier", "Rapport complet" },
                new Action[] { AddDomain, () => { var d = doms.Sel as string; if (d != null && CyberForm.Ask(this, "Retirer " + d + " ?")) { cur.Domains.Remove(d); Store.Save(); ShowClient(cur); } },
                    () => { var d = doms.Sel as string; Shell.Go("mail", d ?? "client:" + cur.Id); }, () => { var d = doms.Sel as string; if (d != null) MailPage.ShowReport(this, d); } });

            secrets = MkGrid(pSecrets, new[] { "Libellé", "Identifiant", "URL", "Modifié" }, new[] { 200, 200, 280, 100 });
            Bar(pSecrets, new[] { "Déverrouiller / ＋ Ajouter", "Copier le mot de passe", "Copier l'identifiant", "Ouvrir l'URL", "Modifier", "Supprimer" },
                new Action[] { AddSecret, () => VaultPage.CopyPass(this, secrets.Sel as Secret), () => { var s = secrets.Sel as Secret; if (s != null) Clip.Text(s.User); },
                    () => { var s = secrets.Sel as Secret; if (s != null && s.Url.StartsWith("http")) Process.Start(s.Url); }, () => { if (VaultPage.Edit(this, secrets.Sel as Secret, cur.Id)) ShowClient(cur); },
                    () => { if (VaultPage.Delete(this, secrets.Sel as Secret)) ShowClient(cur); } });
            secrets.DoubleClick += (s, e) => VaultPage.CopyPass(this, secrets.Sel as Secret);

            Resize += (s, e) => { list.Height = ClientSize.Height - list.Top; detail.Size = new Size(ClientSize.Width - detail.Left, ClientSize.Height); foreach (var p in new[] { pFiche, pAssets, pInters, pDoms, pSecrets }) p.Size = new Size(detail.Width, detail.Height - p.Top); };
            SetDetail(false);
        }

        Grid MkGrid(Panel p, string[] cols, int[] w)
        {
            var g = new Grid { Location = new Point(0, k(40)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            for (int i = 0; i < cols.Length; i++) g.Col(cols[i], w[i]);
            p.Controls.Add(g); p.Resize += (s, e) => g.Size = new Size(p.Width, p.Height - g.Top);
            return g;
        }

        void Bar(Panel p, string[] names, Action[] acts)
        {
            int x = 0;
            for (int i = 0; i < names.Length; i++)
            {
                var a = acts[i]; var c = new Chip(names[i]) { AccentOf = i == 0 ? (Func<Theme, Color>)(t => t.Ok) : (Func<Theme, Color>)(t => t.Accent), Location = new Point(x, 0) };
                c.Click += (s, e) => { if (cur != null) Run(a); }; p.Controls.Add(c); x = c.Right + k(6);
            }
        }

        void SetDetail(bool on) { foreach (Control c in detail.Controls) c.Visible = on && !(c is Panel); empty.Visible = !on; if (on) Tab(Array.FindIndex(tabs, t => t.Active) < 0 ? 0 : Array.FindIndex(tabs, t => t.Active)); else foreach (var p in new[] { pFiche, pAssets, pInters, pDoms, pSecrets }) p.Visible = false; }

        void Tab(int i)
        {
            var ps = new[] { pFiche, pAssets, pInters, pDoms, pSecrets };
            for (int j = 0; j < ps.Length; j++) { ps[j].Visible = j == i; tabs[j].Active = j == i; tabs[j].Invalidate(); }
            CyberForm.Recolor(ps[i]);
        }

        public override void OnShow() { Fill(); if (cur != null && Store.ClientById(cur.Id) != null) ShowClient(cur); else { cur = null; SetDetail(false); } }

        public override void Open(object arg)
        {
            var s = arg as string;
            if (s == "new") { NewClient(); return; }
            var c = Store.ClientById(s); if (c == null) return;
            search.Text = "";
            foreach (ListViewItem it in list.Items) if (it.Tag == c) { it.Selected = true; it.EnsureVisible(); }
            ShowClient(c);
        }

        void Fill()
        {
            var q = search.Text.Trim(); var t = Theme.Current;
            list.BeginUpdate(); list.Items.Clear();
            foreach (var c in Store.D.Clients.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (q.Length > 0 && !Store.Match(q, c.Name, c.Address, c.Phone, c.Email, c.Contract, c.Notes, string.Join(" ", c.Domains), string.Join(" ", c.Assets.Select(a => a.Name + " " + a.Ip + " " + a.Serial + " " + a.QcId + " " + a.Mac)), string.Join(" ", c.Contacts.Select(x => x.Name + " " + x.Phone)))) continue;
                var last = Store.LastVisit(c);
                list.Items.Add(new ListViewItem(new[] { c.Name, c.Assets.Count.ToString(), last.Year > 2000 ? last.ToString("dd/MM/yyyy") : "—" }) { Tag = c, ForeColor = c.Contract.Length > 0 ? t.Accent : t.Text });
                if (cur != null && c.Id == cur.Id) list.Items[list.Items.Count - 1].Selected = true;
            }
            list.EndUpdate();
        }

        void ShowClient(Client c)
        {
            cur = c; SetDetail(true); var t = Theme.Current;
            name.Text = c.Name;
            sub.Text = string.Join("   ·   ", new[] { c.Contract, c.Phone, c.Email, c.Address.Replace("\r\n", ", ") }.Where(x => x.Length > 0));
            fiche.Clear();
            fiche.Line(c.Name, "accent", true);
            if (c.Contract.Length > 0) fiche.Line("Contrat : " + c.Contract, "ok");
            if (c.Address.Length > 0) fiche.Line("Adresse : " + c.Address.Replace("\r\n", ", "), "text");
            if (c.Phone.Length > 0) fiche.Line("Téléphone : " + c.Phone, "text");
            if (c.Email.Length > 0) fiche.Line("E-mail : " + c.Email, "text");
            fiche.Line("", "text"); fiche.Line("// CONTACTS", "accent", true);
            if (c.Contacts.Count == 0) fiche.Line("  aucun", "muted");
            foreach (var x in c.Contacts) fiche.Line("  " + x.Name + (x.Role.Length > 0 ? " (" + x.Role + ")" : "") + (x.Phone.Length > 0 ? " · " + x.Phone : "") + (x.Email.Length > 0 ? " · " + x.Email : ""), "text");
            fiche.Line("", "text"); fiche.Line("// PARC", "accent", true);
            foreach (var g in c.Assets.GroupBy(a => a.Kind)) fiche.Line("  " + g.Count() + " × " + g.Key, "text");
            var ci = Store.D.Interventions.Where(i => i.ClientId == c.Id).ToList();
            fiche.Line("", "text"); fiche.Line("// ACTIVITÉ", "accent", true);
            fiche.Line("  " + ci.Count + " intervention(s), " + HomePage.Fmt(ci.Sum(i => i.TotalMinutes)) + " au total, " + HomePage.Fmt(ci.Where(i => i.Date.Year == DateTime.Now.Year).Sum(i => i.TotalMinutes)) + " cette année", "text");
            if (c.Notes.Length > 0) { fiche.Line("", "text"); fiche.Line("// NOTES", "accent", true); foreach (var l in c.Notes.Split('\n')) fiche.Line("  " + l.TrimEnd('\r'), "text"); }

            assets.BeginUpdate(); assets.Items.Clear();
            foreach (var a in c.Assets.OrderBy(a => a.Kind).ThenBy(a => a.Name))
                assets.Items.Add(new ListViewItem(new[] { a.Kind, a.Name, a.Ip, a.Mac, a.Model, a.Serial, a.QcId.Length > 0 ? "QC " + a.QcId : a.Os }) { Tag = a });
            assets.EndUpdate();
            inters.BeginUpdate(); inters.Items.Clear();
            foreach (var i in ci.OrderByDescending(i => i.Date))
                inters.Items.Add(new ListViewItem(new[] { i.Date.ToString("dd/MM/yyyy"), i.Type, HomePage.Fmt(i.TotalMinutes), i.Done ? i.Result : "en cours", i.Ticket, i.Demande.Replace("\r\n", " ") }) { Tag = i, ForeColor = i.Done ? t.Text : t.Warn });
            inters.EndUpdate();
            doms.BeginUpdate(); doms.Items.Clear();
            foreach (var d in c.Domains)
            {
                var m = Store.MailOf(d);
                doms.Items.Add(new ListViewItem(new[] { d, m != null ? m.Verdict : "jamais vérifié", m != null ? m.At.ToString("dd/MM/yyyy") : "", m != null ? m.Detail : "" }) { Tag = d, ForeColor = m != null ? t.ByName(m.Color) : t.Muted });
            }
            doms.EndUpdate();
            secrets.BeginUpdate(); secrets.Items.Clear();
            if (!Vault.Unlocked) secrets.Items.Add(new ListViewItem(new[] { Vault.Exists ? "Coffre verrouillé : « Déverrouiller »" : "Coffre non créé : « Déverrouiller / ＋ Ajouter » pour le créer", "", "", "" }) { ForeColor = t.Muted });
            else foreach (var s in Vault.Items.Where(x => x.ClientId == c.Id).OrderBy(x => x.Label))
                    secrets.Items.Add(new ListViewItem(new[] { s.Label, s.User, s.Url, s.Updated.ToString("dd/MM/yyyy") }) { Tag = s });
            secrets.EndUpdate();
            tabs[1].SetText("Équipements (" + c.Assets.Count + ")"); tabs[2].SetText("Interventions (" + ci.Count + ")"); tabs[3].SetText("Domaines (" + c.Domains.Count + ")");
            int tx = 0; foreach (var tb in tabs) { tb.Left = tx; tx = tb.Right + k(6); }
            CyberForm.Recolor(detail);
        }

        static readonly string[] Contracts = { "", "Contrat de maintenance", "Infogérance", "Forfait d'heures", "Ponctuel" };

        void NewClient()
        {
            var c = new Client { Id = Store.NewId(), Created = DateTime.Now };
            if (!EditFields(c, "Nouveau client")) return;
            Store.D.Clients.Add(c); Store.Save(); search.Text = ""; Fill(); Open(c.Id);
        }

        void EditClient() { if (EditFields(cur, "Modifier " + cur.Name)) { Store.Save(); Fill(); ShowClient(cur); } }

        bool EditFields(Client c, string title)
        {
            var fName = new F("Nom du client", c.Name) { Required = true };
            var fAddr = new F("Adresse", c.Address) { Multi = true, Lines = 2 };
            var fTel = new F("Téléphone", c.Phone); var fMail = new F("E-mail", c.Email);
            var fCon = new F("Contrat", c.Contract) { Options = Contracts };
            var fDom = new F("Domaines mail (séparés par des virgules)", string.Join(", ", c.Domains));
            var fCts = new F("Contacts — un par ligne : Nom ; rôle ; téléphone ; e-mail", string.Join("\r\n", c.Contacts.Select(x => x.Name + " ; " + x.Role + " ; " + x.Phone + " ; " + x.Email))) { Multi = true, Lines = 4 };
            var fNotes = new F("Notes (accès, horaires, particularités…)", c.Notes) { Multi = true, Lines = 4 };
            if (!Dlg.Edit(this, title, fName, fAddr, fTel, fMail, fCon, fDom, fCts, fNotes)) return false;
            var dup = Store.D.Clients.FirstOrDefault(x => x != c && x.Name.Equals(fName.Value, StringComparison.CurrentCultureIgnoreCase));
            if (dup != null && !CyberForm.Ask(this, "Un client « " + dup.Name + " » existe déjà. Continuer quand même ?")) return false;
            c.Name = fName.Value; c.Address = fAddr.Value; c.Phone = fTel.Value; c.Email = fMail.Value; c.Contract = fCon.Value; c.Notes = fNotes.Value;
            c.Domains = fDom.Value.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(MailPage.CleanDomain).Where(d => d.Length > 0).Distinct().ToList();
            c.Contacts = fCts.Value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => { var p = l.Split(';').Select(x => x.Trim()).ToArray(); return new Contact { Name = p[0], Role = p.Length > 1 ? p[1] : "", Phone = p.Length > 2 ? p[2] : "", Email = p.Length > 3 ? p[3] : "" }; }).ToList();
            return true;
        }

        void DeleteClient()
        {
            int n = Store.D.Interventions.Count(i => i.ClientId == cur.Id);
            if (!CyberForm.Ask(this, "Supprimer le client « " + cur.Name + " » ?" + (n > 0 ? "\n\nSes " + n + " intervention(s) sont conservées (client « supprimé »)." : "") + "\nUne sauvegarde quotidienne permet de revenir en arrière.")) return;
            Store.D.Clients.Remove(cur); cur = null; Store.Save(); Fill(); SetDetail(false);
        }

        public static readonly string[] Kinds = { "PC", "Portable", "Serveur", "NAS", "Téléphone IP", "Imprimante", "Box / routeur", "Switch / Wi-Fi", "Caméra", "Autre" };

        void EditAsset(Asset a)
        {
            bool isNew = a == null; if (isNew) a = new Asset { Id = Store.NewId(), Added = DateTime.Now };
            var f = new[] { new F("Type", a.Kind) { Options = Kinds }, new F("Nom", a.Name), new F("Adresse IP", a.Ip), new F("Adresse MAC", a.Mac), new F("Modèle", a.Model), new F("N° de série", a.Serial), new F("OS / firmware (ex. Windows 11 Pro 24H2, DSM 7.2)", a.Os), new F("ID QuickConnect (NAS)", a.QcId), new F("Notes", a.Notes) { Multi = true } };
            if (!Dlg.Edit(this, isNew ? "Nouvel équipement" : "Modifier " + a.Label, f)) return;
            a.Kind = f[0].Value.Length > 0 ? f[0].Value : "Autre"; a.Name = f[1].Value; a.Ip = f[2].Value; a.Mac = f[3].Value.ToUpperInvariant(); a.Model = f[4].Value; a.Serial = f[5].Value; a.Os = f[6].Value; a.QcId = f[7].Value; a.Notes = f[8].Value;
            if (isNew) cur.Assets.Add(a);
            Store.Save(); ShowClient(cur);
        }

        void DeleteAsset() { var a = assets.Sel as Asset; if (a != null && CyberForm.Ask(this, "Supprimer " + a.Label + " ?")) { cur.Assets.Remove(a); Store.Save(); ShowClient(cur); } }

        public static void OpenAsset(Asset a)
        {
            if (a == null) return;
            string url = null;
            if (a.QcId.Length > 0) url = "https://" + a.QcId + ".quickconnect.to";
            else if (a.Ip.Length > 0) url = a.Kind == "NAS" ? "https://" + a.Ip + ":5001" : "http://" + a.Ip;
            if (url != null) try { Process.Start(url); } catch { }
        }

        void AddDomain()
        {
            var d = Dlg.Ask(this, "Domaine", "Domaine mail du client (ex. lba-securite.com)", "", false);
            if (d == null) return; d = MailPage.CleanDomain(d);
            if (d.Length == 0) { CyberForm.Warn(this, "Domaine invalide."); return; }
            if (!cur.Domains.Contains(d)) cur.Domains.Add(d);
            Store.Save(); ShowClient(cur);
        }

        void AddSecret()
        {
            if (!VaultPage.EnsureOpen(this)) { ShowClient(cur); return; }
            VaultPage.Edit(this, null, cur.Id); ShowClient(cur);
        }

        // ---------------------------------------------------------------- import NasConnect / CSV
        void Import()
        {
            var nas = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NasConnect", "nas.txt");
            var choice = File.Exists(nas) && CyberForm.Ask(this, "Liste NasConnect trouvée :\n" + nas + "\n\nImporter tes NAS (un client par nom de client, NAS ajouté à sa fiche) ?\n\n« Non » = importer un CSV de clients à la place.");
            int nc = 0, na = 0;
            if (choice)
            {
                foreach (var raw in File.ReadAllLines(nas, Encoding.UTF8))
                {
                    var l = raw.Trim(); if (l.Length == 0 || l.StartsWith("#")) continue;
                    var p = l.Split('|'); if (p.Length < 3) continue;
                    string client = p[0].Trim(), nasName = p[1].Trim(), qc = p[2].Trim(), local = p.Length > 3 ? p[3].Trim() : "";
                    if (client.Length == 0) client = nasName;
                    var c = Store.D.Clients.FirstOrDefault(x => x.Name.Equals(client, StringComparison.CurrentCultureIgnoreCase));
                    if (c == null) { c = new Client { Id = Store.NewId(), Name = client, Created = DateTime.Now }; Store.D.Clients.Add(c); nc++; }
                    if (c.Assets.Any(a => a.Kind == "NAS" && ((qc.Length > 0 && a.QcId.Equals(qc, StringComparison.OrdinalIgnoreCase)) || a.Name.Equals(nasName, StringComparison.OrdinalIgnoreCase)))) continue;
                    c.Assets.Add(new Asset { Id = Store.NewId(), Kind = "NAS", Name = nasName, QcId = qc, Ip = Regex.IsMatch(local, @"^\d+\.\d+\.\d+\.\d+$") ? local : "", Notes = local.Length > 0 && !Regex.IsMatch(local, @"^\d+\.\d+\.\d+\.\d+$") ? "Adresse locale : " + local : "", Added = DateTime.Now });
                    na++;
                }
            }
            else
            {
                using (var d = new OpenFileDialog { Filter = "CSV (Nom;Adresse;Téléphone;E-mail;Contrat;Domaines)|*.csv;*.txt" })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    foreach (var raw in File.ReadAllLines(d.FileName, Encoding.Default).Skip(1))
                    {
                        var p = raw.Split(raw.Contains(";") ? ';' : ',').Select(x => x.Trim().Trim('"')).ToArray();
                        if (p.Length == 0 || p[0].Length == 0) continue;
                        if (Store.D.Clients.Any(x => x.Name.Equals(p[0], StringComparison.CurrentCultureIgnoreCase))) continue;
                        var c = new Client { Id = Store.NewId(), Name = p[0], Created = DateTime.Now, Address = p.Length > 1 ? p[1] : "", Phone = p.Length > 2 ? p[2] : "", Email = p.Length > 3 ? p[3] : "", Contract = p.Length > 4 ? p[4] : "" };
                        if (p.Length > 5) c.Domains = p[5].Split(new[] { ' ', ',', '|' }, StringSplitOptions.RemoveEmptyEntries).Select(MailPage.CleanDomain).Where(x => x.Length > 0).ToList();
                        Store.D.Clients.Add(c); nc++;
                    }
                }
            }
            Store.Save(); Fill();
            CyberForm.Info(this, nc + " client(s) créé(s)" + (choice ? ", " + na + " NAS ajouté(s)." : ".") + "\nLes mots de passe NasConnect restent dans NasConnect (Gestionnaire d'identification Windows).");
        }
    }

    // ===================================================================== Interventions
    class InterventionsPage : Page
    {
        readonly ComboBox period; readonly TextBox search; readonly Grid grid; readonly Label total;
        public override string Title { get { return "Interventions"; } }

        public InterventionsPage()
        {
            Lbl("INTERVENTIONS", 0, k(2), "accent", 13f, true);
            period = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(0, k(36)), Width = k(160) };
            period.Items.AddRange(new object[] { "Toutes", "Non clôturées", "Ce mois", "Mois dernier", "Cette année" });
            period.SelectedIndex = 0; period.SelectedIndexChanged += (s, e) => OnShow();
            search = new TextBox { Location = new Point(k(170), k(36)), Width = k(240) }; search.TextChanged += (s, e) => OnShow();
            Controls.Add(period); Controls.Add(search);
            int x = k(424);
            foreach (var b in new[] { Tuple.Create("＋ Nouvelle", (Action)(() => Shell.NewIntervention(null))), Tuple.Create("Ouvrir", (Action)(() => { var i = grid.Sel as Intervention; if (i != null) Shell.EditIntervention(i); })),
                Tuple.Create("Rapport PDF", (Action)(() => { var i = grid.Sel as Intervention; if (i != null) Report.Open(this, i); })), Tuple.Create("Note Station", (Action)(() => { var i = grid.Sel as Intervention; if (i != null) { Clip.Text(Report.Text(i)); CyberForm.Info(this, "Texte copié : colle-le dans le ticket Note Station."); } })),
                Tuple.Create("→ Ticket GLPI", (Action)(() => { var i = grid.Sel as Intervention; if (i == null) return; if (!Glpi.Configured) { CyberForm.Info(this, "Configure GLPI dans Connecteurs."); return; } 
                    // ticket déjà lié : on le complète (compte rendu en suivi + tâche avec le temps passé, résolution si résolu) au lieu d'en créer un autre
                    if (i.GlpiTicket > 0)
                    {
                        if (!CyberForm.Ask(this, "Compléter le ticket GLPI n°" + i.GlpiTicket + " avec ce compte rendu et " + HomePage.Fmt(i.TotalMinutes) + " de temps passé ?")) return;
                        Jobs.Run("Ticket GLPI n°" + i.GlpiTicket, null, j => { Glpi.Session(t => { Glpi.Followup(t, i.GlpiTicket, Glpi.H2(Report.Text(i))); Glpi.Task(t, i.GlpiTicket, Glpi.H2(i.Type + " : " + (i.Actions.Length > 0 ? i.Actions : i.Demande)), i.TotalMinutes); if (i.Result == "Résolu") Glpi.Solve(t, i.GlpiTicket, Glpi.H2(i.Actions.Length > 0 ? i.Actions : "Résolu.")); return 0; }); j.Log("Ticket GLPI n°" + i.GlpiTicket + " complété.", "ok"); });
                        return;
                    }
                    Jobs.Run("Ticket GLPI " + Store.ClientName(i.ClientId), null, j => { var id = Glpi.Ticket(i); i.GlpiTicket = id; Store.Save(); j.Log("Ticket GLPI n°" + id + " créé.", "ok"); BeginInvoke(new Action(() => CyberForm.Info(this, "Ticket GLPI n°" + id + " créé."))); }); })),
                Tuple.Create("Export CSV", (Action)ExportCsv), Tuple.Create("Supprimer", (Action)Delete) })
            {
                var go = b.Item2; var c = Btn(b.Item1, b.Item1.StartsWith("＋") ? (Func<Theme, Color>)(t => t.Ok) : b.Item1 == "Supprimer" ? (Func<Theme, Color>)(t => t.Bad) : (Func<Theme, Color>)(t => t.Accent), x, k(32), (s, e) => Run(go), b.Item1.StartsWith("＋"));
                x = c.Right + k(6);
            }
            grid = new Grid().Col("Date", 90).Col("Client", 200).Col("Type", 170).Col("Lieu", 90).Col("Durée", 70).Col("Résultat", 150).Col("Ticket", 80).Col("Demande", 300);
            grid.Location = new Point(0, k(74)); grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            grid.DoubleClick += (s, e) => { var i = grid.Sel as Intervention; if (i != null) Shell.EditIntervention(i); };
            Controls.Add(grid);
            total = Lbl("", 0, 0, "muted"); total.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            Resize += (s, e) => { grid.Size = new Size(ClientSize.Width, ClientSize.Height - grid.Top - k(26)); total.Location = new Point(0, ClientSize.Height - k(20)); };
        }

        IEnumerable<Intervention> Filtered()
        {
            var now = DateTime.Now; var lm = now.AddMonths(-1); var q = search.Text.Trim();
            return Store.D.Interventions.Where(i =>
                (period.SelectedIndex == 0 || (period.SelectedIndex == 1 && !i.Done) || (period.SelectedIndex == 2 && i.Date.Year == now.Year && i.Date.Month == now.Month) ||
                 (period.SelectedIndex == 3 && i.Date.Year == lm.Year && i.Date.Month == lm.Month) || (period.SelectedIndex == 4 && i.Date.Year == now.Year)) &&
                (q.Length == 0 || Store.Match(q, Store.ClientName(i.ClientId), i.Type, i.Ticket, i.Demande, i.Diag, i.Actions, i.Materiel, i.Result, i.Tech)))
                .OrderByDescending(i => i.Date);
        }

        public override void OnShow()
        {
            var t = Theme.Current; var l = Filtered().ToList();
            grid.BeginUpdate(); grid.Items.Clear();
            foreach (var i in l)
                grid.Items.Add(new ListViewItem(new[] { i.Date.ToString("dd/MM/yyyy"), Store.ClientName(i.ClientId), i.Type, i.Lieu, HomePage.Fmt(i.TotalMinutes), i.Done ? i.Result : i.Running ? "● en cours" : "à clôturer", i.Ticket, i.Demande.Replace("\r\n", " ") })
                { Tag = i, ForeColor = !i.Done ? t.Warn : i.Result.StartsWith("Résolu") ? t.Text : t.Accent2 });
            grid.EndUpdate();
            total.Text = l.Count + " intervention(s) · " + HomePage.Fmt(l.Sum(i => i.TotalMinutes)) + " · Ctrl+N = nouvelle intervention (chrono lancé)";
        }

        void Delete()
        {
            var i = grid.Sel as Intervention; if (i == null) return;
            if (!CyberForm.Ask(this, "Supprimer l'intervention du " + i.Date.ToString("dd/MM/yyyy") + " chez " + Store.ClientName(i.ClientId) + " ?")) return;
            Store.D.Interventions.Remove(i); Store.Save(); OnShow();
        }

        void ExportCsv()
        {
            using (var d = new SaveFileDialog { Filter = "CSV (Excel)|*.csv", FileName = "Interventions_" + DateTime.Now.ToString("yyyy-MM-dd") + ".csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder("Date;Client;Type;Lieu;Minutes;Heures;Résultat;Ticket;Technicien;Demande;Actions;Matériel\r\n");
                foreach (var i in Filtered())
                    sb.Append(string.Join(";", new[] { i.Date.ToString("dd/MM/yyyy"), Store.ClientName(i.ClientId), i.Type, i.Lieu, i.TotalMinutes.ToString(), (i.TotalMinutes / 60.0).ToString("0.00"), i.Result, i.Ticket, i.Tech, i.Demande, i.Actions, i.Materiel }.Select(Csv))).Append("\r\n");
                File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(true));
                CyberForm.Info(this, "Export : " + d.FileName);
            }
        }

        public static string Csv(string s) { s = (s ?? "").Replace("\r\n", " / ").Replace("\n", " / "); if (s.Length > 0 && "=+-@".IndexOf(s[0]) >= 0) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
    }

    // ===================================================================== Fiche d'intervention
    class InterventionForm : CyberForm
    {
        readonly Intervention it;
        readonly ComboBox cClient, cAsset, cType, cLieu, cRes;
        readonly TextBox tTicket, tTech, tSigner, tDemande, tDiag, tActions, tMat, tReco;
        readonly DateTimePicker dDate;
        readonly NumericUpDown nMin;
        readonly Label lChrono;
        readonly Chip bChrono;
        readonly RapportInter.SignPad sign;
        readonly ListBox quick;
        bool clearSig;

        static string QuickFile { get { return Path.Combine(AppInfo.DataDir, "actions-frequentes.txt"); } }
        public static readonly string[] Types = { "Dépannage", "Maintenance préventive", "Installation / mise en service", "NAS / sauvegarde", "Réseau / Wi-Fi", "Téléphonie VoIP", "Messagerie", "Sécurité / antivirus", "Conseil / formation", "Autre" };
        public static readonly string[] Results = { "Résolu", "Partiellement résolu", "Non résolu — à suivre", "En attente de pièce / devis" };

        public InterventionForm(Intervention i) : base("INTERVENTION", 1240, 860)
        {
            it = i; Func<float, int> k = Theme.S;
            SetSubtitle("Fiche d'intervention  //  chrono, signature, rapport PDF  //  by alex.b");
            int x1 = k(16), w1 = k(330), y = k(8);
            Action<string, Control, int> add = (label, c, h) =>
            {
                Body.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(x1, y), Tag = "muted" });
                c.Location = new Point(x1, y + k(17)); c.Width = w1; if (h > 0) c.Height = h; Body.Controls.Add(c); y += c.Height + k(24);
            };
            cClient = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            var clients = Store.D.Clients.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            cClient.Items.Add("(choisir un client)"); foreach (var c in clients) cClient.Items.Add(c.Name); cClient.Items.Add("＋ nouveau client…");
            int ci = clients.FindIndex(c => c.Id == i.ClientId); cClient.SelectedIndex = ci >= 0 ? ci + 1 : 0;
            add("Client *", cClient, 0);
            cAsset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; add("Équipement concerné", cAsset, 0);
            cClient.SelectedIndexChanged += (s, e) =>
            {
                if (cClient.SelectedIndex == cClient.Items.Count - 1)
                {
                    var n = Dlg.Ask(this, "Nouveau client", "Nom du client", "", false);
                    if (n != null) { var c = new Client { Id = Store.NewId(), Name = n, Created = DateTime.Now }; Store.D.Clients.Add(c); Store.Save(); cClient.Items.Insert(cClient.Items.Count - 1, n); cClient.SelectedIndex = cClient.Items.Count - 2; }
                    else cClient.SelectedIndex = 0;
                    return;
                }
                FillAssets();
            };
            tTicket = new TextBox { Text = i.Ticket }; add("N° de ticket", tTicket, 0);
            cType = Combo(Types, i.Type); add("Type", cType, 0);
            cLieu = Combo(new[] { "Sur site", "À distance", "En atelier" }, i.Lieu); add("Lieu", cLieu, 0);
            tTech = new TextBox { Text = i.Tech }; add("Technicien", tTech, 0);
            dDate = new DateTimePicker { Format = DateTimePickerFormat.Short, Value = i.Date.Year > 2000 ? i.Date : DateTime.Today }; add("Date", dDate, 0);
            // chrono
            Body.Controls.Add(new Label { Text = "Temps passé", AutoSize = true, Location = new Point(x1, y), Tag = "muted" });
            lChrono = new Label { AutoSize = true, Location = new Point(x1, y + k(16)), Font = new Font("Consolas", 20f, FontStyle.Bold), Tag = "ok" };
            bChrono = new Chip("") { Location = new Point(x1 + k(170), y + k(18)) };
            nMin = new NumericUpDown { Minimum = 0, Maximum = 100000, Location = new Point(x1 + k(270), y + k(22)), Width = k(60), Value = Math.Min(100000, i.TotalMinutes) };
            Body.Controls.AddRange(new Control[] { lChrono, bChrono, nMin });
            Body.Controls.Add(new Label { Text = "min", AutoSize = true, Location = new Point(x1 + k(270), y + k(48)), Tag = "muted" });
            bChrono.Click += (s, e) => ToggleChrono();
            nMin.ValueChanged += (s, e) => { if (!it.Running) it.Minutes = (int)nMin.Value; };
            y += k(74);
            cRes = Combo(Results, i.Result); add("Résultat", cRes, 0);
            tSigner = new TextBox { Text = i.Signer }; add("Nom du signataire", tSigner, 0);
            sign = new RapportInter.SignPad { Location = new Point(x1, y), Size = new Size(w1, k(100)) };
            var bClr = new Chip("Effacer la signature") { AccentOf = t => t.Muted, Location = new Point(x1, y + k(104)) };
            bClr.Click += (s, e) => { sign.Clear(); clearSig = true; };
            Body.Controls.Add(sign); Body.Controls.Add(bClr);

            // colonne centrale
            int x2 = x1 + w1 + k(18), w2 = k(560); y = k(8);
            Func<string, string, int, TextBox> big = (label, val, h) =>
            {
                Body.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(x2, y), Tag = "muted" });
                var t = new TextBox { Text = val, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Location = new Point(x2, y + k(17)), Size = new Size(w2, k(h)) };
                Body.Controls.Add(t); y += k(h) + k(26); return t;
            };
            tDemande = big("Demande / symptôme *", i.Demande, 64);
            tDiag = big("Diagnostic", i.Diag, 120);
            tActions = big("Actions réalisées  (double-clic sur une action fréquente →)", i.Actions, 170);
            tMat = big("Matériel / pièces / licences", i.Materiel, 54);
            tReco = big("Recommandations / suite à donner", i.Reco, 70);

            // colonne droite
            int x3 = x2 + w2 + k(18), w3 = k(270); y = k(8);
            Func<string, Func<Theme, Color>, Action, Chip> btn = (txt, acc, go) => { var c = new Chip(txt) { AccentOf = acc, Location = new Point(x3, y) }; c.Click += (s, e) => go(); Body.Controls.Add(c); y += k(36); return c; };
            btn("✓  CLÔTURER + RAPPORT PDF", t => t.Ok, () => Finish(true)).Primary = true;
            btn("Enregistrer et fermer", t => t.Accent, () => Close());
            btn("Copier pour Note Station", t => t.Accent2, () => { Collect(); Clip.Text(Report.Text(it)); });
            btn("Insérer : infos de ce poste", t => t.Accent, () => Append(tDiag, Report.PcInfo()));
            btn("Insérer : dernier PanneScan", t => t.Warn, () => { if (Store.D.LastPanneText.Length > 0) Append(tDiag, Store.D.LastPanneText); else Info(this, "Lance d'abord un PanneScan (menu PanneScan)."); });
            y += k(6);
            Body.Controls.Add(new Label { Text = "Actions fréquentes", AutoSize = true, Location = new Point(x3, y), Tag = "accent" }); y += k(18);
            quick = new ListBox { Location = new Point(x3, y), Size = new Size(w3, k(330)), IntegralHeight = false, HorizontalScrollbar = true };
            LoadQuick();
            quick.DoubleClick += (s, e) => { if (quick.SelectedItem != null) Append(tActions, "- " + quick.SelectedItem); };
            Body.Controls.Add(quick); y += k(336);
            var bq = new Chip("Modifier la liste") { AccentOf = t => t.Muted, Location = new Point(x3, y) };
            bq.Click += (s, e) => { LoadQuick(); var p = Process.Start("notepad.exe", "\"" + QuickFile + "\""); if (p != null) { p.EnableRaisingEvents = true; p.Exited += (a, b) => BeginInvoke(new Action(LoadQuick)); } };
            Body.Controls.Add(bq);

            FillAssets();
            var timer = new Timer { Interval = 1000 }; timer.Tick += (s, e) => UpdateChrono(); timer.Start();
            FormClosing += (s, e) => { timer.Stop(); Collect(); };
            UpdateChrono();
        }

        static ComboBox Combo(string[] items, string val)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; c.Items.AddRange(items);
            int i = Array.IndexOf(items, val); c.SelectedIndex = i >= 0 ? i : 0; return c;
        }

        Client SelClient()
        {
            if (cClient.SelectedIndex <= 0 || cClient.SelectedIndex >= cClient.Items.Count - 1) return null;
            var n = (string)cClient.SelectedItem;
            return Store.D.Clients.FirstOrDefault(c => c.Name == n);
        }

        void FillAssets()
        {
            cAsset.Items.Clear(); cAsset.Items.Add("(aucun)");
            var c = SelClient(); if (c == null) { cAsset.SelectedIndex = 0; return; }
            int sel = 0;
            foreach (var a in c.Assets) { cAsset.Items.Add(a.Label); if (a.Id == it.AssetId) sel = cAsset.Items.Count - 1; }
            cAsset.SelectedIndex = sel;
        }

        void LoadQuick()
        {
            if (!File.Exists(QuickFile))
                File.WriteAllText(QuickFile,
                    "# " + Ed.Name + " (by alex.b) - actions fréquentes, une par ligne\r\n" +
                    "Diagnostic matériel et logiciel du poste (PanneScan)\r\nNettoyage des fichiers temporaires et du disque\r\nMises à jour Windows installées\r\nMise à jour des pilotes\r\n" +
                    "Analyse antivirus complète, aucune menace\r\nVérification de l'état des disques (SMART)\r\nRéparation du démarrage (bcdboot)\r\nAnnulation des mises à jour en attente (DISM)\r\n" +
                    "Réparation des fichiers système (DISM + SFC)\r\nSauvegarde des données utilisateur\r\nRestauration des données utilisateur\r\nInstallation et paramétrage de la messagerie\r\n" +
                    "Configuration du profil Outlook\r\nCorrection SPF / DKIM / DMARC du domaine\r\nRemplacement de la charnière\r\nRemplacement du disque par un SSD + clonage\r\nAjout de mémoire vive\r\n" +
                    "Vérification de la sauvegarde du NAS (Hyper Backup)\r\nMise à jour DSM du NAS\r\nRemplacement d'un disque du NAS + reconstruction du RAID\r\n" +
                    "Paramétrage du téléphone IP (compte SIP)\r\nMise à jour du firmware du téléphone\r\nConfiguration du Wi-Fi\r\nPassage en IP fixe\r\nInstallation de l'imprimante réseau\r\n" +
                    "Préparation d'un poste neuf (PrépaPoste)\r\nTest de fonctionnement avec l'utilisateur\r\nExplications données à l'utilisateur\r\n", new UTF8Encoding(false));
            quick.Items.Clear();
            quick.Items.AddRange(File.ReadAllLines(QuickFile, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToArray());
        }

        static void Append(TextBox t, string line) { t.AppendText((t.TextLength > 0 && !t.Text.EndsWith("\n") ? "\r\n" : "") + line + "\r\n"); t.Focus(); }

        void ToggleChrono()
        {
            if (it.Running) { it.Minutes = it.TotalMinutes; it.RunningSince = DateTime.MinValue; }
            else
            {
                var other = Store.Running();
                if (other != null && other != it) { other.Minutes = other.TotalMinutes; other.RunningSince = DateTime.MinValue; }
                it.Minutes = (int)nMin.Value; it.RunningSince = DateTime.Now; it.Done = false;
            }
            Store.Save(); UpdateChrono();
        }

        void UpdateChrono()
        {
            var e = it.Elapsed;
            lChrono.Text = ((int)e.TotalHours).ToString("00") + ":" + e.Minutes.ToString("00") + ":" + e.Seconds.ToString("00");
            lChrono.ForeColor = it.Running ? Theme.Current.Ok : Theme.Current.Muted;
            bChrono.SetText(it.Running ? "❚❚ Pause" : "▶ Reprendre"); bChrono.AccentOf = it.Running ? (Func<Theme, Color>)(t => t.Warn) : (Func<Theme, Color>)(t => t.Ok);
            nMin.Enabled = !it.Running;
            if (it.Running && nMin.Value != it.TotalMinutes) nMin.Value = Math.Min(100000, it.TotalMinutes);
            bChrono.Invalidate();
        }

        void Collect()
        {
            var c = SelClient(); it.ClientId = c != null ? c.Id : it.ClientId;
            it.AssetId = c != null && cAsset.SelectedIndex > 0 && cAsset.SelectedIndex - 1 < c.Assets.Count ? c.Assets[cAsset.SelectedIndex - 1].Id : "";
            it.Ticket = tTicket.Text.Trim(); it.Type = (string)cType.SelectedItem; it.Lieu = (string)cLieu.SelectedItem; it.Tech = tTech.Text.Trim();
            it.Date = dDate.Value.Date; it.Result = (string)cRes.SelectedItem; it.Signer = tSigner.Text.Trim();
            it.Demande = tDemande.Text.Trim(); it.Diag = tDiag.Text.Trim(); it.Actions = tActions.Text.Trim(); it.Materiel = tMat.Text.Trim(); it.Reco = tReco.Text.Trim();
            if (!it.Running) it.Minutes = (int)nMin.Value;
            var png = sign.Png64(); if (png != null) it.Signature = png; else if (clearSig) it.Signature = "";
            if (it.Tech.Length > 0) { Prefs.Ini.Set("Tech", it.Tech); Prefs.Save(); }
        }

        void Finish(bool pdf)
        {
            Collect();
            if (SelClient() == null) { Warn(this, "Choisis le client."); cClient.Focus(); return; }
            if (it.Demande.Length == 0 || it.Actions.Length == 0) { Warn(this, "Renseigne la demande et les actions réalisées."); return; }
            if (it.Running) { it.Minutes = it.TotalMinutes; it.RunningSince = DateTime.MinValue; }
            it.Done = true; Store.Save();
            if (pdf) Report.Open(this, it);
            Close();
        }
    }

    // ===================================================================== Rapport (HTML imprimable en PDF, texte Note Station)
    static class Report
    {
        static string H(string s) { return Fmt.Html(s).Replace("\r\n", "\n").Replace("\n", "<br>"); }

        public static string Dir
        {
            get
            {
                var d = Prefs.Ini.Get("ReportDir", "");
                if (d.Length == 0 || !Directory.Exists(d)) d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rapports d'intervention");
                Directory.CreateDirectory(d); return d;
            }
        }

        public static string Text(Intervention i)
        {
            var sb = new StringBuilder();
            sb.AppendLine("RAPPORT D'INTERVENTION — " + Store.ClientName(i.ClientId) + (i.Ticket.Length > 0 ? " — ticket " + i.Ticket : ""));
            sb.AppendLine(i.Date.ToString("dd/MM/yyyy") + " · " + i.Lieu + " · " + i.Type + " · " + HomePage.Fmt(i.TotalMinutes) + (i.Tech.Length > 0 ? " · " + i.Tech : ""));
            var a = AssetOf(i); if (a != null) sb.AppendLine("Équipement : " + a.Label + (a.Serial.Length > 0 ? " (n° " + a.Serial + ")" : ""));
            Action<string, string> part = (t, v) => { if (v.Length > 0) { sb.AppendLine(); sb.AppendLine(t.ToUpperInvariant()); sb.AppendLine(v); } };
            part("Demande", i.Demande); part("Diagnostic", i.Diag); part("Actions réalisées", i.Actions); part("Matériel", i.Materiel); part("Recommandations", i.Reco);
            sb.AppendLine(); sb.AppendLine("RÉSULTAT : " + i.Result);
            return sb.ToString();
        }

        static Asset AssetOf(Intervention i) { var c = Store.ClientById(i.ClientId); return c == null ? null : c.Assets.FirstOrDefault(a => a.Id == i.AssetId); }

        public static string Html(Intervention i)
        {
            var c = Store.ClientById(i.ClientId) ?? new Client { Name = "(client supprimé)" };
            var co = Prefs.Ini.Get("Societe", ""); var coLine = Prefs.Ini.Get("SocieteInfos", "").Replace("\\n", "\n"); var logo = Prefs.Ini.Get("Logo", "");
            string logoTag = "";
            if (logo.Length > 0 && File.Exists(logo))
                try { var ext = Path.GetExtension(logo).ToLowerInvariant(); if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") logoTag = "<img class=logo src=\"data:image/" + (ext == ".png" ? "png" : "jpeg") + ";base64," + Convert.ToBase64String(File.ReadAllBytes(logo)) + "\">"; } catch { }
            var res = i.Result; var cls = res.StartsWith("Résolu") ? "ok" : res.StartsWith("Partiellement") || res.StartsWith("En attente") ? "warn" : "bad";
            var a = AssetOf(i);
            var sig = i.Signature.Length > 0 && Regex.IsMatch(i.Signature, @"^[A-Za-z0-9+/=]+$") ? i.Signature : null;
            var contact = c.Contacts.FirstOrDefault();
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=fr><head><meta charset=utf-8><title>Rapport d'intervention - ").Append(Fmt.Html(c.Name)).Append(" - ").Append(i.Date.ToString("dd/MM/yyyy")).Append("</title><style>")
              .Append("@page{size:A4;margin:14mm}body{font-family:'Segoe UI',Arial,sans-serif;color:#1a2230;font-size:10.5pt;max-width:780px;margin:24px auto;padding:0 16px}")
              .Append("header{display:flex;justify-content:space-between;align-items:flex-start;border-bottom:3px solid #0084a8;padding-bottom:10px}.logo{max-height:64px;max-width:220px}")
              .Append("h1{font-size:18pt;margin:0;color:#0084a8}.co{font-weight:600}.muted{color:#5f6f80;font-size:9pt}")
              .Append("table.id{width:100%;border-collapse:collapse;margin:14px 0}table.id td{padding:5px 8px;border:1px solid #d6dee8;vertical-align:top}table.id td.k{background:#f1f5f9;width:20%;font-weight:600}")
              .Append("h2{font-size:11pt;margin:16px 0 4px;color:#0084a8;text-transform:uppercase;border-bottom:1px solid #d6dee8;padding-bottom:2px}.bx{line-height:1.45}")
              .Append(".res{display:inline-block;padding:4px 12px;border-radius:4px;font-weight:700}.ok{background:#e3f6ec;color:#007a43}.warn{background:#fff3dc;color:#a35d00}.bad{background:#fde6ea;color:#b0102f}")
              .Append(".sig{display:flex;gap:30px;margin-top:22px}.sig div{flex:1;border:1px solid #d6dee8;padding:8px;min-height:110px}.sig img{max-width:100%;max-height:110px}")
              .Append("footer{margin-top:26px;border-top:1px solid #d6dee8;padding-top:6px;font-size:8pt;color:#8a97a6;text-align:center}@media print{.noprint{display:none}body{margin:0}}")
              .Append(".noprint{background:#0084a8;color:#fff;border:0;padding:8px 16px;border-radius:4px;font-size:10pt;cursor:pointer;float:right}</style></head><body>")
              .Append("<button class=noprint onclick=\"window.print()\">Imprimer / Enregistrer en PDF</button>")
              .Append("<header><div>").Append(logoTag).Append(co.Length > 0 ? "<div class=co>" + Fmt.Html(co) + "</div>" : "").Append(coLine.Length > 0 ? "<div class=muted>" + H(coLine) + "</div>" : "")
              .Append("</div><div style=\"text-align:right\"><h1>RAPPORT D'INTERVENTION</h1><div class=muted>").Append(i.Ticket.Length > 0 ? "Ticket n° " + Fmt.Html(i.Ticket) + " · " : "").Append(i.Date.ToString("dd/MM/yyyy")).Append("</div></div></header>")
              .Append("<table class=id><tr><td class=k>Client</td><td>").Append(Fmt.Html(c.Name)).Append(c.Address.Length > 0 ? "<br><span class=muted>" + H(c.Address) + "</span>" : "").Append("</td><td class=k>Date</td><td>").Append(i.Date.ToString("dd/MM/yyyy")).Append("</td></tr>")
              .Append("<tr><td class=k>Contact</td><td>").Append(contact != null ? Fmt.Html(contact.Name + (contact.Phone.Length > 0 ? " · " + contact.Phone : "")) : "").Append("</td><td class=k>Durée</td><td>").Append(HomePage.Fmt(i.TotalMinutes)).Append("</td></tr>")
              .Append("<tr><td class=k>Type</td><td>").Append(Fmt.Html(i.Type)).Append("</td><td class=k>Lieu</td><td>").Append(Fmt.Html(i.Lieu)).Append("</td></tr>")
              .Append("<tr><td class=k>Équipement</td><td>").Append(a != null ? Fmt.Html(a.Label + (a.Model.Length > 0 ? " — " + a.Model : "") + (a.Serial.Length > 0 ? " — n° " + a.Serial : "")) : "—").Append("</td><td class=k>Technicien</td><td>").Append(Fmt.Html(i.Tech)).Append("</td></tr></table>");
            Action<string, string> part = (t, v) => { if (v.Length > 0) sb.Append("<h2>").Append(t).Append("</h2><div class=bx>").Append(H(v)).Append("</div>"); };
            part("Demande / symptôme", i.Demande); part("Diagnostic", i.Diag); part("Actions réalisées", i.Actions); part("Matériel / pièces", i.Materiel); part("Recommandations", i.Reco);
            sb.Append("<h2>Résultat</h2><span class=\"res ").Append(cls).Append("\">").Append(Fmt.Html(res)).Append("</span>")
              .Append("<div class=sig><div><b>Le technicien</b><br>").Append(Fmt.Html(i.Tech)).Append("</div><div><b>Le client</b> — ").Append(Fmt.Html(i.Signer))
              .Append(sig != null ? "<br><img src=\"data:image/png;base64," + sig + "\">" : "<br><span class=muted>(non signé)</span>").Append("</div></div>")
              .Append("<footer>").Append(co.Length > 0 ? Fmt.Html(co) + " · " : "").Append("Rapport généré le ").Append(DateTime.Now.ToString("dd/MM/yyyy à HH:mm")).Append("</footer></body></html>");
            return sb.ToString();
        }

        static string Safe(string s) { s = Regex.Replace(s ?? "", @"[^\p{L}\p{N}\-_ ]", "").Trim(); return s.Length > 40 ? s.Substring(0, 40) : s; }

        public static void Open(IWin32Window owner, Intervention i)
        {
            try
            {
                var file = Path.Combine(Dir, i.Date.ToString("yyyy-MM-dd") + "_" + Safe(Store.ClientName(i.ClientId)) + (i.Ticket.Length > 0 ? "_" + Safe(i.Ticket) : "") + "_" + i.Id.Substring(0, 4) + ".html");
                File.WriteAllText(file, Html(i), new UTF8Encoding(false));
                Process.Start(file);
            }
            catch (Exception ex) { CyberForm.Warn(owner, "Rapport impossible : " + ex.Message); }
        }

        public static string PcInfo()
        {
            string model = "", serial = "", os = "", cpu = ""; long ram = 0;
            try { using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem")) foreach (ManagementObject o in s.Get()) { model = (o["Manufacturer"] + " " + o["Model"]).Trim(); ram = Convert.ToInt64(o["TotalPhysicalMemory"]); } } catch { }
            try { using (var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS")) foreach (ManagementObject o in s.Get()) serial = (o["SerialNumber"] ?? "").ToString().Trim(); } catch { }
            try { using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor")) foreach (ManagementObject o in s.Get()) cpu = (o["Name"] ?? "").ToString().Trim(); } catch { }
            os = WinVersion();
            return "Poste : " + Environment.MachineName + " — " + model + (serial.Length > 0 ? " — n° série " + serial : "") + " — " + os + " — " + cpu + " — " + AlexB.Fmt.Size(ram);
        }

        public static string WinVersion()
        {
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build; int.TryParse((k.GetValue("CurrentBuildNumber") ?? "0").ToString(), out build);
                    var name = (k.GetValue("ProductName") ?? "Windows").ToString();
                    if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");   // ProductName reste « Windows 10 » sous Windows 11
                    return name + " " + (k.GetValue("DisplayVersion") ?? "") + " (build " + build + ")";
                }
            }
            catch { return Environment.OSVersion.VersionString; }
        }
    }

    // ===================================================================== Temps passé
    class TempsPage : Page
    {
        readonly ComboBox period; readonly Grid grid; readonly Panel chart; readonly Label total;
        List<Tuple<string, int, int>> rows = new List<Tuple<string, int, int>>();
        public override string Title { get { return "Temps passé"; } }

        public TempsPage()
        {
            Lbl("TEMPS PASSÉ", 0, k(2), "accent", 13f, true);
            period = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(0, k(36)), Width = k(180) };
            period.Items.AddRange(new object[] { "Cette semaine", "Ce mois", "Mois dernier", "Cette année", "Année dernière", "Tout" });
            period.SelectedIndex = 1; period.SelectedIndexChanged += (s, e) => OnShow(); Controls.Add(period);
            Btn("Export CSV", t => t.Accent, k(192), k(32), (s, e) => Export());
            total = Lbl("", k(310), k(38), "text", 10f, true);
            grid = new Grid().Col("Client", 220).Col("Interv.", 60).Col("Durée", 80).Col("Part", 60);
            grid.Location = new Point(0, k(74)); grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom; grid.Width = k(440);
            chart = new Panel { Location = new Point(k(456), k(74)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(chart, true, null);
            chart.Paint += DrawChart;
            Controls.Add(grid); Controls.Add(chart);
            Resize += (s, e) => { grid.Height = ClientSize.Height - grid.Top; chart.Size = new Size(ClientSize.Width - chart.Left, ClientSize.Height - chart.Top); chart.Invalidate(); };
        }

        bool InPeriod(DateTime d)
        {
            var n = DateTime.Today; var lm = n.AddMonths(-1);
            switch (period.SelectedIndex)
            {
                case 0: int dow = ((int)n.DayOfWeek + 6) % 7; return d >= n.AddDays(-dow);
                case 1: return d.Year == n.Year && d.Month == n.Month;
                case 2: return d.Year == lm.Year && d.Month == lm.Month;
                case 3: return d.Year == n.Year;
                case 4: return d.Year == n.Year - 1;
                default: return true;
            }
        }

        public override void OnShow()
        {
            var l = Store.D.Interventions.Where(i => InPeriod(i.Date)).ToList();
            rows = l.GroupBy(i => i.ClientId).Select(g => Tuple.Create(Store.ClientName(g.Key), g.Count(), g.Sum(i => i.TotalMinutes))).OrderByDescending(r => r.Item3).ToList();
            int tot = Math.Max(1, rows.Sum(r => r.Item3));
            grid.BeginUpdate(); grid.Items.Clear();
            foreach (var r in rows) grid.Items.Add(new ListViewItem(new[] { r.Item1, r.Item2.ToString(), HomePage.Fmt(r.Item3), (r.Item3 * 100 / tot) + " %" }));
            grid.EndUpdate();
            total.Text = HomePage.Fmt(rows.Sum(r => r.Item3)) + " sur " + l.Count + " intervention(s), " + rows.Count + " client(s)";
            chart.Invalidate();
        }

        void DrawChart(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; var t = Theme.Current; float K = Theme.K;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(t.Bg);
            if (rows.Count == 0) { using (var f = new Font("Segoe UI", 10f)) using (var b = new SolidBrush(t.Muted)) g.DrawString("Aucune intervention sur la période.", f, b, 10, 10); return; }
            int max = rows.Max(r => r.Item3); float y = 4 * K, bh = 24 * K, labelW = 180 * K, w = chart.Width - labelW - 90 * K;
            using (var f = new Font("Segoe UI", 9f)) using (var fv = new Font("Consolas", 9f, FontStyle.Bold))
                foreach (var r in rows.Take((int)(chart.Height / (bh + 8 * K))))
                {
                    using (var b = new SolidBrush(t.Text)) using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                        g.DrawString(r.Item1, f, b, new RectangleF(0, y + 3 * K, labelW - 8 * K, bh), sf);
                    float bw = Math.Max(3 * K, w * r.Item3 / Math.Max(1, max));
                    var rect = new RectangleF(labelW, y, bw, bh);
                    using (var path = Theme.Round(rect, 5 * K)) using (var lg = new LinearGradientBrush(new RectangleF(labelW, y, Math.Max(1, w), bh), t.Accent, t.Accent2, 0f)) g.FillPath(lg, path);
                    using (var b = new SolidBrush(t.Text)) g.DrawString(HomePage.Fmt(r.Item3), fv, b, labelW + bw + 8 * K, y + 4 * K);
                    y += bh + 8 * K;
                }
        }

        void Export()
        {
            using (var d = new SaveFileDialog { Filter = "CSV (Excel)|*.csv", FileName = "Temps_" + period.Text.Replace(' ', '_') + "_" + DateTime.Now.ToString("yyyy-MM-dd") + ".csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder("Client;Interventions;Minutes;Heures\r\n");
                foreach (var r in rows) sb.Append(InterventionsPage.Csv(r.Item1)).Append(";").Append(r.Item2).Append(";").Append(r.Item3).Append(";").Append((r.Item3 / 60.0).ToString("0.00")).Append("\r\n");
                File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(true));
            }
        }
    }
}
