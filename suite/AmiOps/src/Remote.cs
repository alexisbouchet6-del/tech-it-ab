// Tech IT AB - plusieurs PC à la fois (WinRM), connecteurs GLPI et Tactical RMM, mise à jour automatique
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using AlexB;

namespace AmiOps
{
    // ===================================================================== HTTP JSON (TLS vérifié, jamais contourné)
    static class Http
    {
        public static object Call(string method, string url, Dictionary<string, string> headers, object body, int timeoutMs = 30000)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method; req.Timeout = timeoutMs; req.UserAgent = "AmiOps/" + AppInfo.Version; req.Accept = "application/json";
            if (headers != null) foreach (var h in headers) req.Headers[h.Key] = h.Value;
            if (body != null)
            {
                var b = Encoding.UTF8.GetBytes(body as string ?? Store.Json().Serialize(body));
                req.ContentType = "application/json"; req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            try
            {
                using (var r = (HttpWebResponse)req.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                { var t = sr.ReadToEnd(); var ts = t.TrimStart(); return t.Length == 0 ? null : ts.StartsWith("{") || ts.StartsWith("[") ? Store.Json().DeserializeObject(t) : ts.StartsWith("\"") ? Store.Json().Deserialize<string>(t) : t; }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse; string detail = "";
                if (r != null) try { using (var sr = new StreamReader(r.GetResponseStream())) detail = sr.ReadToEnd(); } catch { }
                throw new Exception((r != null ? "HTTP " + (int)r.StatusCode + " " : "") + ex.Message + (detail.Length > 0 ? " — " + (detail.Length > 300 ? detail.Substring(0, 300) : detail) : ""));
            }
        }
        public static IEnumerable<Dictionary<string, object>> List(object o) { var a = o as object[]; if (a == null && o is System.Collections.ArrayList) a = ((System.Collections.ArrayList)o).ToArray(); return a == null ? Enumerable.Empty<Dictionary<string, object>>() : a.OfType<Dictionary<string, object>>(); }
        public static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }
    }

    // ===================================================================== GLPI (API REST v1 : apirest.php)
    static partial class Glpi
    {
        public static string Url { get { return Prefs.Ini.Get("GlpiUrl", "").TrimEnd('/'); } }
        public static bool Configured { get { return Url.Length > 0 && Secrets.Get("GlpiUser").Length > 0; } }

        static Dictionary<string, string> H(string session)
        {
            var h = new Dictionary<string, string>(); var app = Secrets.Get("GlpiApp"); if (app.Length > 0) h["App-Token"] = app;
            if (session == null) h["Authorization"] = "user_token " + Secrets.Get("GlpiUser"); else h["Session-Token"] = session;
            return h;
        }

        public static T Session<T>(Func<string, T> f)
        {
            if (!Url.StartsWith("https://")) throw new Exception("adresse GLPI en https obligatoire (jeton envoyé à chaque appel)");
            var init = Http.Call("GET", Url + "/initSession", H(null), null) as Dictionary<string, object>;
            var tok = Http.S(init, "session_token"); if (tok.Length == 0) throw new Exception("session GLPI refusée");
            try { return f(tok); } finally { try { Http.Call("GET", Url + "/killSession", H(tok), null); } catch { } }
        }

        public static string Test()
        {
            return Session(t => { object me; var r = Http.Call("GET", Url + "/getMyEntities?is_recursive=true", H(t), null) as Dictionary<string, object>; return (r != null && r.TryGetValue("myentities", out me) ? Http.List(me).Count() : 0) + " entité(s) accessibles"; });
        }

