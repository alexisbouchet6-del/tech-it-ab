// Tech IT AB - centre des tâches (exécution parallèle, file d'attente, annulation), scénarios enchaînés, autotest
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlexB;

namespace AmiOps
{
    // ===================================================================== Tâche
    class Job
    {
        public int Id; public string Title = "", Group;   // Group : tâches qui ne doivent pas tourner en même temps (Windows Update, DISM…)
        public string State = "En attente"; public double Progress = -1;
        public DateTime Queued = DateTime.Now, Started, Ended;
        public readonly List<Tuple<string, string>> Lines = new List<Tuple<string, string>>();
        public readonly CancellationTokenSource Cts = new CancellationTokenSource();
        public Action<Job> Work; public Action<string, string> Echo;   // Echo : journal de la page qui a lancé la tâche
        public bool Finished { get { return State == "Terminée" || State == "Échec" || State == "Annulée"; } }

        [ThreadStatic] public static Job Current;
        public void Record(string s, string c) { lock (Lines) Lines.Add(Tuple.Create(s, c)); Jobs.Touch(); }
        public void Log(string s, string c) { lock (Lines) Lines.Add(Tuple.Create(s, c)); if (Echo != null) try { Echo(s, c); } catch { } Jobs.Touch(); }
        public void Check() { if (Cts.IsCancellationRequested) throw new OperationCanceledException(); }
    }

    static class Jobs
    {
        static readonly List<Job> all = new List<Job>(); static int seq; const int MaxParallel = 4;
        public static event Action Changed; static DateTime lastTouch;
        public static List<Job> All { get { lock (all) return all.ToList(); } }
        public static int Running { get { lock (all) return all.Count(j => j.State == "En cours"); } }
        public static Action<Job> Done;   // notification (icône zone de notification)

        public static void Touch() { if ((DateTime.Now - lastTouch).TotalMilliseconds > 250) { lastTouch = DateTime.Now; Fire(); } }
        static void Fire() { var c = Changed; if (c != null) try { c(); } catch { } }

        public static Job Run(string title, string group, Action<Job> work, Action<string, string> echo = null)
        {
            var j = new Job { Id = Interlocked.Increment(ref seq), Title = title, Group = group, Work = work, Echo = echo };
            lock (all) all.Add(j);
            Log.Write("Tâche " + j.Id + " en file : " + title);
            Pump(); Fire(); return j;
        }

        // démarre ce qui peut l'être : au plus 4 en parallèle, une seule par groupe
        static void Pump()
        {
            var start = new List<Job>();
            lock (all)
            {
                int running = all.Count(j => j.State == "En cours");
                foreach (var j in all.Where(x => x.State == "En attente").OrderBy(x => x.Id))
                {
                    if (running >= MaxParallel) break;
                    if (j.Group != null && all.Any(x => x.State == "En cours" && x.Group == j.Group)) continue;
                    j.State = "En cours"; j.Started = DateTime.Now; running++; start.Add(j);
                }
            }
            foreach (var j in start) { var jj = j; new Thread(() => Exec(jj)) { IsBackground = true, Name = "job" + jj.Id }.Start(); }
        }

        static void Exec(Job j)
        {
            Job.Current = j;
            try { j.Check(); j.Work(j); j.State = "Terminée"; }
            catch (OperationCanceledException) { j.State = "Annulée"; j.Log("Tâche annulée.", "warn"); }
            catch (Exception ex) { j.State = "Échec"; j.Log("Erreur : " + ex.Message, "bad"); Log.Write("Tâche " + j.Id + " : " + ex); }
            finally { Job.Current = null; j.Ended = DateTime.Now; j.Progress = 1; Log.Write("Tâche " + j.Id + " " + j.State + " : " + j.Title); }
            var d = Done; if (d != null) try { d(j); } catch { }
            Pump(); Fire();
        }