        // entités « CLI-xx-xx-xxxx - Nom » → fiches clients ; ordinateurs → parc
        public static string Sync(Action<string, string> log)
        {
            return Session(t =>
            {
                int nc = 0, nl = 0, na = 0;
                var ents = Http.List(Http.Call("GET", Url + "/Entity?range=0-1999", H(t), null)).ToList();
                foreach (var e in ents)
                {
                    int id; int.TryParse(Http.S(e, "id"), out id); var name = Http.S(e, "name"); if (id == 0 || name.Length == 0) continue;
                    var c = Store.D.Clients.FirstOrDefault(x => x.GlpiEntity == id) ?? Store.D.Clients.FirstOrDefault(x => x.GlpiEntity == 0 && (Store.Norm(x.Name) == Store.Norm(name) || Store.Norm(name).EndsWith(" - " + Store.Norm(x.Name))));
                    if (c == null) { if (!Regex.IsMatch(name, @"^CLI-\d{2}-\d{2}-\d{4}")) continue; c = new Client { Id = Store.NewId(), Name = name, Created = DateTime.Now }; Store.D.Clients.Add(c); nc++; }
                    else if (c.GlpiEntity == 0) nl++;
                    c.GlpiEntity = id;
                }
                var comps = Http.List(Http.Call("GET", Url + "/Computer?range=0-4999&expand_dropdowns=false", H(t), null)).ToList();
                foreach (var pc in comps)
                {
                    int ent; int.TryParse(Http.S(pc, "entities_id"), out ent); var c = Store.D.Clients.FirstOrDefault(x => x.GlpiEntity == ent && ent > 0); if (c == null) continue;
                    var name = Http.S(pc, "name"); var serial = Http.S(pc, "serial"); if (name.Length == 0) continue;
                    var a = c.Assets.FirstOrDefault(x => (serial.Length > 0 && x.Serial == serial) || x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (a == null) { c.Assets.Add(new Asset { Id = Store.NewId(), Kind = "PC", Name = name, Serial = serial, Notes = "importé de GLPI (id " + Http.S(pc, "id") + ")", Added = DateTime.Now }); na++; }
                    else if (a.Serial.Length == 0) a.Serial = serial;
                }
                Store.Save();
                var msg = ents.Count + " entité(s) lues : " + nc + " client(s) créé(s), " + nl + " lié(s) à une fiche existante ; " + comps.Count + " ordinateur(s) lus, " + na + " ajouté(s) aux parcs.";
                log(msg, "ok"); return msg;
            });
        }

        public static int Ticket(Intervention i)
        {
            var c = Store.ClientById(i.ClientId); if (c == null || c.GlpiEntity == 0) throw new Exception("client non lié à une entité GLPI (lance la synchronisation)");
            var content = Fmt.Html(Report.Text(i)).Replace("\r\n", "<br>").Replace("\n", "<br>");
            return Session(t =>
            {
                var input = new Dictionary<string, object> { { "name", (i.Ticket.Length > 0 ? "[" + i.Ticket + "] " : "") + i.Type + " — " + i.Demande.Split('\n')[0].Trim() }, { "content", content }, { "entities_id", c.GlpiEntity }, { "type", 1 }, { "status", i.Done ? 5 : 2 }, { "actiontime", i.TotalMinutes * 60 } };
                var r = Http.Call("POST", Url + "/Ticket/", H(t), new Dictionary<string, object> { { "input", input } }) as Dictionary<string, object>;
                int id; int.TryParse(Http.S(r, "id"), out id); if (id == 0) throw new Exception("réponse GLPI inattendue"); return id;
            });
        }
    }

    // ===================================================================== Tactical RMM (clé API, en-tête X-API-KEY)
    static partial class Trmm
    {
        public static string Url { get { return Prefs.Ini.Get("TrmmUrl", "").TrimEnd('/'); } }
        public static bool Configured { get { return Url.Length > 0 && Secrets.Get("TrmmKey").Length > 0; } }
        static Dictionary<string, string> H() { if (!Url.StartsWith("https://")) throw new Exception("adresse de l'API Tactical RMM en https obligatoire"); return new Dictionary<string, string> { { "X-API-KEY", Secrets.Get("TrmmKey") } }; }
        public static List<Dictionary<string, object>> Agents() { return Http.List(Http.Call("GET", Url + "/agents/", H(), null)).ToList(); }
        public static string Cmd(string agentId, string ps, int timeout)
        {
            if (!Regex.IsMatch(agentId, @"^[A-Za-z0-9\-]+$")) throw new Exception("identifiant d'agent invalide");
            var r = Http.Call("POST", Url + "/agents/" + agentId + "/cmd/", H(), new Dictionary<string, object> { { "shell", "powershell" }, { "cmd", ps }, { "timeout", timeout }, { "run_as_user", false } }, (timeout + 15) * 1000);
            return Convert.ToString(r);
        }
    }

    // ===================================================================== Plusieurs PC à la fois (PowerShell à distance / WinRM)
    static class Multi
    {
        public static readonly string[] Actions = { "Inventaire", "Santé (disque, redémarrage, MAJ, écrans bleus)", "Sécurité rapide", "Lancer une recherche Windows Update", "Commande PowerShell libre…" };

        internal static string Block(string action, string custom)
        {
            switch (action)
            {
                case "Inventaire": return "$cs=Get-CimInstance Win32_ComputerSystem;$b=Get-CimInstance Win32_BIOS;$os=Get-CimInstance Win32_OperatingSystem;$c=Get-CimInstance Win32_Processor|select -First 1;" +
                    "[pscustomobject]@{Modele=($cs.Manufacturer+' '+$cs.Model);Serie=$b.SerialNumber;OS=($os.Caption+' '+$os.BuildNumber);CPU=$c.Name;RAM=[math]::Round($cs.TotalPhysicalMemory/1GB);Utilisateur=$cs.UserName;IP=((Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue|?{$_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254*'}).IPAddress -join ' ')}";
                case "Sécurité rapide": return "$bl=try{[string](Get-CimInstance -Namespace root\\cimv2\\Security\\MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -Filter \"DriveLetter='$env:SystemDrive'\").ProtectionStatus}catch{'?'};" +
                    "$av=(Get-CimInstance -Namespace root\\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue|%{$_.displayName}) -join ', ';" +
                    "[pscustomobject]@{BitLocker=$(if($bl -eq '1'){'actif'}else{'NON'});PareFeu=$(if((Get-NetFirewallProfile|?{-not $_.Enabled}).Count -eq 0){'actif'}else{'DÉSACTIVÉ'});Antivirus=$av;SMBv1=[string](Get-SmbServerConfiguration).EnableSMB1Protocol;Admins=((Get-LocalGroupMember -SID S-1-5-32-544 -ErrorAction SilentlyContinue).Name -join ', ')}";
                case "Lancer une recherche Windows Update": return "Start-Process UsoClient.exe -ArgumentList StartScan -WindowStyle Hidden;[pscustomobject]@{Resultat='recherche lancée'}";
                case "Commande PowerShell libre…": return custom;
                default: return "$d=Get-PSDrive ($env:SystemDrive.TrimEnd(':'));$os=Get-CimInstance Win32_OperatingSystem;$hf=(Get-HotFix|?{$_.InstalledOn}|sort InstalledOn -Descending|select -First 1).InstalledOn;" +
                    "$bs=(Get-WinEvent -FilterHashtable @{LogName='System';Id=1001;StartTime=(Get-Date).AddDays(-30)} -ErrorAction SilentlyContinue|?{$_.ProviderName -like '*WER-SystemErrorReporting*'}).Count;" +
                    "$kp=(Get-WinEvent -FilterHashtable @{LogName='System';Id=41;StartTime=(Get-Date).AddDays(-30)} -ErrorAction SilentlyContinue).Count;" +
                    "$rb=(Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Component Based Servicing\\RebootPending') -or (Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Auto Update\\RebootRequired');" +
                    "$disk=(Get-PhysicalDisk|?{$_.HealthStatus -ne 'Healthy'}).FriendlyName -join ', ';" +
                    "[pscustomobject]@{Libre=([math]::Round($d.Free/1GB)+' Go ('+[math]::Round($d.Free/($d.Used+$d.Free)*100)+' %)');Demarre=([string][int]((Get-Date)-$os.LastBootUpTime).TotalDays+' j');RedemarrageEnAttente=$rb;DerniereMAJ=$(if($hf){$hf.ToString('dd/MM/yyyy')}else{'?'});EcransBleus30j=$bs;ArretsBrutaux30j=$kp;DisquesEnErreur=$(if($disk){$disk}else{'aucun'})}";
            }
        }