        public static void Cancel(Job j) { if (j == null || j.Finished) return; if (j.State == "En attente") { j.State = "Annulée"; j.Ended = DateTime.Now; Fire(); Pump(); } else j.Cts.Cancel(); }
        public static void Clear() { lock (all) all.RemoveAll(j => j.Finished); Fire(); }
        public static bool Busy(string title) { lock (all) return all.Any(j => !j.Finished && j.Title == title); }
    }

    // ===================================================================== Page « Tâches »
    class TasksPage : Page
    {
        readonly Grid grid; readonly LogBox box; Job shown; int shownLines; readonly System.Windows.Forms.Timer t;
        public override string Title { get { return "Tâches"; } }

        public TasksPage()
        {
            Lbl("TÂCHES", 0, k(2), "accent", 13f, true);
            Lbl("Toutes les opérations tournent en fond : 4 en parallèle, une seule à la fois pour Windows Update / DISM / réseau. Tu peux changer de page.", k(100), k(10), "muted");
            int x = 0;
            x = Btn("Annuler la tâche", t2 => t2.Bad, x, k(36), (s, e) => Jobs.Cancel(grid.Sel as Job)).Right + k(6);
            x = Btn("Copier son journal", t2 => t2.Accent2, x, k(36), (s, e) => { var j = grid.Sel as Job; if (j != null) lock (j.Lines) Clip.Text(j.Title + "\r\n" + string.Join("\r\n", j.Lines.Select(l => l.Item1))); }).Right + k(6);
            Btn("Effacer les terminées", t2 => t2.Muted, x, k(36), (s, e) => Jobs.Clear());
            grid = new Grid().Col("#", 40).Col("Tâche", 340).Col("État", 100).Col("Progression", 100).Col("Début", 80).Col("Durée", 80);
            grid.Location = new Point(0, k(76)); grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            grid.SelectedIndexChanged += (s, e) => { shown = null; Detail(); };
            box = new LogBox { Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Controls.Add(grid); Controls.Add(box);
            Resize += (s, e) => { int h = Math.Max(k(120), (ClientSize.Height - k(76)) * 40 / 100); grid.Size = new Size(ClientSize.Width, h); box.SetBounds(0, k(84) + h, ClientSize.Width, ClientSize.Height - k(84) - h); };
            t = new System.Windows.Forms.Timer { Interval = 700 }; t.Tick += (s, e) => { if (Visible) { Fill(); Detail(); } }; t.Start();
        }

        public override void OnShow() { Fill(); }

        void Fill()
        {
            var th = Theme.Current; var sel = grid.Sel as Job; var list = Jobs.All.OrderByDescending(j => j.Id).ToList();
            if (sel == null) sel = list.FirstOrDefault(j => j.State == "En cours") ?? list.FirstOrDefault();   // journal visible sans clic
            grid.BeginUpdate();
            if (grid.Items.Count != list.Count) { grid.Items.Clear(); foreach (var j in list) grid.Items.Add(new ListViewItem(new string[6]) { Tag = j }); }
            for (int i = 0; i < list.Count; i++)
            {
                var j = list[i]; var it = grid.Items[i]; it.Tag = j;
                var dur = (j.Finished ? j.Ended : DateTime.Now) - (j.Started.Year > 2000 ? j.Started : DateTime.Now);
                var v = new[] { j.Id.ToString(), j.Title, j.State, j.Progress >= 0 && j.Progress < 1 ? (j.Progress * 100).ToString("0") + " %" : j.State == "En cours" ? "…" : "", j.Started.Year > 2000 ? j.Started.ToString("HH:mm:ss") : "", j.Started.Year > 2000 ? dur.ToString(@"mm\:ss") : "" };
                for (int c = 0; c < 6; c++) if (it.SubItems[c].Text != v[c]) it.SubItems[c].Text = v[c];
                it.ForeColor = j.State == "En cours" ? th.Accent : j.State == "Échec" ? th.Bad : j.State == "Annulée" ? th.Warn : j.State == "Terminée" ? th.Ok : th.Muted;
                it.Selected = j == sel;
            }
            grid.EndUpdate();
        }

        void Detail()
        {
            var j = grid.Sel as Job;
            if (j != shown) { box.Clear(); shown = j; shownLines = 0; if (j != null) box.Line("// " + j.Title, "accent", true); }
            if (j == null) return;
            List<Tuple<string, string>> add; lock (j.Lines) add = j.Lines.Skip(shownLines).ToList();
            foreach (var l in add) box.Line(l.Item1, l.Item2); shownLines += add.Count;
        }
    }

    // ===================================================================== Scénarios enchaînés
#if !POSTE
    class Step { public string Name; public bool On = true; public Action<Job> Run; }

    static class Scenarios
    {
        public static readonly string[] Names = { "PC lent", "Écran bleu", "PC neuf", "Problème réseau", "Fin d'intervention" };

        public static List<Step> Build(string name)
        {
            var l = new List<Step>();
            Action<string, Action<Job>> s = (n, a) => l.Add(new Step { Name = n, Run = a });
            Action<string, Action<Job>> off = (n, a) => l.Add(new Step { Name = n, Run = a, On = false });
            switch (name)
            {
                case "PC lent":
                    s("Point de restauration", j => RestorePoint(j));
                    s("Bilan AVANT (sans test de rapidité)", j => j.Log(Optim.Bilan(false, x => j.Log("  " + x, "muted")).Text(), "text"));
                    s("Nettoyage (temporaires, caches, navigateurs fermés)", j => Optim.Clean(Optim.CleanScan().Where(c => c.Default).ToList(), j.Log));
                    off("Vider la corbeille", j => Optim.Clean(Optim.CleanScan().Where(c => c.Name == "Corbeille").ToList(), j.Log));
                    s("Programmes inutiles au démarrage désactivés", j => { foreach (var i in Startup.Scan(false, false).Where(i => i.Enabled && i.Recommend)) Startup.SetEnabled(i, false, j.Log); });
                    s("Mises à jour Windows + pilotes", j => { string e; var u = Optim.SearchUpdates(out e); if (e != null) j.Log(e, "warn"); else if (u.Count == 0) j.Log("à jour", "ok"); else Optim.InstallUpdates(u, j.Log); });
                    s("Analyse antivirus rapide", j => Optim.AvScan(false, j.Log));
                    s("Bilan APRÈS", j => j.Log(Optim.Bilan(false, x => { }).Text(), "text"));
                    break;
                case "Écran bleu":
                    s("PanneScan 30 jours", j => j.Log(PanneScan.Run(30, x => j.Log("  " + x, "muted")).Text(), "text"));
                    s("Analyse du dernier vidage (pilote en cause)", j => { var d = Bsod.Dumps().FirstOrDefault(); if (d == null) j.Log("aucun vidage mémoire", "warn"); else Bsod.Analyze(d.FullName, j.Log); });
                    s("Santé des disques (SMART)", j => j.Log(Optim.Bilan(false, x => { }).E.Where(e => e.Name.StartsWith("Santé du disque")).Select(e => e.Name + " : " + e.Constat).FirstOrDefault() ?? "", "text"));
                    off("DISM + SFC", j => Repair.DismSfc(j.Log));
                    break;
                case "PC neuf":
                    s("Préparation standard (PrépaPoste, actions recommandées)", j => PrepaDefault(j));
                    s("Audit de sécurité + corrections sûres", j => { var r = Audit.Run(x => j.Log("  " + x, "muted")); foreach (var c in r.Where(c => c.Fix != null && c.Safe)) j.Log("  " + c.Name + " : " + c.Fix(), "ok"); j.Log("Audit : " + (r.Sum(c => c.Points) * 100 / Math.Max(1, r.Sum(c => c.Max))) + "/100 avant corrections", "text"); });
                    s("Bilan final", j => j.Log(Optim.Bilan(false, x => { }).Text(), "text"));
                    break;
                case "Problème réseau":
                    s("Analyse Wi-Fi", j => Wifi.Analyze(j.Log));
                    s("Recherche d'un second serveur DHCP", j => NetTools.Dhcp(j.Log));
                    s("Réseau léger (DNS + renouvellement IP)", j => Repair.Network(false, j.Log));
                    s("Mesure 2 minutes box / Internet / DNS", j => QuickMonitor(j, 120));
                    break;
                case "Fin d'intervention":
                    s("Bilan du poste", j => j.Log(Optim.Bilan(false, x => { }).Text(), "text"));
                    s("Audit de sécurité (sans correction)", j => { var r = Audit.Run(x => { }); j.Log("Audit : " + (r.Sum(c => c.Points) * 100 / Math.Max(1, r.Sum(c => c.Max))) + "/100 — à corriger : " + string.Join(", ", r.Where(c => !c.Ok).Select(c => c.Name)), "text"); });
                    s("Écrans bleus et arrêts brutaux récents", j => j.Log(PanneScan.Run(7, x => { }).Text(), "text"));
                    break;
            }
            return l;
        }