        // même bloc exécuté par l'agent Tactical RMM de chaque PC (8 en parallèle), résultat JSON sur une ligne
        public static List<Dictionary<string, object>> RunTrmm(List<string> pcs, string action, string custom, Action<string, string> log, out List<string> errors)
        {
            var errs = new List<string>(); var res = new List<Dictionary<string, object>>(); var lk = new object();
            var agents = Trmm.All(); var targets = new List<Trmm.Ag>();
            foreach (var p in pcs)
            {
                var a = agents.FirstOrDefault(x => x.Host.Equals(p, StringComparison.OrdinalIgnoreCase) || x.Host.Equals(p.Split('.')[0], StringComparison.OrdinalIgnoreCase));
                if (a == null) errs.Add(p + " : aucun agent Tactical RMM de ce nom"); else if (a.Status != "online") errs.Add(p + " : agent " + a.Status); else targets.Add(a);
            }
            var script = "$ErrorActionPreference='Continue';$r=& {" + Block(action, custom) + "};'JSON|'+(ConvertTo-Json -InputObject @($r|select * -ExcludeProperty RunspaceId,PSComputerName,PSShowComputerName) -Compress -Depth 3)";
            log("Tactical RMM : " + targets.Count + " agent(s) en ligne sur " + pcs.Count + ".", "muted");
            System.Threading.Tasks.Parallel.ForEach(targets, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, a =>
            {
                try
                {
                    var line = Trmm.Cmd(a.Id, script, 300).Replace("\r", "").Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("JSON|"));
                    if (line == null) throw new Exception("pas de résultat (commande refusée ou délai dépassé)");
                    var items = Http.List(Store.Json().DeserializeObject(line.Substring(5))).ToList();
                    foreach (var d in items) d["PSComputerName"] = a.Host;
                    lock (lk) res.AddRange(items);
                }
                catch (Exception ex) { lock (lk) errs.Add(a.Host + " : " + ex.Message); }
            });
            errors = errs; return res;
        }

        // le mot de passe passe par l'entrée standard de PowerShell : jamais sur la ligne de commande ni sur le disque
        public static List<Dictionary<string, object>> Run(List<string> pcs, string action, string custom, string user, string pass, Action<string, string> log, out List<string> errors)
        {
            errors = new List<string>();
            if (pcs.Any(p => !Regex.IsMatch(p, @"^[A-Za-z0-9.\-_]{1,253}$"))) throw new Exception("nom d'ordinateur invalide dans la liste");
            var list = string.Join(",", pcs.Select(Sh.Q));
            var cred = user.Length > 0 ? "$pw=[Console]::In.ReadLine()|ConvertTo-SecureString -AsPlainText -Force;$cr=New-Object System.Management.Automation.PSCredential(" + Sh.Q(user) + ",$pw);" : "";
            var script = "$ErrorActionPreference='Continue';" + cred +
                "$r=Invoke-Command -ComputerName @(" + list + ") " + (user.Length > 0 ? "-Credential $cr " : "") + "-ThrottleLimit 16 -ErrorAction SilentlyContinue -ErrorVariable ev -ScriptBlock {" + Block(action, custom) + "};" +
                "foreach($e in $ev){'ERR|'+$e.TargetObject+'|'+($e.Exception.Message -replace '\\r?\\n',' ')};" +
                "'JSON|'+(ConvertTo-Json -InputObject @($r|select * -ExcludeProperty RunspaceId,PSShowComputerName) -Compress -Depth 3)";
            var psi = new ProcessStartInfo(Sh.PsExe, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script)))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            string outp;
            using (var p = Process.Start(psi))
            {
                if (user.Length > 0) p.StandardInput.WriteLine(pass);
                p.StandardInput.Close();
                var err = p.StandardError.ReadToEndAsync(); outp = p.StandardOutput.ReadToEnd();   // stderr lu en parallèle : pas de blocage si le tampon se remplit
                if (!p.WaitForExit(30 * 60 * 1000)) { try { p.Kill(); } catch { } throw new Exception("délai dépassé"); }
                if (outp.IndexOf("JSON|", StringComparison.Ordinal) < 0 && err.Wait(5000) && err.Result.Trim().Length > 0) errors.Add("PowerShell : " + err.Result.Trim());
            }
            var res = new List<Dictionary<string, object>>();
            foreach (var l in outp.Split('\n').Select(x => x.Trim()))
            {
                if (l.StartsWith("ERR|")) { var p = l.Split(new[] { '|' }, 3); errors.Add(p[1] + " : " + (p.Length > 2 ? p[2] : "")); }
                else if (l.StartsWith("JSON|")) res.AddRange(Http.List(Store.Json().DeserializeObject(l.Substring(5))));
            }
            return res;
        }
    }

    class MultiPage : Page
    {
        readonly TextBox pcs; readonly ComboBox action, canal; readonly Grid grid; readonly Label stat;
        List<Dictionary<string, object>> last = new List<Dictionary<string, object>>();
        public override string Title { get { return "Multi-PC"; } }

        public MultiPage()
        {
            Lbl("MULTI-PC", 0, k(2), "accent", 13f, true);
            Lbl("La même action sur plusieurs PC : WinRM à l'atelier et dans un domaine AD ; Tactical RMM pour les PC des clients (aucun port à ouvrir).", k(120), k(10), "muted");
            Lbl("Postes : un par ligne (nom ou IP)", 0, k(44), "muted");
            pcs = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(0, k(66)), Size = new Size(k(240), k(250)), Font = new Font("Consolas", 9.5f) };
            Controls.Add(pcs);
            int x = k(252), y = k(40);
            action = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x, y + k(4)), Width = k(300) }; action.Items.AddRange(Multi.Actions); action.SelectedIndex = 0; Controls.Add(action);
            var bGo = Btn("▶  LANCER", t => t.Ok, x + k(310), y, (s, e) => Go(), true);
            // canal : WinRM pour l'atelier et les domaines AD ; Tactical RMM pour les clients (ni domaine ni réseau commun)
            canal = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(bGo.Right + k(10), y + k(4)), Width = k(250) };
            canal.Items.AddRange(new object[] { "WinRM (atelier, domaine AD)", "Tactical RMM (PC des clients)" }); canal.SelectedIndex = Trmm.Configured ? 1 : 0; Controls.Add(canal);
            // 2e ligne : sources de la liste et exports (une seule ligne débordait en 1366 px)
            int bx = x, y2 = y + k(38);
            bx = Btn("Depuis un client…", t => t.Accent, bx, y2, (s, e) => FromClient()).Right + k(6);
            bx = Btn("Tous les PC de l'AD", t => t.Accent, bx, y2, (s, e) => FromAd()).Right + k(6);
            bx = Btn("Hôtes de confiance…", t => t.Warn, bx, y2, (s, e) => Trusted()).Right + k(6);
            bx = Btn("Export CSV", t => t.Muted, bx, y2, (s, e) => Export()).Right + k(6);
            Btn("Inventaire → fiche client…", t => t.Accent2, bx, y2, (s, e) => ToClient());
            stat = Lbl("", x, k(122), "muted");
            grid = new Grid(); grid.Location = new Point(x, k(144)); grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            grid.Col("PC", 150).Col("Résultat", 700);
            Controls.Add(grid);
            // tailles recalculées au redimensionnement (la page est créée à 200 × 100 avant d'être ancrée : Anchor Bottom débordait)
            Resize += (s, e) => { grid.Size = new Size(ClientSize.Width - grid.Left, ClientSize.Height - grid.Top); pcs.Height = Math.Max(k(120), ClientSize.Height - pcs.Top); };
        }

        List<string> Pcs() { return pcs.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }

        void Go()
        {
            var list = Pcs(); if (list.Count == 0) { CyberForm.Info(this, "Saisis au moins un PC."); return; }
            var act = (string)action.SelectedItem; string custom = "";
            if (act.StartsWith("Commande"))
            {
                custom = Dlg.Ask(this, "Commande libre", "Commande PowerShell exécutée en ADMINISTRATEUR sur les " + list.Count + " PC (sortie = objets)", Prefs.Ini.Get("MultiCmd", "Get-Service Spooler | select Status"), false);
                if (custom == null) return; Prefs.Ini.Set("MultiCmd", custom); Prefs.Save();
                if (!CyberForm.Ask(this, "Exécuter sur " + list.Count + " PC :\n\n" + custom + "\n\nAction potentiellement irréversible. Continuer ?")) return;
            }
            if (canal.SelectedIndex == 1) { GoTrmm(list, act, custom); return; }
            var fu = new F("Compte administrateur (vide = ton compte actuel, conseillé dans un domaine)", Prefs.Ini.Get("MultiUser", ""));
            var fp = new F("Mot de passe (jamais enregistré)", "") { Password = true };
            if (!Dlg.Edit(this, "Identifiants", fu, fp)) return;
            Prefs.Ini.Set("MultiUser", fu.Value); Prefs.Save();
            var user = fu.Value; var pass = fp.Value;
            stat.Text = "En cours sur " + list.Count + " PC… (suivi dans Tâches)";
            Jobs.Run(act + " sur " + list.Count + " PC", null, j =>
            {
                List<string> errs; var res = Multi.Run(list, act, custom, user, pass, j.Log, out errs);
                foreach (var e in errs) j.Log("  ✗ " + e, "bad");
                j.Log(res.Count + " réponse(s), " + errs.Count + " PC injoignable(s)", errs.Count == 0 ? "ok" : "warn");
                BeginInvoke(new Action(() => Show(res, errs)));
            });
        }

        void Show(List<Dictionary<string, object>> res, List<string> errs)
        {
            last = res; var cols = res.SelectMany(d => d.Keys).Where(kk => kk != "PSComputerName" && kk != "PSSourceJobInstanceId").Distinct().ToList();
            grid.BeginUpdate(); grid.Items.Clear(); grid.Columns.Clear(); grid.Col("PC", 150); foreach (var c in cols) grid.Col(c, 140);
            foreach (var d in res.OrderBy(d => Http.S(d, "PSComputerName")))
            {
                var vals = new[] { Http.S(d, "PSComputerName") }.Concat(cols.Select(c => Http.S(d, c))).ToArray();
                var bad = vals.Any(v => v == "NON" || v == "DÉSACTIVÉ" || v == "True" && cols.Contains("RedemarrageEnAttente"));
                grid.Items.Add(new ListViewItem(vals) { Tag = d, ForeColor = bad ? Theme.Current.Warn : Theme.Current.Text });
            }
            foreach (var e in errs) grid.Items.Add(new ListViewItem(new[] { e.Split(':')[0].Trim(), "INJOIGNABLE : " + e.Substring(e.IndexOf(':') + 1).Trim() }) { ForeColor = Theme.Current.Bad });
            grid.EndUpdate(); grid.Fit();
            stat.Text = res.Count + " PC OK · " + errs.Count + " en échec (WinRM désactivé, pare-feu, identifiants, hors ligne)";
        }

        void GoTrmm(List<string> list, string act, string custom)
        {
            if (!Trmm.Configured) { CyberForm.Info(this, "Configure Tactical RMM dans Connecteurs."); return; }
            stat.Text = "En cours via Tactical RMM sur " + list.Count + " PC… (suivi dans Tâches)";
            Jobs.Run(act + " sur " + list.Count + " PC (Tactical RMM)", null, j =>
            {
                List<string> errs; var res = Multi.RunTrmm(list, act, custom, j.Log, out errs);
                foreach (var e in errs) j.Log("  ✗ " + e, "bad");
                j.Log(res.Count + " réponse(s), " + errs.Count + " PC en échec", errs.Count == 0 ? "ok" : "warn");
                BeginInvoke(new Action(() => Show(res, errs)));
            });
        }

        void FromClient()
        {
            var names = Store.D.Clients.OrderBy(c => c.Name).Select(c => c.Name).ToArray(); if (names.Length == 0) return;
            var f = new F("Client", names[0]) { Options = names, Editable = false };
            if (!Dlg.Edit(this, "PC d'un client", f)) return;
            var cl = Store.D.Clients.First(x => x.Name == f.Value);
            if (canal.SelectedIndex == 1 && Trmm.Configured)
            {
                try { pcs.Text = string.Join("\r\n", Trmm.All().Where(a => Trmm.OfClient(a, cl)).Select(a => a.Host).OrderBy(h => h)); return; }
                catch (Exception ex) { CyberForm.Warn(this, "Tactical RMM : " + ex.Message + "\nListe prise dans la fiche client."); }
            }
            pcs.Text = string.Join("\r\n", cl.Assets.Where(a => a.Kind == "PC" || a.Kind == "Portable" || a.Kind == "Serveur").Select(a => a.Name.Length > 0 ? a.Name : a.Ip).Where(n => n.Length > 0));
        }

        void FromAd()
        {
            var o = Sh.Ps("([adsisearcher]'(&(objectCategory=computer)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))').FindAll()|%{$_.Properties['dnshostname'][0]}|sort", 120000);
            var l = o.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.Contains(" ")).ToList();
            if (l.Count == 0) { CyberForm.Warn(this, "Aucun ordinateur trouvé (ce PC est-il dans un domaine ?)."); return; }
            pcs.Text = string.Join("\r\n", l);
        }

        void Trusted()
        {
            var list = Pcs(); if (list.Count == 0) return;
            if (!CyberForm.Ask(this, "Ajouter ces PC aux hôtes de confiance WinRM de CE poste ?\n" + string.Join(", ", list.Take(10)) + (list.Count > 10 ? "…" : "") + "\n\nHors domaine, l'authentification est NTLM : à n'utiliser que sur le réseau du client, jamais vers Internet.")) return;
            Jobs.Run("Hôtes de confiance WinRM", null, j =>
            {
                string o; Sh.Ps("Start-Service WinRM -ErrorAction SilentlyContinue;Set-Item WSMan:\\localhost\\Client\\TrustedHosts -Value " + Sh.Q(string.Join(",", list)) + " -Concatenate -Force", 60000, null, out o);
                j.Log("hôtes de confiance : " + Sh.Ps("(Get-Item WSMan:\\localhost\\Client\\TrustedHosts).Value", 30000).Trim(), "ok");
                j.Log("Sur chaque PC cible (admin) : Enable-PSRemoting -Force", "text");
            });
        }

        void Export()
        {
            if (grid.Items.Count == 0) return;
            using (var d = new SaveFileDialog { Filter = "CSV (Excel)|*.csv", FileName = "MultiPC_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder(string.Join(";", grid.Columns.Cast<ColumnHeader>().Select(c => InterventionsPage.Csv(c.Text))) + "\r\n");
                foreach (ListViewItem it in grid.Items) sb.Append(string.Join(";", it.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(si => InterventionsPage.Csv(si.Text)))).Append("\r\n");
                File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(true));
            }
        }

        void ToClient()
        {
            if (last.Count == 0 || !last[0].ContainsKey("Serie")) { CyberForm.Info(this, "Lance d'abord l'action « Inventaire »."); return; }
            var names = Store.D.Clients.OrderBy(c => c.Name).Select(c => c.Name).ToArray(); if (names.Length == 0) return;
            var f = new F("Client", names[0]) { Options = names, Editable = false };
            if (!Dlg.Edit(this, "Inventaire → fiche client", f)) return;
            var cl = Store.D.Clients.First(x => x.Name == f.Value); int add = 0, upd = 0;
            foreach (var d in last)
            {
                var name = Http.S(d, "PSComputerName"); var serial = Http.S(d, "Serie");
                var a = cl.Assets.FirstOrDefault(x => (serial.Length > 0 && x.Serial == serial) || x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (a == null) { a = new Asset { Id = Store.NewId(), Kind = "PC", Added = DateTime.Now }; cl.Assets.Add(a); add++; } else upd++;
                a.Name = name; a.Serial = serial; a.Model = Http.S(d, "Modele"); a.Os = Http.S(d, "OS"); var ip = Http.S(d, "IP").Split(' ')[0]; if (ip.Length > 0) a.Ip = ip;
            }
            Store.Save(); CyberForm.Info(this, add + " PC ajouté(s), " + upd + " mis à jour dans « " + cl.Name + " ».");
        }
    }

    // ===================================================================== Mise à jour automatique (versions publiées sur GitHub)
    static class Updater
    {
        public static string Repo { get { return Prefs.Ini.Get("UpdateRepo", "alexisbouchet6-del/tech-it-ab"); } }
        // certificat de signature de code Amiprotek (kit glpi-amiprotek, signing/amiprotek-codesigning.cer)
        public const string AmiprotekThumb = "7C02F586B652E6C722E3523BD86170E5564B9D0A";

        static Dictionary<string, string> H(bool binary)
        {
            var h = new Dictionary<string, string>(); var tok = Secrets.Get("GithubToken");
            if (tok.Length > 0) h["Authorization"] = "Bearer " + tok;
            return h;
        }

        static Version V(string s) { Version v; return Version.TryParse(Regex.Replace(s ?? "", "[^0-9.]", ""), out v) ? v : new Version(0, 0); }

        public static string Check(out string url, out string sumsUrl, out string tag)
        {
            url = sumsUrl = null; tag = null;
            if (!Regex.IsMatch(Repo, @"^[\w.\-]+/[\w.\-]+$")) throw new Exception("dépôt invalide (format propriétaire/nom)");
            var r = Http.Call("GET", "https://api.github.com/repos/" + Repo + "/releases/latest", H(false), null) as Dictionary<string, object>;
            tag = Http.S(r, "tag_name");
            foreach (var a in Http.List(r.ContainsKey("assets") ? r["assets"] : null))
            {
                var n = Http.S(a, "name");
                if (n == Ed.Asset) url = Http.S(a, "url");
                if (n == "SHA256SUMS.txt") sumsUrl = Http.S(a, "url");
            }
            return V(tag) > V(AppInfo.Version) ? tag : null;
        }

        static byte[] Download(string apiUrl)
        {
            var req = (HttpWebRequest)WebRequest.Create(apiUrl); req.UserAgent = "AmiOps/" + AppInfo.Version; req.Accept = "application/octet-stream"; req.Timeout = 120000;
            foreach (var h in H(true)) req.Headers[h.Key] = h.Value;
            using (var r = req.GetResponse()) using (var s = r.GetResponseStream()) using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }

        // téléchargement → empreinte SHA-256 vérifiée → (signature vérifiée si une empreinte de certificat est épinglée) → remplacement au redémarrage
        public static void Apply(string url, string sumsUrl, string tag, Action<string, string> log)
        {
            if (url == null || sumsUrl == null) throw new Exception("la version " + tag + " ne contient pas " + Ed.Asset + " + SHA256SUMS.txt");
            var exe = Download(url); var sums = Encoding.UTF8.GetString(Download(sumsUrl));
            string hash; using (var sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(exe)).Replace("-", "").ToLowerInvariant();
            var m = Regex.Match(sums, @"([0-9a-fA-F]{64})\s+\*?" + Regex.Escape(Ed.Asset));
            if (!m.Success || !m.Groups[1].Value.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new Exception("EMPREINTE SHA-256 INCORRECTE : mise à jour refusée");
            log("empreinte SHA-256 vérifiée : " + hash.Substring(0, 16) + "…", "ok");
            // dossier neuf réservé à SYSTEM + Administrateurs : un processus non élevé ne peut pas remplacer
            // l'exe entre la vérification et la copie (%TEMP% est modifiable par tout processus de la session)
            var dir = Hardening.SecureTempDir("AmiOps-maj");
            var tmp = Path.Combine(dir, "AmiOps-" + Regex.Replace(tag, @"[^\w.\-]", "") + ".exe"); File.WriteAllBytes(tmp, exe);
            var pin = Prefs.Ini.Get("UpdateThumb", "").Replace(" ", "");
            if (pin.Length > 0)
            {
                // Status doit valoir Valid : un exe signé puis modifié garde son certificat mais passe en HashMismatch
                var sig = Sh.Ps("$s=Get-AuthenticodeSignature -LiteralPath " + Sh.Q(tmp) + ";[string]$s.Status+'|'+[string]$s.SignerCertificate.Thumbprint", 60000).Trim();
                if (!sig.Equals("Valid|" + pin, StringComparison.OrdinalIgnoreCase)) { try { Directory.Delete(dir, true); } catch { } throw new Exception("SIGNATURE NON CONFORME (" + sig + ") : mise à jour refusée"); }
                log("signature vérifiée (certificat épinglé)", "ok");
            }
            var target = Application.ExecutablePath; var pid = Process.GetCurrentProcess().Id;
            var cmd = Path.Combine(dir, "amiops-update.cmd");
            // cmd.exe lit les .cmd dans la page de code OEM : chemins accentués (C:\Users\Amélie) corrects
            var oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            File.WriteAllText(cmd, "@echo off\r\n:w\r\ntasklist /fi \"PID eq " + pid + "\" | find \"" + pid + "\" >nul && (timeout /t 1 >nul & goto w)\r\ncopy /y \"" + target + "\" \"" + target + ".old\" >nul\r\ncopy /y \"" + tmp + "\" \"" + target + "\" >nul\r\nstart \"\" \"" + target + "\"\r\n(goto) 2>nul & rd /s /q \"" + dir + "\"\r\n", oem);
            log(Ed.Name + " va redémarrer en " + tag + " (ancienne version gardée en .old).", "ok");
            Process.Start(new ProcessStartInfo(Proc.Sys("cmd.exe"), "/c \"" + cmd + "\"") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
            TrayHost.Quitting = true; Application.Exit();
        }
    }

    // ===================================================================== Page Connecteurs
    class ConnectPage : HubPage
    {
        public override string Title { get { return "Connecteurs"; } }

        public ConnectPage() : base("CONNECTEURS", "GLPI, Tactical RMM et mise à jour de " + Ed.Name + ". Jetons chiffrés DPAPI (ton compte Windows), https obligatoire.", 120)
        {
            T(Theme.Icon("", "⚙"), "Configurer GLPI", "URL + jetons", t => t.Accent, ConfGlpi, false);
            T(Theme.Icon("", "↻"), "Synchroniser GLPI", "entités → clients, PC → parc", t => t.Ok, () => { if (!Glpi.Configured) throw new Exception("GLPI non configuré"); Glpi.Sync(Out); });
            T(Theme.Icon("", "⚙"), "Configurer TRMM", "Tactical RMM : URL API + clé", t => t.Accent, ConfTrmm, false);
            T(Theme.Icon("", "▤"), "Agents Tactical RMM", "état, commande à distance", t => t.Ok, () => { if (!Trmm.Configured) throw new Exception("Tactical RMM non configuré"); var l = Trmm.Agents(); Ui(() => AgentsForm(l)); });
            T(Theme.Icon("", "⇩"), "Mise à jour de " + Ed.Name, "versions GitHub", t => t.Accent2, CheckUpdate);
            T(Theme.Icon("", "⚙"), "Réglages MAJ", "dépôt, jeton, certificat", t => t.Muted, ConfUpdate, false);
            T(Theme.Icon("", "♥"), "Santé des serveurs", "HTTPS, certificats", t => t.Warn, () =>
            {
                foreach (var r in Infra.All()) Out((r.Level == "ok" ? "✓ " : "✗ ") + r.Url + " : " + r.Detail, r.Level);
            });
            T(Theme.Icon("", "⚙"), "Adresses surveillées", "glpi, rmm, api, mesh…", t => t.Muted, () =>
            {
                var f = new F("Adresses https, une par ligne", string.Join("\r\n", Infra.Urls)) { Multi = true, Lines = 6 };
                if (!Dlg.Edit(this, "Santé des serveurs", f)) return;
                var l = f.Value.Split('\n').Select(x => x.Trim()).Where(x => x.StartsWith("https://")).ToList();
                Prefs.Ini.Set("InfraUrls", string.Join("|", l)); Prefs.Save(); Out(l.Count + " adresse(s) surveillée(s), contrôle quotidien avec notification.", "ok");
            }, false);
        }

        void ConfGlpi()
        {
            var fu = new F("URL de l'API (ex. https://glpi.ami-tech.fr/apirest.php)", Prefs.Ini.Get("GlpiUrl", "")) { Required = true };
            var fa = new F("App-Token (Configuration > Générale > API ; vide si non exigé)", Secrets.Get("GlpiApp")) { Password = true };
            var ft = new F("Jeton d'API utilisateur (Préférences > Jeton API) — compte dédié conseillé, droits limités", Secrets.Get("GlpiUser")) { Password = true, Required = true };
            var fs = new F("Adresse d'inventaire des agents (enrôlement des PC)", Enrol.AgentServer) { Required = true };
            if (!Dlg.Edit(this, "GLPI", fu, fa, ft, fs)) return;
            if (!fu.Value.StartsWith("https://") || !fs.Value.StartsWith("https://")) { CyberForm.Warn(this, "https obligatoire."); return; }
            Prefs.Ini.Set("GlpiUrl", fu.Value); Prefs.Ini.Set("GlpiAgentServer", fs.Value); Prefs.Save(); Secrets.Set("GlpiApp", fa.Value); Secrets.Set("GlpiUser", ft.Value);
            Start("Test GLPI", () => Out("GLPI : " + Glpi.Test(), "ok"));
        }

        void ConfTrmm()
        {
            var fu = new F("URL de l'API (ex. https://api.ami-tech.fr)", Prefs.Ini.Get("TrmmUrl", "")) { Required = true };
            var fk = new F("Clé API (Paramètres > Clés API) — rôle limité conseillé (contourne la 2FA)", Secrets.Get("TrmmKey")) { Password = true, Required = true };
            if (!Dlg.Edit(this, "Tactical RMM", fu, fk)) return;
            if (!fu.Value.StartsWith("https://")) { CyberForm.Warn(this, "https obligatoire."); return; }
            Prefs.Ini.Set("TrmmUrl", fu.Value); Prefs.Save(); Secrets.Set("TrmmKey", fk.Value);
            Start("Test Tactical RMM", () => Out("Tactical RMM : " + Trmm.Agents().Count + " agent(s)", "ok"));
        }

        void AgentsForm(List<Dictionary<string, object>> agents)
        {
            var g = new Grid { Dock = DockStyle.Fill, CheckBoxes = true }; g.Col("Client", 170).Col("Site", 120).Col("Poste", 160).Col("État", 80).Col("OS", 220).Col("Vu le", 140).Col("Utilisateur", 140);
            foreach (var a in agents.OrderBy(a => Http.S(a, "client_name")).ThenBy(a => Http.S(a, "hostname")))
            {
                var st = Http.S(a, "status");
                g.Items.Add(new ListViewItem(new[] { Http.S(a, "client_name"), Http.S(a, "site_name"), Http.S(a, "hostname"), st, Http.S(a, "operating_system"), Http.S(a, "last_seen"), Http.S(a, "logged_username") }) { Tag = a, ForeColor = st == "online" ? Theme.Current.Ok : st == "overdue" ? Theme.Current.Bad : Theme.Current.Muted });
            }
            using (var f = new Form { Text = "Agents Tactical RMM (" + agents.Count + ") · by alex.b", Size = new Size(k(1100), k(620)), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Current.Bg, ShowInTaskbar = false })
            {
                var b = new Chip("Commande PowerShell sur les agents cochés…") { AccentOf = t => t.Warn, Dock = DockStyle.Bottom };
                b.Click += (s, e) =>
                {
                    var sel = g.CheckedItems.Cast<ListViewItem>().Select(i => (Dictionary<string, object>)i.Tag).ToList(); if (sel.Count == 0) return;
                    var cmd = Dlg.Ask(f, "Tactical RMM", "Commande PowerShell (exécutée en SYSTEM sur " + sel.Count + " poste(s))", "Get-PSDrive C | select Used,Free", false);
                    if (cmd == null || !CyberForm.Ask(f, "Exécuter sur " + sel.Count + " poste(s) :\n\n" + cmd + "\n\nContinuer ?")) return;
                    f.Close();
                    foreach (var a in sel) { var aa = a; Start("TRMM " + Http.S(aa, "hostname"), () => Out(Http.S(aa, "hostname") + " :\r\n" + Trmm.Cmd(Http.S(aa, "agent_id"), cmd, 60), "text")); }
                };
                f.Controls.Add(g); f.Controls.Add(b); CyberForm.Recolor(f); f.ShowDialog(this);
            }
        }

        void CheckUpdate()
        {
            string url, sums, tag; var newer = Updater.Check(out url, out sums, out tag);
            if (newer == null) { Out(Ed.Name + " est à jour (" + AppInfo.Version + ", dernière publiée : " + (tag ?? "?") + ").", "ok"); return; }
            Out("Nouvelle version disponible : " + newer, "warn");
            if (AskUi("Installer " + Ed.Name + " " + newer + " maintenant ?\nL'appli va redémarrer (tâches en cours interrompues).")) Updater.Apply(url, sums, tag, Out);
        }

        void ConfUpdate()
        {
            var fr = new F("Dépôt GitHub (propriétaire/nom)", Updater.Repo) { Required = true };
            var ft = new F("Jeton GitHub en lecture seule (dépôt privé : fine-grained, Contents: Read)", Secrets.Get("GithubToken")) { Password = true };
            var fp = new F("Empreinte du certificat de signature à exiger (vide = SHA-256 seulement ; certificat Amiprotek : " + Updater.AmiprotekThumb + ")", Prefs.Ini.Get("UpdateThumb", ""));
            if (!Dlg.Edit(this, "Mise à jour", fr, ft, fp)) return;
            Prefs.Ini.Set("UpdateRepo", fr.Value); Prefs.Ini.Set("UpdateThumb", fp.Value.Replace(" ", "")); Prefs.Save(); Secrets.Set("GithubToken", ft.Value);
            Out("Configuration de mise à jour enregistrée.", "ok");
        }
    }
}