        public static void RestorePoint(Job j)
        {
            string o; int rc = Sh.Ps("Enable-ComputerRestore -Drive ($env:SystemDrive+'\\') -ErrorAction SilentlyContinue;New-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' -Name SystemRestorePointCreationFrequency -Value 0 -PropertyType DWord -Force|Out-Null;Checkpoint-Computer -Description 'AMI-OPS scenario' -RestorePointType MODIFY_SETTINGS", 600000, null, out o);
            j.Log(rc == 0 ? "point de restauration créé" : "point de restauration impossible (protection système désactivée ?)", rc == 0 ? "ok" : "warn");
        }

        static void PrepaDefault(Job j)
        {
            LogBox lb = null; var f = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
            if (f != null) f.Invoke(new Action(() => { lb = new LogBox(); lb.CreateControl(); var h = lb.Handle; }));
            var r = new PrepaPoste.Runner(lb) { NewName = "" };
            foreach (var st in PrepaPoste.Catalog.Build().Where(x => x.Default && x.Id != "rename"))
            {
                j.Check(); j.Log("▶ " + st.Title, "text");
                bool ok; try { ok = st.Run(r); } catch (Exception ex) { j.Log("  " + ex.Message, "bad"); ok = false; }
                j.Log(ok ? "  ✓" : "  ✗ (voir le détail dans PrépaPoste)", ok ? "ok" : "warn");
            }
            if (f != null) f.BeginInvoke(new Action(() => lb.Dispose()));
        }

        static void QuickMonitor(Job j, int seconds)
        {
            var gw = ScanParc.Scanner.Gateway(); var targets = new List<string>(); if (gw != null) targets.Add(gw.ToString()); targets.Add("1.1.1.1");
            var lost = targets.ToDictionary(t => t, t => 0); int dnsKo = 0;
            for (int i = 0; i < seconds; i++)
            {
                j.Check(); j.Progress = (double)i / seconds;
                foreach (var t in targets) try { using (var p = new System.Net.NetworkInformation.Ping()) if (p.Send(t, 1000).Status != System.Net.NetworkInformation.IPStatus.Success) lost[t]++; } catch { lost[t]++; }
                if (i % 10 == 0) { int rc; if (MailCheck.Dns2.Query("www.google.fr", MailCheck.Dns2.A, out rc).Count == 0) dnsKo++; }
                Thread.Sleep(1000);
            }
            foreach (var t in targets) j.Log("  " + (t == targets[0] && gw != null ? "box " : "Internet ") + t + " : " + (lost[t] * 100 / seconds) + " % de pertes", lost[t] > 0 ? "warn" : "ok");
            j.Log("  DNS : " + dnsKo + " échec(s) sur " + (seconds / 10), dnsKo > 0 ? "warn" : "ok");
            j.Log(gw != null && lost[gw.ToString()] > 1 ? "→ problème LOCAL (Wi-Fi / câble / box côté LAN)" : lost["1.1.1.1"] > 1 ? "→ problème BOX ↔ OPÉRATEUR" : dnsKo > 0 ? "→ problème DNS" : "→ pas de coupure pendant la mesure : lancer la surveillance longue (Diagnostic réseau)", "text");
        }

        // lance le scénario comme une tâche et remplit une intervention à la fin
        public static void Launch(MainForm shell, string name, List<Step> steps, string clientId)
        {
            Jobs.Run("Scénario « " + name + " »", "servicing", j =>
            {
                var sb = new StringBuilder(); int n = steps.Count(x => x.On), i = 0;
                foreach (var st in steps.Where(x => x.On))
                {
                    j.Check(); j.Progress = (double)i++ / n; j.Log("", "text"); j.Log("▶ " + st.Name, "accent");
                    int mark; lock (j.Lines) mark = j.Lines.Count;
                    try { st.Run(j); sb.AppendLine("- " + st.Name + " : fait"); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { j.Log("  échec : " + ex.Message, "bad"); sb.AppendLine("- " + st.Name + " : ÉCHEC (" + ex.Message + ")"); }
                    lock (j.Lines) foreach (var l in j.Lines.Skip(mark).Where(l => l.Item2 == "bad" || l.Item2 == "ok" && l.Item1.Length < 160).Take(12)) sb.AppendLine("    " + l.Item1.Trim());
                }
                string diag; lock (j.Lines) diag = string.Join("\r\n", j.Lines.Where(l => l.Item1.StartsWith("BILAN") || l.Item1.StartsWith("PanneScan") || l.Item1.StartsWith("Audit")).Select(l => l.Item1));
                shell.BeginInvoke(new Action(() =>
                {
                    var it = new Intervention { Id = Store.NewId(), ClientId = clientId ?? "", Date = DateTime.Today, Tech = Prefs.Ini.Get("Tech", ""), Type = name == "PC neuf" ? "Installation / mise en service" : name == "Problème réseau" ? "Réseau / Wi-Fi" : "Dépannage",
                        Demande = "Scénario " + Ed.Name + " « " + name + " »", Diag = diag, Actions = sb.ToString().TrimEnd(), Minutes = (int)Math.Max(1, (DateTime.Now - j.Started).TotalMinutes), Result = "Résolu" };
                    Store.D.Interventions.Add(it); try { Store.Save(); } catch { }
                    shell.Notify("Scénario terminé", name + " : intervention préremplie créée (à vérifier et clôturer).");
                }));
            });
        }
    }

    class ScenarioForm : Form
    {
        public ScenarioForm(MainForm shell)
        {
            Func<float, int> k = Theme.S;
            Text = "Scénarios · by alex.b"; Size = new Size(k(720), k(520)); StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Current.Bg; ShowInTaskbar = false; Font = new Font("Segoe UI", 9.5f);
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = k(48), Padding = new Padding(k(8)) };
            var sc = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = k(220) }; sc.Items.AddRange(Scenarios.Names); sc.SelectedIndex = 0;
            var cl = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = k(260) }; cl.Items.Add("(sans client)"); foreach (var c in Store.D.Clients.OrderBy(c => c.Name)) cl.Items.Add(c.Name); cl.SelectedIndex = 0;
            top.Controls.Add(sc); top.Controls.Add(new Label { Text = "client :", AutoSize = true, Margin = new Padding(k(12), k(6), 0, 0), Tag = "muted" }); top.Controls.Add(cl);
            var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10f) };
            List<Step> steps = null;
            Action load = () => { steps = Scenarios.Build((string)sc.SelectedItem); list.Items.Clear(); foreach (var s in steps) list.Items.Add(s.Name, s.On); };
            sc.SelectedIndexChanged += (s, e) => load(); load();
            var go = new Chip("▶ Lancer le scénario (tâche de fond, intervention remplie à la fin)") { AccentOf = t => t.Ok, Dock = DockStyle.Bottom, Primary = true };
            go.Click += (s, e) =>
            {
                for (int i = 0; i < steps.Count; i++) steps[i].On = list.GetItemChecked(i);
                if (!steps.Any(x => x.On)) return;
                var client = Store.D.Clients.FirstOrDefault(c => c.Name == (string)cl.SelectedItem);
                if (!CyberForm.Ask(this, "Lancer « " + sc.SelectedItem + " » (" + steps.Count(x => x.On) + " étapes) ?\nSuivi dans la page Tâches.")) return;
                Scenarios.Launch(shell, (string)sc.SelectedItem, steps, client != null ? client.Id : null);
                Close(); shell.Go("taches", null);
            };
            Controls.Add(list); Controls.Add(top); Controls.Add(go);
            CyberForm.Recolor(this);
        }
    }

    // ===================================================================== Autotest (bouton ou --selftest en ligne de commande / CI)
#endif

    static class SelfTest
    {
        public class R { public string Name, Status, Detail; public long Ms; }

        static R T(string name, Func<string> f)
        {
            var sw = Stopwatch.StartNew(); var r = new R { Name = name };
            try { var d = f(); r.Status = d != null && d.StartsWith("N/A") ? "N/A" : "OK"; r.Detail = d ?? ""; }
            catch (Exception ex) { r.Status = "KO"; r.Detail = ex.GetType().Name + " : " + ex.Message; }
            r.Ms = sw.ElapsedMilliseconds; return r;
        }

        public static List<R> Run(bool full, Action<R> each)
        {
            var l = new List<R>(); Action<R> add = r => { l.Add(r); if (each != null) each(r); };
            add(T("Données : enregistrement et relecture", () =>
            {
                var tmp = Path.Combine(Path.GetTempPath(), "amiops-selftest.json");
                var d = new Data(); d.Clients.Add(new Client { Id = "t", Name = "Test é", Created = DateTime.Now });
                File.WriteAllText(tmp, Store.Json().Serialize(d), Encoding.UTF8);
                var back = Store.Json().Deserialize<Data>(File.ReadAllText(tmp, Encoding.UTF8)); File.Delete(tmp);
                if (back.Clients[0].Name != "Test é") throw new Exception("relecture incorrecte"); return "JSON aller-retour OK";
            }));
#if !POSTE
            add(T("Coffre : chiffrement AES + HMAC", () =>
            {
                var c = Vault.CryptoSelfTest(); if (c != null) throw new Exception(c); return "chiffré, relu, altération détectée";
            }));
#endif
            add(T("Wi-Fi : lecture d'une sortie netsh FR", () =>
            {
                Ap cur; string err; var l2 = Wifi.Parse("SSID 1 : Box\r\n    Authentification : WPA2 - Personnel\r\n    BSSID 1 : aa:bb:cc:dd:ee:ff\r\n         Signal : 80%\r\n         Canal : 6\r\n", "    SSID : Box\r\n    Signal : 80%\r\n    Canal : 6\r\n", out cur, out err);
                if (l2.Count != 1 || l2[0].Channel != 6 || cur == null || cur.Signal != 80) throw new Exception("analyse incorrecte"); return "OK";
            }));
            add(T("Version de Windows", () => Report.WinVersion()));
            add(T("PanneScan (7 jours)", () => { var r = PanneScan.Run(7, x => { }); return r.Verdict + " · " + r.F.Count + " constats · " + r.T.Count + " événements"; }));
            add(T("Bilan du poste (sans WinSAT)", () => { var b = Optim.Bilan(false, x => { }); return "note " + b.Global.ToString("0.0") + " · " + b.E.Count + " éléments · " + string.Join(", ", b.E.Where(e => e.Note < 0).Select(e => e.Name + " n/a")); }));
            add(T("Nettoyage : mesure des tailles", () => string.Join(" · ", Optim.CleanScan().Select(c => c.Name.Split(' ')[0] + " " + (c.Size < 0 ? "?" : AlexB.Fmt.Size(c.Size))))));
            add(T("Démarrage : lecture + signatures", () => { var s = Startup.Scan(true, true); return s.Count + " éléments · " + s.Count(i => i.Sig == "Valid") + " signés · " + s.Count(i => i.Suspicious) + " suspects"; }));
            add(T("Audit de sécurité", () => { var a = Audit.Run(x => { }); return (a.Sum(c => c.Points) * 100 / Math.Max(1, a.Sum(c => c.Max))) + "/100 · " + a.Count + " contrôles"; }));
            add(T("Antivirus (état)", () => { var d = Optim.Defender(); return d == null ? "N/A Defender non lisible" : "Defender " + d["Mode"] + " · autres : " + string.Join(", ", Optim.OtherAv().Select(x => x.Split('|')[0])); }));
            add(T("Écrans bleus : vidages + débogueur", () => Bsod.Dumps().Count + " vidage(s) · débogueur " + (Bsod.Cdb() != null ? "trouvé" : "absent")));
            add(T("Licence Windows", () => { var sb = new StringBuilder(); Repair.License((s, c) => sb.Append(c == "bad" ? "[!] " : "").Append(s.Split('\n')[0]).Append(" | ")); return sb.ToString().TrimEnd(' ', '|'); }));
            add(T("Imprimantes (WMI)", () => { int n = 0; using (var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Printer")) n = s.Get().Count; return n + " imprimante(s)"; }));
#if !POSTE
            add(T("Secours : installations hors ligne", () => Rescue.Find().Count + " Windows hors ligne détecté(s)"));
#endif
            add(T("DNS : MX de microsoft.com", () => { int rc; var r = MailCheck.Dns2.Query("microsoft.com", MailCheck.Dns2.MX, out rc); if (r.Count == 0) throw new Exception("aucune réponse (code " + rc + ")"); return r[0].Data; }));
#if !POSTE
            add(T("Recette de livraison : mesures locales", () =>
            {
                var loc = Recette.ParseLocal(Sh.Ps(Recette.Script, 180000));
                if (loc.Count < 12) throw new Exception("script PowerShell incomplet : " + loc.Count + " mesure(s) sur 13");
                return loc.Count + " mesures · " + loc["Name"] + " · Windows " + (loc["Win"].StartsWith("1;") ? "activé" : "non activé") + " · agent GLPI " + loc["Glpi"].Split(';')[0];
            }));
            add(T("Parc : règles d'évaluation", () =>
            {
                var r = new ParcRow { LastSeen = DateTime.Now.AddDays(-9), FreePct = 5, OsName = "Windows 10 Pro", GlpiId = 1 }; Parc.Eval(r, DateTime.Now);
                if (r.Level != 2 || r.Issues.Count != 4) throw new Exception("évaluation inattendue : " + string.Join(" ; ", r.Issues));
                if (!Optim.SafetyScannerTrusted("Valid|CN=Microsoft Corporation, O=Microsoft Corporation|CN=Microsoft Root Certificate Authority 2011, O=Microsoft Corporation") || Optim.SafetyScannerTrusted("Valid|O=Microsoft Corporation|CN=Evil, O=Evil")) throw new Exception("règle Safety Scanner");
                return "4 constats critiques détectés, règle de signature Microsoft OK";
            }));
#endif
#if !POSTE
            add(T("Cyber : garde-fous et règles", () =>
            {
                if (Nmap.Profiles.Any(p => Nmap.CheckArgs(p.Args) != null) || Nmap.CheckArgs("--script exploit") == null) throw new Exception("garde-fou des scripts Nmap");
                var c = new Client { Name = "test", CyberScope = "10.0.0.0/24", CyberAuth = DateTime.Now };
                if (Scope.Check(c, Scope.Targets("10.0.0.5", n => new System.Net.IPAddress[0]), DateTime.Now) != null || Scope.Check(c, Scope.Targets("10.0.1.5", n => new System.Net.IPAddress[0]), DateTime.Now) == null) throw new Exception("contrôle du périmètre");
                var f = Risk.Evaluate(Nmap.Parse("<nmaprun><host><status state=\"up\"/><address addr=\"10.0.0.5\" addrtype=\"ipv4\"/><ports><port protocol=\"tcp\" portid=\"23\"><state state=\"open\"/><service name=\"telnet\"/></port></ports></host></nmaprun>"), DateTime.Now);
                if (!f.Any(x => x.Level == "bad")) throw new Exception("règle Telnet");
                return "périmètre, scripts autorisés et règles OK · Nmap " + (Nmap.Version() ?? "absent");
            }));
            add(T("Cyber : chasse aux menaces (journaux)", () => { var o = Sh.Ps(Poste.HuntScript, 300000); if (!Poste.Complete(o, "H")) throw new Exception("script incomplet : " + o.Trim().Split('\n').LastOrDefault()); return Poste.EvalHunt(o).Count + " constat(s) sur 7 jours"; }));
            add(T("Cyber : connexions", () => { var o = Sh.Ps(Poste.ConnScript, 120000); if (!Poste.Complete(o, "C")) throw new Exception("script incomplet"); int n; var f = Poste.EvalConn(o, out n); return n + " connexion(s) · " + f.Count + " constat(s)"; }));
#endif
            add(T("Réseau : passerelle", () => { var g = ScanParc.Scanner.Gateway(); return g == null ? "N/A aucune passerelle" : g.ToString(); }));
            if (full)
            {
                add(T("Windows Update : recherche", () => { string e; var u = Optim.SearchUpdates(out e); if (e != null) throw new Exception(e); return u.Count + " en attente (" + u.Count(x => x.Type == 2) + " pilotes)"; }));
                add(T("Disques (SMART)", () => string.Join(" · ", PanneScan.PowerShellJson("Get-PhysicalDisk|select FriendlyName,@{n='H';e={[string]$_.HealthStatus}}|ConvertTo-Json -Compress").Select(d => d["FriendlyName"] + " " + d["H"]))));
            }
            return l;
        }

        public static string Text(List<R> l)
        {
            var sb = new StringBuilder("AUTOTEST " + Ed.Name + " " + AppInfo.Version + " — " + Environment.MachineName + " — " + Report.WinVersion() + " — " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + "\r\n");
            sb.AppendLine(l.Count(r => r.Status == "OK") + " OK · " + l.Count(r => r.Status == "KO") + " KO · " + l.Count(r => r.Status == "N/A") + " non applicables");
            foreach (var r in l) sb.AppendLine("[" + r.Status.PadRight(3) + "] " + r.Name.PadRight(42) + (r.Ms + " ms").PadLeft(9) + "  " + r.Detail);
            return sb.ToString();
        }

        // amiops.exe --selftest [fichier] : sans fenêtre, code de sortie = nombre de KO (utilisé par la CI)
        public static int Headless(string outFile, bool full = true)
        {
            try { Store.Load(); } catch { }
            var l = Run(full, r => Console.WriteLine("[" + r.Status + "] " + r.Name + " — " + r.Detail));
            var txt = Text(l);
            File.WriteAllText(outFile ?? Path.Combine(Path.GetTempPath(), "amiops-autotest.txt"), txt, new UTF8Encoding(true));
            return l.Count(r => r.Status == "KO");
        }
    }
}
