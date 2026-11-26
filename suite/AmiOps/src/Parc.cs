// AMI·OPS - parc des clients (projet GLPI Amiprotek) : recette de livraison, enrôlement, tableau de bord,
// rapport mensuel de conformité, NAS → GLPI, tickets dans les deux sens, santé des serveurs.
// Développé by alex.b
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlexB;

namespace AmiOps
{
    // ===================================================================== GLPI : appels du projet (API v1, même session)
    static partial class Glpi
    {
        public static string Base { get { return Regex.Replace(Url, @"/apirest\.php$", "", RegexOptions.IgnoreCase); } }
        static string E(string s) { return Uri.EscapeDataString(s ?? ""); }
        public static object Get(string t, string path) { return Http.Call("GET", Url + path, H(t), null, 60000); }
        public static Dictionary<string, object> Post(string t, string path, Dictionary<string, object> input)
        {
            return Http.Call("POST", Url + path, H(t), new Dictionary<string, object> { { "input", input } }) as Dictionary<string, object>;
        }
        public static int IdOf(Dictionary<string, object> d) { int id; int.TryParse(Http.S(d, "id"), out id); return id; }

        // « CLI-09-16-0699 - Nom du client » → « CLI-09-16-0699 » ; nom des PC : CLI09160699-01
        public static string CodeOf(string clientName) { var m = Regex.Match(clientName ?? "", @"CLI-\d{2}-\d{2}-\d{4}"); return m.Success ? m.Value : ""; }
        public static string PcPrefix(string code) { return code.Replace("-", "") + "-"; }
        public static string PcName(string code, int n) { return PcPrefix(code) + n.ToString("00"); }

        public static List<Dictionary<string, object>> Computers(string t) { return Http.List(Get(t, "/Computer/?range=0-4999&is_deleted=0")).ToList(); }
        public static List<Dictionary<string, object>> Agents(string t) { try { return Http.List(Get(t, "/Agent/?range=0-4999")).ToList(); } catch { return new List<Dictionary<string, object>>(); } }

        public static Dictionary<string, object> FindComputer(string t, string name)
        {
            return Http.List(Get(t, "/Computer/?searchText[name]=" + E(name) + "&range=0-50")).FirstOrDefault(c => Http.S(c, "name").Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        // premier numéro libre pour ce client (CLI09160699-01, -02…)
        public static int NextNumber(IEnumerable<string> names, string code)
        {
            var rx = new Regex("^" + Regex.Escape(PcPrefix(code)) + @"(\d{2})$", RegexOptions.IgnoreCase);
            var used = new HashSet<int>(names.Select(n => rx.Match(n ?? "")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)));
            int i = 1; while (used.Contains(i)) i++; return i;
        }

        // sous-éléments d'un ordinateur : la liste vide si le type n'existe pas dans cette version de GLPI
        public static List<Dictionary<string, object>> Sub(string t, int computer, params string[] types)
        {
            foreach (var ty in types)
                try { return Http.List(Get(t, "/Computer/" + computer + "/" + ty + "/?expand_dropdowns=true&range=0-50")).ToList(); } catch { }
            return new List<Dictionary<string, object>>();
        }

        public static int EnsureEntity(string t, string code, string name)
        {
            var ents = Http.List(Get(t, "/Entity/?range=0-1999")).ToList();
            var e = ents.FirstOrDefault(x => Http.S(x, "name").StartsWith(code, StringComparison.OrdinalIgnoreCase));
            if (e != null) return IdOf(e);
            var label = name.StartsWith(code) ? name : code + " - " + name;
            return IdOf(Post(t, "/Entity/", new Dictionary<string, object> { { "name", label }, { "entities_id", 0 } }));
        }

        // type 1 = incident, 2 = demande ; statut 1 = nouveau, 2 = en cours
        public static int NewTicket(string t, int entity, string title, string html, int type, int status)
        {
            var id = IdOf(Post(t, "/Ticket/", new Dictionary<string, object> { { "name", title }, { "content", html }, { "entities_id", entity }, { "type", type }, { "status", status } }));
            if (id == 0) throw new Exception("réponse GLPI inattendue à la création du ticket");
            return id;
        }
        public static void Followup(string t, int ticket, string html) { Post(t, "/ITILFollowup/", new Dictionary<string, object> { { "itemtype", "Ticket" }, { "items_id", ticket }, { "content", html } }); }
        public static void Task(string t, int ticket, string html, int minutes) { Post(t, "/TicketTask/", new Dictionary<string, object> { { "tickets_id", ticket }, { "content", html }, { "actiontime", Math.Max(0, minutes) * 60 }, { "state", 2 } }); }
        public static void Solve(string t, int ticket, string html) { Post(t, "/ITILSolution/", new Dictionary<string, object> { { "itemtype", "Ticket" }, { "items_id", ticket }, { "content", html } }); }
        public static void LinkComputer(string t, int ticket, int computer) { Post(t, "/Item_Ticket/", new Dictionary<string, object> { { "tickets_id", ticket }, { "itemtype", "Computer" }, { "items_id", computer } }); }

        // tickets non clos d'une entité (moteur de recherche : 2 = id, 1 = titre, 12 = statut, 15 = date d'ouverture, 80 = entité)
        public static List<Dictionary<string, object>> OpenTickets(string t, int entity)
        {
            var q = "/search/Ticket/?criteria[0][field]=80&criteria[0][searchtype]=equals&criteria[0][value]=" + entity +
                    "&criteria[1][link]=AND&criteria[1][field]=12&criteria[1][searchtype]=equals&criteria[1][value]=notold" +
                    "&forcedisplay[0]=2&forcedisplay[1]=1&forcedisplay[2]=12&forcedisplay[3]=15&sort=15&order=DESC&range=0-200";
            var r = Get(t, q) as Dictionary<string, object>; object data;
            return r != null && r.TryGetValue("data", out data) ? Http.List(data).ToList() : new List<Dictionary<string, object>>();
        }
        public static readonly string[] Statuts = { "", "Nouveau", "En cours (attribué)", "En cours (planifié)", "En attente", "Résolu", "Clos" };

        // envoi d'un document (multipart, champ uploadManifest) puis liaison au ticket
        public static int Upload(string t, string file, int entity, int ticket)
        {
            var name = Path.GetFileName(file); var boundary = "----amiops" + Guid.NewGuid().ToString("N");
            var manifest = Store.Json().Serialize(new Dictionary<string, object> { { "input", new Dictionary<string, object> { { "name", name }, { "_filename", new[] { name } }, { "entities_id", entity } } } });
            var req = (HttpWebRequest)WebRequest.Create(Url + "/Document/"); req.Method = "POST"; req.Timeout = 120000; req.UserAgent = "AmiOps/" + AppInfo.Version; req.Accept = "application/json";
            foreach (var h in H(t)) req.Headers[h.Key] = h.Value;
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            using (var ms = new MemoryStream())
            {
                Action<string> w = s => { var b = Encoding.UTF8.GetBytes(s); ms.Write(b, 0, b.Length); };
                w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"uploadManifest\"\r\nContent-Type: application/json\r\n\r\n" + manifest + "\r\n");
                w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"filename[0]\"; filename=\"" + name.Replace("\"", "") + "\"\r\nContent-Type: " + (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "text/html") + "\r\n\r\n");
                var bytes = File.ReadAllBytes(file); ms.Write(bytes, 0, bytes.Length);
                w("\r\n--" + boundary + "--\r\n");
                req.ContentLength = ms.Length; using (var s = req.GetRequestStream()) ms.WriteTo(s);
            }
            string body;
            try { using (var r = req.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd(); }
            catch (WebException ex) { throw new Exception("envoi du document refusé : " + ex.Message); }
            var id = IdOf(Store.Json().DeserializeObject(body) as Dictionary<string, object>);
            if (id == 0) throw new Exception("réponse GLPI inattendue à l'envoi du document");
            if (ticket > 0) try { Post(t, "/Document_Item/", new Dictionary<string, object> { { "documents_id", id }, { "itemtype", "Ticket" }, { "items_id", ticket } }); } catch { }
            return id;
        }

        public static string TicketUrl(int id) { return Base + "/front/ticket.form.php?id=" + id; }
        public static string H2(string text) { return Fmt.Html(text ?? "").Replace("\r\n", "\n").Replace("\n", "<br>"); }
    }

    // ===================================================================== Tactical RMM : agents typés
    static partial class Trmm
    {
        public class Ag { public string Id = "", Host = "", Client = "", Site = "", Status = "", Os = ""; public DateTime LastSeen; public bool Reboot, Patches; public int Failing; }

        public static Ag Parse(Dictionary<string, object> d)
        {
            var a = new Ag { Id = Http.S(d, "agent_id"), Host = Http.S(d, "hostname"), Client = Http.S(d, "client_name"), Site = Http.S(d, "site_name"), Status = Http.S(d, "status"), Os = Http.S(d, "operating_system") };
            DateTime ls; if (DateTime.TryParse(Http.S(d, "last_seen"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out ls)) a.LastSeen = ls.ToLocalTime();
            a.Reboot = Http.S(d, "needs_reboot").Equals("True", StringComparison.OrdinalIgnoreCase);
            a.Patches = Http.S(d, "has_patches_pending").Equals("True", StringComparison.OrdinalIgnoreCase);
            object ch; var cd = d.TryGetValue("checks", out ch) ? ch as Dictionary<string, object> : null; int f; if (cd != null && int.TryParse(Http.S(cd, "failing"), out f)) a.Failing = f;
            return a;
        }
        public static List<Ag> All() { return Agents().Select(Parse).ToList(); }
        public static bool OfClient(Ag a, Client c) { var code = Glpi.CodeOf(c.Name); return code.Length > 0 ? a.Client.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0 : Store.Norm(a.Client) == Store.Norm(c.Name); }

        // lien de déploiement d'un client (exe généré par Tactical RMM) : même domaine que l'API, https
        public static string CheckDeployUrl(string u)
        {
            u = (u ?? "").Trim(); Uri x, api;
            if (!Uri.TryCreate(u, UriKind.Absolute, out x) || x.Scheme != "https" || !Regex.IsMatch(x.AbsolutePath, @"^/clients/[A-Za-z0-9\-]+/deploy/?$")) throw new Exception("lien de déploiement invalide (attendu https://api.…/clients/<id>/deploy/)");
            if (!Uri.TryCreate(Url, UriKind.Absolute, out api)) throw new Exception("adresse de l'API Tactical RMM non configurée");
            Func<string, string> dom = h => string.Join(".", h.Split('.').Reverse().Take(2).Reverse());
            if (dom(x.Host) != dom(api.Host)) throw new Exception("le lien de déploiement doit être sur le domaine de ton serveur (" + dom(api.Host) + ")");
            return u;
        }
    }

    // ===================================================================== A. Recette de livraison (lecture seule)
    class Chk { public string Name = "", State = "na", Detail = ""; }   // State : ok, warn, bad, na

    static class Recette
    {
        public const string KitReport = @"C:\ProgramData\Amiprotek\Logs\rapport-deploiement.json";

        // une seule passe PowerShell, une ligne « R|clé|valeur » par mesure
        public const string Script =
            "$o=[ordered]@{};$o.Name=$env:COMPUTERNAME;" +
            "try{$w=Get-CimInstance SoftwareLicensingProduct -Filter \"ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL\" -EA Stop|sort {if($_.LicenseStatus -eq 1){0}else{1}}|select -First 1;$o.Win=$(if($w){[string]$w.LicenseStatus+';'+$w.Description}else{'none'})}catch{$o.Win='err'};" +
            "try{$of=@(Get-CimInstance SoftwareLicensingProduct -Filter \"ApplicationID='0ff1ce15-a989-479d-af46-f275c6370663' AND PartialProductKey IS NOT NULL\" -EA Stop);$ok=@($of|?{$_.LicenseStatus -eq 1});$o.Office=$(if($ok.Count){'1;'+$ok[0].Name}elseif($of.Count){'0;'+$of[0].Name}else{'none'})}catch{$o.Office='err'};" +
            "try{$v=Get-CimInstance -Namespace root\\cimv2\\Security\\MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -Filter \"DriveLetter='$env:SystemDrive'\" -EA Stop;$kp=(Invoke-CimMethod -InputObject $v -MethodName GetKeyProtectors -Arguments @{KeyProtectorType=[uint32]3}).VolumeKeyProtectorID;$o.BL=[string]$v.ProtectionStatus+';'+(@($kp) -join ',')}catch{$o.BL='err'};" +
            "$s=Get-Service glpi-agent -EA SilentlyContinue;$r=Get-ItemProperty 'HKLM:\\SOFTWARE\\GLPI-Agent' -EA SilentlyContinue;$o.Glpi=$(if($s){[string]$s.Status+';'+$r.server+';'+$r.tag}else{'none'});" +
            "$t=Get-Service tacticalrmm -EA SilentlyContinue;$o.Trmm=$(if($t){[string]$t.Status}else{'none'});" +
            "$o.Av=(@(Get-CimInstance -Namespace root\\SecurityCenter2 -ClassName AntiVirusProduct -EA SilentlyContinue|%{$_.displayName+':'+$_.productState}) -join ',');" +
            "$o.Kav=(@(Get-Service -EA SilentlyContinue|?{$_.Name -like 'AVP*' -or $_.Name -eq 'klnagent'}|%{$_.Name+':'+$_.Status}) -join ',');" +
            "$o.Reboot=[string]((Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Component Based Servicing\\RebootPending') -or (Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Auto Update\\RebootRequired'));" +
            "$h=Get-HotFix -EA SilentlyContinue|?{$_.InstalledOn}|sort InstalledOn -Descending|select -First 1;$o.Hotfix=$(if($h){$h.InstalledOn.ToString('yyyy-MM-dd')}else{''});" +
            "$u=Get-LocalUser -Name User -EA SilentlyContinue;$o.User=$(if($u){[string]$u.Enabled+';'+[string][bool](@(Get-LocalGroupMember -SID S-1-5-32-544 -EA SilentlyContinue)|?{$_.Name -like '*\\User'})}else{'none'});" +
            "$o.QS=[string]@(Get-ChildItem 'C:\\Users\\Public\\Desktop','C:\\Users\\User\\Desktop' -Filter '*QuickSupport*.exe' -EA SilentlyContinue).Count;" +
            "$o.Edition=[string](Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion').EditionID;" +
            "$o.GetEnumerator()|%{'R|'+$_.Key+'|'+[string]$_.Value}";

        public static Dictionary<string, string> ParseLocal(string output)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in (output ?? "").Split('\n')) { var p = l.Trim().Split(new[] { '|' }, 3); if (p.Length == 3 && p[0] == "R") d[p[1]] = p[2]; }
            return d;
        }

        public class Glpi_ { public bool Found; public int Entity, Id; public DateTime LastInventory, LastContact; public string Tag = ""; }

        static Chk C(string n, string s, string d) { return new Chk { Name = n, State = s, Detail = d }; }
        static string V(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? v ?? "" : ""; }

        // évaluation pure (testable) : mesures locales + rapport du kit + GLPI + Tactical RMM
        public static List<Chk> Evaluate(Client c, Dictionary<string, string> loc, Dictionary<string, object> kit, Glpi_ g, Trmm.Ag ag, bool glpiOn, bool trmmOn, DateTime now)
        {
            var l = new List<Chk>(); var code = Glpi.CodeOf(c.Name); var name = V(loc, "Name");

            if (kit == null) l.Add(C("Rapport du kit d'installation", "na", "absent : PC non installé avec la clé Amiprotek"));
            else
            {
                var res = Http.S(kit, "result"); object iss; var issues = kit.TryGetValue("issues", out iss) && iss is object[] ? string.Join(" ; ", ((object[])iss).Select(Convert.ToString)) : "";
                l.Add(C("Rapport du kit d'installation", Http.S(kit, "schema").StartsWith("amiprotek.deploiement/") ? (res == "CONFORME" ? "ok" : "warn") : "warn",
                    "kit v" + Http.S(kit, "kitVersion") + " : " + (res.Length > 0 ? res : "résultat absent") + (issues.Length > 0 ? " (" + issues + ")" : "")));
            }

            var rxName = code.Length > 0 ? "^" + Regex.Escape(Glpi.PcPrefix(code)) + @"\d{2}$" : @"^CLI\d{8}-\d{2}$";
            l.Add(C("Nom du PC", Regex.IsMatch(name, rxName, RegexOptions.IgnoreCase) ? "ok" : "bad", name + (code.Length > 0 ? " (attendu " + Glpi.PcPrefix(code) + "NN)" : " (client sans code CLI-xx-xx-xxxx)")));

            var win = V(loc, "Win"); var winOk = win.StartsWith("1;");
            l.Add(C("Licence Windows", winOk ? "ok" : win == "err" ? "na" : "bad", winOk ? "activée — " + win.Substring(2) : win == "none" ? "aucune clé installée" : win == "err" ? "lecture impossible" : "NON activée — " + win.Substring(Math.Min(2, win.Length))));
            var off = V(loc, "Office");
            l.Add(C("Licence Office", off == "none" ? "na" : off.StartsWith("1;") ? "ok" : off == "err" ? "na" : "bad", off == "none" ? "Office non installé (ou sans licence en volume)" : off.StartsWith("1;") ? "activé — " + off.Substring(2) : off == "err" ? "lecture impossible" : "NON activé — " + off.Substring(Math.Min(2, off.Length))));

            var bl = V(loc, "BL").Split(';'); var edition = V(loc, "Edition");
            if (bl[0] == "err" || bl[0].Length == 0) l.Add(C("BitLocker", edition.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 ? "warn" : "bad", edition.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 ? "Windows Famille : pas de BitLocker (chiffrement de l'appareil seulement)" : "état illisible"));
            else
            {
                var prot = bl.Length > 1 ? bl[1] : ""; var on = bl[0] == "1";
                l.Add(C("BitLocker", on && prot.Length > 0 ? "ok" : on ? "warn" : "bad", (on ? "actif" : "INACTIF") + (prot.Length > 0 ? " · clé de récupération " + prot.Split(',')[0] : " · aucune clé de récupération")));
                if (on && prot.Length > 0)
                {
                    var id = prot.Split(',')[0].Trim('{', '}');
                    var saved = Vault.Unlocked ? Vault.Items.Any(s => (s.Label + " " + s.Notes).IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0) : (bool?)null;
                    l.Add(C("Clé BitLocker séquestrée", saved == true ? "ok" : saved == null ? "warn" : "bad", saved == true ? "présente dans le coffre" : saved == null ? "coffre verrouillé : vérification impossible" : "ABSENTE du coffre (bouton « Séquestrer la clé »)"));
                }
            }

            var ga = V(loc, "Glpi").Split(';');
            if (ga[0] == "none" || ga[0].Length == 0) l.Add(C("Agent GLPI", "bad", "non installé"));
            else
            {
                var srv = ga.Length > 1 ? ga[1] : ""; var tag = ga.Length > 2 ? ga[2] : "";
                var bad = ga[0] != "Running" ? "service " + ga[0] : !Regex.IsMatch(srv, @"^https://[^/]*ami-tech\.fr", RegexOptions.IgnoreCase) ? "serveur inattendu : " + srv : code.Length > 0 && !tag.Equals(code, StringComparison.OrdinalIgnoreCase) ? "étiquette « " + tag + " » au lieu de « " + code + " » (le PC n'ira pas dans la bonne entité)" : null;
                l.Add(C("Agent GLPI", bad == null ? "ok" : "bad", bad ?? "en service · étiquette " + tag));
            }
            if (!glpiOn) l.Add(C("Inventaire reçu par GLPI", "na", "GLPI non configuré dans Connecteurs"));
            else if (g == null || !g.Found) l.Add(C("Inventaire reçu par GLPI", "bad", "aucun ordinateur « " + name + " » dans GLPI"));
            else
            {
                var age = g.LastInventory.Year > 2000 ? now - g.LastInventory : TimeSpan.MaxValue;
                var wrong = c.GlpiEntity > 0 && g.Entity != c.GlpiEntity;
                l.Add(C("Inventaire reçu par GLPI", wrong ? "bad" : age.TotalHours <= 48 ? "ok" : "warn", (wrong ? "MAUVAISE ENTITÉ (id " + g.Entity + " au lieu de " + c.GlpiEntity + ") · " : "") + (g.LastInventory.Year > 2000 ? "dernier inventaire " + g.LastInventory.ToString("dd/MM HH:mm") : "jamais inventorié") + (g.LastContact.Year > 2000 ? " · dernier contact " + g.LastContact.ToString("dd/MM HH:mm") : "")));
            }

            var tr = V(loc, "Trmm");
            if (!trmmOn) l.Add(C("Agent Tactical RMM", tr == "Running" ? "ok" : "bad", tr == "Running" ? "service en cours (API non configurée : état serveur non vérifié)" : "service " + (tr == "none" ? "absent" : tr)));
            else if (ag == null) l.Add(C("Agent Tactical RMM", "bad", "service " + (tr == "none" ? "absent" : tr) + " · aucun agent « " + name + " » sur le serveur"));
            else
            {
                var wrongClient = code.Length > 0 && ag.Client.IndexOf(code, StringComparison.OrdinalIgnoreCase) < 0;
                l.Add(C("Agent Tactical RMM", ag.Status == "online" && !wrongClient ? "ok" : "bad", ag.Status + " · client « " + ag.Client + " » / site « " + ag.Site + " »" + (wrongClient ? " : MAUVAIS CLIENT" : "")));
            }

            var avs = V(loc, "Av").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => { var i = x.LastIndexOf(':'); int st; int.TryParse(i > 0 ? x.Substring(i + 1) : "", out st); return new { Name = i > 0 ? x.Substring(0, i) : x, On = ((st >> 12) & 0xF) == 1, Fresh = ((st >> 4) & 0xF) == 0 }; }).ToList();
            var kas = avs.FirstOrDefault(x => x.Name.IndexOf("Kaspersky", StringComparison.OrdinalIgnoreCase) >= 0);
            var kSvc = V(loc, "Kav");
            l.Add(C("Kaspersky", kas == null || !kas.On ? "bad" : kas.Fresh ? "ok" : "warn", kas == null ? "non détecté" + (avs.Count > 0 ? " (antivirus : " + string.Join(", ", avs.Select(x => x.Name)) + ")" : "") : kas.Name + (kas.On ? " · actif" : " · INACTIF") + (kas.Fresh ? " · à jour" : " · bases NON à jour") + (kSvc.IndexOf("klnagent", StringComparison.OrdinalIgnoreCase) >= 0 ? " · agent réseau présent" : "")));

            DateTime hf; var hfOk = DateTime.TryParseExact(V(loc, "Hotfix"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out hf);
            var reboot = V(loc, "Reboot") == "True";
            l.Add(C("Mises à jour Windows", !hfOk ? "warn" : reboot || (now - hf).TotalDays > 45 ? "warn" : "ok", (hfOk ? "dernière mise à jour " + hf.ToString("dd/MM/yyyy") : "date inconnue") + (reboot ? " · REDÉMARRAGE EN ATTENTE" : "")));

            var us = V(loc, "User").Split(';');
            l.Add(C("Compte « User »", us[0] == "True" ? "ok" : "warn", us[0] == "none" ? "absent" : us[0] == "True" ? "actif" + (us.Length > 1 && us[1] == "True" ? ", administrateur" : ", non administrateur") : "désactivé"));
            l.Add(C("TeamViewer QuickSupport", V(loc, "QS") != "0" && V(loc, "QS").Length > 0 ? "ok" : "warn", V(loc, "QS") != "0" && V(loc, "QS").Length > 0 ? "présent sur le Bureau" : "absent du Bureau"));
            return l;
        }

        public static string Worst(IEnumerable<Chk> l) { return l.Any(x => x.State == "bad") ? "bad" : l.Any(x => x.State == "warn") ? "warn" : "ok"; }

        public static string Text(string pc, List<Chk> l)
        {
            var sb = new StringBuilder("Recette de livraison du poste " + pc + " — " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + "\r\n");
            foreach (var x in l) sb.Append(x.State == "ok" ? "[OK]  " : x.State == "warn" ? "[!]   " : x.State == "bad" ? "[KO]  " : "[--]  ").Append(x.Name).Append(" : ").Append(x.Detail).Append("\r\n");
            return sb.ToString();
        }

        // exécution complète sur ce PC
        public static List<Chk> Run(Client c, Action<string, string> log)
        {
            log("Mesures locales (licences, BitLocker, agents, antivirus, mises à jour)…", "muted");
            var loc = ParseLocal(Sh.Ps(Script, 180000));
            if (!loc.ContainsKey("Name")) loc["Name"] = Environment.MachineName;
            Dictionary<string, object> kit = null;
            try { if (File.Exists(KitReport)) kit = Store.Json().DeserializeObject(File.ReadAllText(KitReport, Encoding.UTF8)) as Dictionary<string, object>; } catch (Exception ex) { log("Rapport du kit illisible : " + ex.Message, "warn"); }
            Glpi_ g = null;
            if (Glpi.Configured)
                try
                {
                    log("GLPI : recherche de " + loc["Name"] + "…", "muted");
                    g = Glpi.Session(t =>
                    {
                        var r = new Glpi_(); var pc = Glpi.FindComputer(t, loc["Name"]); if (pc == null) return r;
                        r.Found = true; r.Id = Glpi.IdOf(pc); int e; int.TryParse(Http.S(pc, "entities_id"), out e); r.Entity = e;
                        DateTime d; if (DateTime.TryParse(Http.S(pc, "last_inventory_update"), CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) r.LastInventory = d;
                        var a = Glpi.Agents(t).FirstOrDefault(x => Http.S(x, "itemtype") == "Computer" && Http.S(x, "items_id") == r.Id.ToString());
                        if (a != null) { if (DateTime.TryParse(Http.S(a, "last_contact"), CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) r.LastContact = d; r.Tag = Http.S(a, "tag"); }
                        return r;
                    });
                }
                catch (Exception ex) { log("GLPI injoignable : " + ex.Message, "warn"); }
            Trmm.Ag ag = null;
            if (Trmm.Configured)
                try { log("Tactical RMM : recherche de l'agent…", "muted"); ag = Trmm.All().FirstOrDefault(x => x.Host.Equals(loc["Name"], StringComparison.OrdinalIgnoreCase)); }
                catch (Exception ex) { log("Tactical RMM injoignable : " + ex.Message, "warn"); }
            LastGlpiId = g != null && g.Found ? g.Id : 0; LastPc = loc["Name"];
            return Evaluate(c, loc, kit, g, ag, Glpi.Configured, Trmm.Configured, DateTime.Now);
        }
        public static int LastGlpiId; public static string LastPc = "";

        // séquestre de la clé de récupération dans le coffre AMI·OPS (jamais dans GLPI : lisible par tout technicien)
        public static string Escrow(Client c)
        {
            if (!Vault.Unlocked) throw new Exception("déverrouille le coffre d'abord (page Coffre)");
            var o = Sh.Ps("$k=(Get-BitLockerVolume -MountPoint $env:SystemDrive -EA Stop).KeyProtector|?{$_.KeyProtectorType -eq 'RecoveryPassword'}|select -First 1;if($k){'KEY|'+$k.KeyProtectorId+'|'+$k.RecoveryPassword}", 60000);
            var m = Regex.Match(o, @"KEY\|\{?([0-9A-Fa-f\-]{36})\}?\|([0-9\-]{55})");
            if (!m.Success) throw new Exception("aucune clé de récupération BitLocker lisible sur ce PC");
            var label = "BitLocker " + Environment.MachineName;
            var s = Vault.Items.FirstOrDefault(x => x.Label == label && x.ClientId == c.Id) ?? new Secret { Id = Store.NewId(), ClientId = c.Id, Label = label };
            if (!Vault.Items.Contains(s)) Vault.Items.Add(s);
            s.User = "ID " + m.Groups[1].Value; s.Notes = "Clé de récupération BitLocker du lecteur système, ID " + m.Groups[1].Value + " — séquestrée le " + DateTime.Now.ToString("dd/MM/yyyy");
            s.Cipher = Vault.Encrypt(m.Groups[2].Value); s.Updated = DateTime.Now; Vault.Save();
            return m.Groups[1].Value;
        }
    }

    // ===================================================================== PDF : Microsoft Edge sans fenêtre (présent sur tout Windows 10/11)
    static class Pdf
    {
        public static string Edge()
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            { if (root.Length == 0) continue; var p = Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe"); if (File.Exists(p)) return p; }
            return null;
        }
        // HTML → PDF ; null si Edge absent (le HTML reste imprimable)
        public static string FromHtml(string html)
        {
            var edge = Edge(); if (edge == null) return null;
            var pdf = Path.ChangeExtension(html, ".pdf"); var prof = Hardening.SecureTempDir("AmiOps-edge");
            try
            {
                string o; Proc.Run(edge, "--headless=new --disable-gpu --no-first-run --no-default-browser-check --user-data-dir=\"" + prof + "\" --no-pdf-header-footer --print-to-pdf=\"" + pdf + "\" \"" + new Uri(html).AbsoluteUri + "\"", 90000, out o);
            }
            finally { try { Directory.Delete(prof, true); } catch { } }
            return File.Exists(pdf) && new FileInfo(pdf).Length > 500 ? pdf : null;
        }
    }

    // ===================================================================== B. Enrôlement d'un PC existant
    static class Enrol
    {
        public static string AgentServer { get { return Prefs.Ini.Get("GlpiAgentServer", "https://glpi.ami-tech.fr:8443/front/inventory.php"); } }

        // dernière version de GLPI Agent sur GitHub, empreinte SHA-256 publiée par GitHub (champ digest) obligatoire
        public static string DownloadAgent(Action<string, string> log)
        {
            var r = Http.Call("GET", "https://api.github.com/repos/glpi-project/glpi-agent/releases/latest", null, null) as Dictionary<string, object>;
            object assets; var a = r != null && r.TryGetValue("assets", out assets) ? Http.List(assets).FirstOrDefault(x => Regex.IsMatch(Http.S(x, "name"), @"^GLPI-Agent-[\d.]+-x64\.msi$")) : null;
            if (a == null) throw new Exception("MSI x64 introuvable dans la dernière version de GLPI Agent");
            var digest = Http.S(a, "digest"); var m = Regex.Match(digest, @"^sha256:([0-9a-f]{64})$");
            if (!m.Success) throw new Exception("GitHub ne publie pas d'empreinte pour ce fichier : indique un MSI local vérifié");
            log("Téléchargement de " + Http.S(a, "name") + "…", "muted");
            var dir = Hardening.SecureTempDir("AmiOps-glpi"); var file = Path.Combine(dir, Http.S(a, "name"));
            using (var wc = new WebClient()) { wc.Headers["User-Agent"] = "AmiOps/" + AppInfo.Version; wc.DownloadFile(Http.S(a, "browser_download_url"), file); }
            string h; using (var sha = SHA256.Create()) using (var fs = File.OpenRead(file)) h = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (h != m.Groups[1].Value) { try { Directory.Delete(dir, true); } catch { } throw new Exception("EMPREINTE SHA-256 INCORRECTE : MSI supprimé"); }
            log("Empreinte SHA-256 vérifiée.", "ok");
            return file;
        }

        // installation silencieuse : service, étiquette = code client (règle GLPI « Tag = code client → entité »)
        public static void InstallAgent(string msi, string tag, string user, string pass, Action<string, string> log)
        {
            if (!File.Exists(msi)) throw new Exception("MSI introuvable : " + msi);
            if (!Regex.IsMatch(tag, @"^CLI-\d{2}-\d{2}-\d{4}$")) throw new Exception("étiquette invalide : " + tag);
            if (!Regex.IsMatch(AgentServer, @"^https://[A-Za-z0-9.\-]+(:\d+)?/[\w./\-]*$")) throw new Exception("adresse du serveur d'inventaire invalide");
            Func<string, string> q = v => "\"" + (v ?? "").Replace("\"", "") + "\"";
            var args = "/i " + q(msi) + " /qn /norestart SERVER=" + q(AgentServer) + " TAG=" + q(tag) + " RUNNOW=1 EXECMODE=1 ADD_FIREWALL_EXCEPTION=0" + (user.Length > 0 ? " USER=" + q(user) + " PASSWORD=" + q(pass) : "");
            log("Installation de GLPI Agent (étiquette " + tag + ")…", "muted");
            string o; var rc = Proc.Run(Proc.Sys("msiexec.exe"), args, 600000, out o);
            if (rc != 0 && rc != 3010) throw new Exception("msiexec code " + rc);
            log("GLPI Agent installé" + (rc == 3010 ? " (redémarrage demandé)" : "") + ", premier inventaire lancé.", "ok");
        }

        public static void InstallTrmm(string deployUrl, Action<string, string> log)
        {
            var u = Trmm.CheckDeployUrl(deployUrl);
            var dir = Hardening.SecureTempDir("AmiOps-trmm"); var exe = Path.Combine(dir, "tacticalrmm-deploy.exe");
            log("Téléchargement de l'installateur Tactical RMM du client…", "muted");
            using (var wc = new WebClient()) { wc.Headers["User-Agent"] = "AmiOps/" + AppInfo.Version; wc.DownloadFile(u, exe); }
            if (new FileInfo(exe).Length < 100000) throw new Exception("fichier reçu trop petit : lien expiré ?");
            string o; var rc = Proc.Run(exe, "", 900000, out o);
            try { Directory.Delete(dir, true); } catch { }
            if (rc != 0) throw new Exception("installateur Tactical RMM code " + rc);
            log("Agent Tactical RMM installé.", "ok");
        }

        public static void BitLocker(Action<string, string> log)
        {
            log("BitLocker : activation (TPM + clé de récupération)…", "muted");
            var o = Sh.Ps("$ErrorActionPreference='Stop';$d=$env:SystemDrive;$v=Get-BitLockerVolume -MountPoint $d;" +
                "if($v.ProtectionStatus -ne 'On' -and $v.VolumeStatus -eq 'FullyDecrypted'){if(-not (Get-Tpm).TpmReady){throw 'TPM absent ou non prêt'};Enable-BitLocker -MountPoint $d -EncryptionMethod XtsAes128 -UsedSpaceOnly -TpmProtector -SkipHardwareTest|Out-Null};" +
                "if(-not ((Get-BitLockerVolume -MountPoint $d).KeyProtector|?{$_.KeyProtectorType -eq 'RecoveryPassword'})){Add-BitLockerKeyProtector -MountPoint $d -RecoveryPasswordProtector -WarningAction SilentlyContinue|Out-Null};" +
                "$v=Get-BitLockerVolume -MountPoint $d;'ETAT|'+$v.VolumeStatus+'|'+$v.EncryptionPercentage", 300000);
            var m = Regex.Match(o, @"ETAT\|(\w+)\|(\d+)");
            if (!m.Success) throw new Exception("BitLocker : " + o.Trim().Split('\n').Last());
            log("BitLocker : " + m.Groups[1].Value + " (" + m.Groups[2].Value + " %).", "ok");
        }

        public static void Rename(string name, Action<string, string> log)
        {
            if (!Regex.IsMatch(name, @"^CLI\d{8}-\d{2}$")) throw new Exception("nom invalide : " + name);
            if (Environment.MachineName.Equals(name, StringComparison.OrdinalIgnoreCase)) { log("Nom déjà correct : " + name, "ok"); return; }
            var o = Sh.Ps("Rename-Computer -NewName " + Sh.Q(name) + " -Force -EA Stop -WarningAction SilentlyContinue;'OK'", 60000);
            if (!o.Contains("OK")) throw new Exception("renommage refusé : " + o.Trim());
            log("PC renommé en " + name + " : effectif au prochain redémarrage.", "ok");
        }
    }

    // ===================================================================== D. Tableau de bord du parc (GLPI + Tactical RMM)
    class ParcRow
    {
        public string Pc = "", AgentId = "", OsName = "", OsVersion = "", AvName = "", TrmmStatus = "";
        public int GlpiId; public DateTime LastSeen; public double FreePct = -1; public int Encrypted = -1;
        public bool? AvActive, AvFresh; public bool Reboot, Patches; public int Failing;
        public List<string> Issues = new List<string>(); public int Level;   // 0 ok, 1 alerte, 2 critique
    }

    static class Parc
    {
        public static void Eval(ParcRow r, DateTime now)
        {
            r.Issues.Clear(); int lvl = 0; Action<int, string> add = (l, s) => { r.Issues.Add(s); lvl = Math.Max(lvl, l); };
            if (r.LastSeen.Year < 2000) add(1, "jamais vu");
            else if ((now - r.LastSeen).TotalDays > 7) add(2, "pas vu depuis " + (int)(now - r.LastSeen).TotalDays + " j");
            if (r.FreePct >= 0 && r.FreePct < 10) add(2, "disque système plein (" + r.FreePct.ToString("0") + " % libre)"); else if (r.FreePct >= 0 && r.FreePct < 20) add(1, "disque système à " + r.FreePct.ToString("0") + " % libre");
            if (r.Encrypted == 0 || r.Encrypted == 2) add(1, "BitLocker " + (r.Encrypted == 0 ? "inactif" : "partiel"));
            if (r.AvActive == null && r.GlpiId > 0) add(2, "aucun antivirus inventorié"); else if (r.AvActive == false) add(2, "antivirus inactif"); else if (r.AvFresh == false) add(1, "antivirus pas à jour");
            if (Regex.IsMatch(r.OsName, @"Windows 10", RegexOptions.IgnoreCase)) add(2, "Windows 10 : plus de mises à jour depuis le 14/10/2025 (hors ESU)");
            else if (Regex.IsMatch(r.OsVersion, @"^(21H2|22H2|23H2)$")) add(1, "Windows 11 " + r.OsVersion + " : version plus suivie");
            if (r.Reboot) add(1, "redémarrage en attente");
            if (r.Patches) add(1, "mises à jour en attente");
            if (r.Failing > 0) add(1, r.Failing + " contrôle(s) Tactical RMM en échec");
            r.Level = lvl;
        }

        static DateTime D(string s) { DateTime d; return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : DateTime.MinValue; }

        public static List<ParcRow> Load(Client c, Action<string, string> log)
        {
            var rows = new Dictionary<string, ParcRow>(StringComparer.OrdinalIgnoreCase);
            Func<string, ParcRow> row = n => { ParcRow r; if (!rows.TryGetValue(n, out r)) rows[n] = r = new ParcRow { Pc = n }; return r; };
            if (Glpi.Configured && c.GlpiEntity > 0)
                Glpi.Session(t =>
                {
                    var agents = Glpi.Agents(t);
                    var pcs = Glpi.Computers(t).Where(x => Http.S(x, "entities_id") == c.GlpiEntity.ToString()).ToList();
                    log("GLPI : " + pcs.Count + " ordinateur(s) dans l'entité.", "muted");
                    foreach (var pc in pcs)
                    {
                        var r = row(Http.S(pc, "name")); r.GlpiId = Glpi.IdOf(pc);
                        var a = agents.FirstOrDefault(x => Http.S(x, "itemtype") == "Computer" && Http.S(x, "items_id") == r.GlpiId.ToString());
                        var seen = a != null ? D(Http.S(a, "last_contact")) : D(Http.S(pc, "last_inventory_update")); if (seen > r.LastSeen) r.LastSeen = seen;
                        var disk = Glpi.Sub(t, r.GlpiId, "Item_Disk").FirstOrDefault(x => Regex.IsMatch(Http.S(x, "mountpoint"), @"^C:", RegexOptions.IgnoreCase));
                        double tot, fr; if (disk != null && double.TryParse(Http.S(disk, "totalsize"), NumberStyles.Any, CultureInfo.InvariantCulture, out tot) && tot > 0 && double.TryParse(Http.S(disk, "freesize"), NumberStyles.Any, CultureInfo.InvariantCulture, out fr)) r.FreePct = fr * 100 / tot;
                        int enc; if (disk != null && int.TryParse(Http.S(disk, "encryption_status"), out enc)) r.Encrypted = enc;
                        var av = Glpi.Sub(t, r.GlpiId, "ItemAntivirus", "ComputerAntivirus").FirstOrDefault();
                        if (av != null) { r.AvName = Http.S(av, "name"); r.AvActive = Http.S(av, "is_active") == "1"; r.AvFresh = Http.S(av, "is_uptodate") == "1"; }
                        var os = Glpi.Sub(t, r.GlpiId, "Item_OperatingSystem").FirstOrDefault();
                        if (os != null) { r.OsName = Http.S(os, "operatingsystems_id"); r.OsVersion = Http.S(os, "operatingsystemversions_id"); }
                    }
                    return 0;
                });
            if (Trmm.Configured)
            {
                var ags = Trmm.All().Where(a => Trmm.OfClient(a, c)).ToList();
                log("Tactical RMM : " + ags.Count + " agent(s) du client.", "muted");
                foreach (var a in ags)
                {
                    var r = row(a.Host); r.AgentId = a.Id; r.TrmmStatus = a.Status; r.Reboot = a.Reboot; r.Patches = a.Patches; r.Failing = a.Failing;
                    if (a.LastSeen > r.LastSeen) r.LastSeen = a.LastSeen;
                    if (r.OsName.Length == 0) r.OsName = a.Os;
                }
            }
            var list = rows.Values.OrderByDescending(r => { Eval(r, DateTime.Now); return r.Level; }).ThenBy(r => r.Pc).ToList();
            return list;
        }
    }

    // ===================================================================== E. Rapport mensuel de conformité
    static class Monthly
    {
        public static string Html(Client c, List<ParcRow> rows, DateTime month, List<Intervention> inter)
        {
            var co = Prefs.Ini.Get("Societe", "AMIPROTEK"); var ok = rows.Count(r => r.Level == 0);
            var sb = new StringBuilder("<!DOCTYPE html><html lang=fr><head><meta charset=utf-8><title>Conformité - ");
            sb.Append(Fmt.Html(c.Name)).Append(" - ").Append(month.ToString("MM-yyyy")).Append("</title><style>@page{size:A4;margin:14mm}body{font-family:'Segoe UI',Arial,sans-serif;color:#1a2230;font-size:10pt;max-width:780px;margin:24px auto;padding:0 16px}")
              .Append("h1{font-size:17pt;color:#0084a8;margin:0}h2{font-size:11pt;color:#0084a8;text-transform:uppercase;border-bottom:1px solid #d6dee8;margin:18px 0 6px}")
              .Append("table{width:100%;border-collapse:collapse}td,th{border:1px solid #d6dee8;padding:4px 6px;text-align:left;vertical-align:top}th{background:#f1f5f9}.ok{color:#007a43;font-weight:600}.warn{color:#a35d00;font-weight:600}.bad{color:#b0102f;font-weight:600}")
              .Append(".kpi{display:flex;gap:10px}.kpi div{flex:1;border:1px solid #d6dee8;padding:8px;text-align:center}.kpi b{display:block;font-size:18pt}footer{margin-top:20px;font-size:8pt;color:#8a97a6;text-align:center}</style></head><body>")
              .Append("<h1>Rapport de conformité du parc</h1><div>").Append(Fmt.Html(c.Name)).Append(" · ").Append(month.ToString("MMMM yyyy", new CultureInfo("fr-FR"))).Append("</div>")
              .Append("<h2>Synthèse</h2><div class=kpi><div><b>").Append(rows.Count).Append("</b>postes suivis</div><div><b class=ok>").Append(ok).Append("</b>conformes</div><div><b class=warn>").Append(rows.Count(r => r.Level == 1)).Append("</b>à surveiller</div><div><b class=bad>").Append(rows.Count(r => r.Level == 2)).Append("</b>à traiter</div><div><b>").Append(inter.Count).Append("</b>interventions</div></div>")
              .Append("<h2>Postes</h2><table><tr><th>Poste</th><th>Système</th><th>Dernier contact</th><th>Disque</th><th>Antivirus</th><th>État</th></tr>");
            foreach (var r in rows)
                sb.Append("<tr><td>").Append(Fmt.Html(r.Pc)).Append("</td><td>").Append(Fmt.Html((r.OsName + " " + r.OsVersion).Trim())).Append("</td><td>").Append(r.LastSeen.Year > 2000 ? r.LastSeen.ToString("dd/MM/yyyy") : "—")
                  .Append("</td><td>").Append(r.FreePct >= 0 ? r.FreePct.ToString("0") + " % libre" : "—").Append(r.Encrypted == 1 ? " · chiffré" : "").Append("</td><td>").Append(Fmt.Html(r.AvName.Length > 0 ? r.AvName : "—"))
                  .Append("</td><td class=").Append(r.Level == 0 ? "ok" : r.Level == 1 ? "warn" : "bad").Append(">").Append(r.Issues.Count == 0 ? "Conforme" : Fmt.Html(string.Join(" ; ", r.Issues))).Append("</td></tr>");
            sb.Append("</table>");
            if (inter.Count > 0)
            {
                sb.Append("<h2>Interventions du mois</h2><table><tr><th>Date</th><th>Type</th><th>Objet</th><th>Durée</th><th>Résultat</th></tr>");
                foreach (var i in inter.OrderBy(x => x.Date)) sb.Append("<tr><td>").Append(i.Date.ToString("dd/MM")).Append("</td><td>").Append(Fmt.Html(i.Type)).Append("</td><td>").Append(Fmt.Html(i.Demande.Split('\n')[0])).Append("</td><td>").Append(HomePage.Fmt(i.TotalMinutes)).Append("</td><td>").Append(Fmt.Html(i.Result)).Append("</td></tr>");
                sb.Append("</table>");
            }
            sb.Append("<footer>").Append(Fmt.Html(co)).Append(" · rapport généré le ").Append(DateTime.Now.ToString("dd/MM/yyyy")).Append(" · AMI·OPS by alex.b</footer></body></html>");
            return sb.ToString();
        }

        public static string Write(Client c, List<ParcRow> rows, DateTime month)
        {
            var inter = Store.D.Interventions.Where(i => i.ClientId == c.Id && i.Date.Year == month.Year && i.Date.Month == month.Month).ToList();
            var dir = Path.Combine(Report.Dir, "Conformité"); Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, month.ToString("yyyy-MM") + "_" + Regex.Replace(c.Name, @"[^\p{L}\p{N}\-_ ]", "").Trim() + ".html");
            File.WriteAllText(file, Html(c, rows, month, inter), new UTF8Encoding(false));
            return Pdf.FromHtml(file) ?? file;
        }
    }

    // ===================================================================== H. Santé des serveurs (HTTPS, certificat)
    static class Infra
    {
        public static string[] Urls { get { return Prefs.Ini.Get("InfraUrls", "https://glpi.ami-tech.fr:8443/|https://rmm.ami-tech.fr/|https://api.ami-tech.fr/|https://mesh.ami-tech.fr/").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries); } }
        public class Res { public string Url, Detail; public int Code, Ms, Days = -1; public string Level; }

        public static Res Check(string url)
        {
            var r = new Res { Url = url }; var sw = Stopwatch.StartNew();
            System.Security.Cryptography.X509Certificates.X509Certificate2 cert = null; System.Net.Security.SslPolicyErrors errs = 0;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url); req.Method = "GET"; req.Timeout = 15000; req.AllowAutoRedirect = false; req.UserAgent = "AmiOps/" + AppInfo.Version;
                req.ServerCertificateValidationCallback = (s, c, ch, e) => { if (c != null) cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(c); errs = e; return e == System.Net.Security.SslPolicyErrors.None; };
                try { using (var resp = (HttpWebResponse)req.GetResponse()) r.Code = (int)resp.StatusCode; }
                catch (WebException ex) { var hr = ex.Response as HttpWebResponse; if (hr == null) throw; r.Code = (int)hr.StatusCode; hr.Close(); }
            }
            catch (Exception ex) { r.Detail = errs != 0 ? "CERTIFICAT REFUSÉ (" + errs + ")" : ex.Message; }
            r.Ms = (int)sw.ElapsedMilliseconds;
            if (cert != null) r.Days = (int)Math.Floor((cert.NotAfter - DateTime.Now).TotalDays);
            r.Level = r.Code == 0 || r.Code >= 500 || (r.Days >= 0 && r.Days < 7) ? "bad" : (r.Days >= 0 && r.Days < 21) || r.Ms > 3000 ? "warn" : "ok";
            if (r.Detail == null) r.Detail = "HTTP " + r.Code + " · " + r.Ms + " ms" + (r.Days >= 0 ? " · certificat valable " + r.Days + " j" : "");
            return r;
        }
        public static List<Res> All() { return Urls.AsParallel().AsOrdered().Select(Check).ToList(); }
    }

    // ===================================================================== Page « Parc client »
    class ParcPage : Page
    {
        readonly ComboBox client; readonly Grid grid; readonly Label stat; readonly LogBox box;
        List<Chk> lastChecks; List<ParcRow> lastRows; Intervention lastPv;
        public override string Title { get { return "Parc client"; } }

        public ParcPage()
        {
            Lbl("PARC CLIENT", 0, k(2), "accent", 13f, true);
            Lbl("Projet GLPI Amiprotek : recette de livraison, enrôlement, état du parc, rapport mensuel, NAS et tickets GLPI.", k(150), k(10), "muted");
            Lbl("Client", 0, k(46), "muted");
            client = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(k(50), k(42)), Width = k(330) }; Controls.Add(client);
            int x = k(392), y = k(40);
            x = Btn("▶  Recette de ce PC", t => t.Ok, x, y, (s, e) => RunRecette(), true).Right + k(6);
            x = Btn("PV + signature", t => t.Accent, x, y, (s, e) => Pv()).Right + k(6);
            x = Btn("→ GLPI (livraison)", t => t.Accent2, x, y, (s, e) => SendPv()).Right + k(6);
            x = Btn("Séquestrer la clé", t => t.Warn, x, y, (s, e) => Escrow()).Right + k(6);
            Btn("Enrôler ce PC…", t => t.Accent, x, y, (s, e) => Enroll());
            int x2 = 0, y2 = k(80);
            x2 = Btn("▤  État du parc", t => t.Ok, x2, y2, (s, e) => LoadParc(), true).Right + k(6);
            x2 = Btn("Ticket pour ce PC", t => t.Accent2, x2, y2, (s, e) => TicketRow()).Right + k(6);
            x2 = Btn("Rapport mensuel", t => t.Accent, x2, y2, (s, e) => MonthlyOne()).Right + k(6);
            x2 = Btn("Rapports de tous les clients", t => t.Muted, x2, y2, (s, e) => MonthlyAll()).Right + k(6);
            x2 = Btn("Tickets GLPI du client", t => t.Accent, x2, y2, (s, e) => Tickets()).Right + k(6);
            Btn("NAS → GLPI", t => t.Warn, x2, y2, (s, e) => NasToGlpi());
            stat = Lbl("", 0, k(120), "muted");
            grid = new Grid(); grid.Location = new Point(0, k(142)); Controls.Add(grid);
            box = new LogBox(); Controls.Add(box);
            grid.SelectedIndexChanged += (s, e) => ShowDetail();
            grid.Col("Contrôle", 230).Col("État", 70).Col("Détail", 640);
            Resize += (s, e) =>
            {
                int h = ClientSize.Height - grid.Top, gh = Math.Max(k(120), h * 62 / 100);
                grid.Size = new Size(ClientSize.Width, gh); box.Location = new Point(0, grid.Bottom + k(8)); box.Size = new Size(ClientSize.Width, Math.Max(k(60), ClientSize.Height - box.Top));
            };
        }

        public override void OnShow()
        {
            var sel = client.SelectedItem as string; client.Items.Clear();
            foreach (var c in Store.D.Clients.OrderBy(c => c.Name)) client.Items.Add(c.Name);
            if (sel != null && client.Items.Contains(sel)) client.SelectedItem = sel; else if (client.Items.Count > 0) client.SelectedIndex = 0;
        }

        Client Cur(bool needCode = false)
        {
            var c = Store.D.Clients.FirstOrDefault(x => x.Name == client.SelectedItem as string);
            if (c == null) { CyberForm.Info(this, "Choisis un client (ou crée-le / synchronise GLPI)."); return null; }
            if (needCode && Glpi.CodeOf(c.Name).Length == 0) { CyberForm.Info(this, "Le nom du client doit contenir son code (CLI-09-16-0699 - Nom) : c'est l'étiquette GLPI et le préfixe des noms de PC."); return null; }
            return c;
        }
        void Out(string s, string c) { Ui(() => box.Line(s, c)); var j = Job.Current; if (j != null) j.Record(s, c); }
        void Ui(Action a) { try { if (IsHandleCreated) BeginInvoke(a); } catch { } }
        static Color Col(string st) { var t = Theme.Current; return st == "ok" ? t.Ok : st == "warn" ? t.Warn : st == "bad" ? t.Bad : t.Muted; }

        // ---- A
        void RunRecette()
        {
            var c = Cur(); if (c == null) return;
            stat.Text = "Recette en cours (suivi dans Tâches)…";
            Jobs.Run("Recette de livraison " + Environment.MachineName, null, j =>
            {
                var l = Recette.Run(c, Out); lastChecks = l;
                foreach (var x in l) j.Record((x.State == "ok" ? "✓ " : x.State == "na" ? "· " : "✗ ") + x.Name + " : " + x.Detail, x.State == "na" ? "muted" : x.State);
                Ui(() => ShowChecks(l));
            });
        }
        void ShowChecks(List<Chk> l)
        {
            grid.BeginUpdate(); grid.Items.Clear(); grid.Columns.Clear(); grid.Col("Contrôle", 230).Col("État", 70).Col("Détail", 640);
            foreach (var x in l) grid.Items.Add(new ListViewItem(new[] { x.Name, x.State == "ok" ? "OK" : x.State == "warn" ? "ALERTE" : x.State == "bad" ? "KO" : "—", x.Detail }) { Tag = x, ForeColor = Col(x.State) });
            grid.EndUpdate();
            var w = Recette.Worst(l); stat.Text = Recette.LastPc + " : " + (w == "ok" ? "CONFORME" : w == "warn" ? "conforme avec réserves" : "NON CONFORME") + " · " + l.Count(x => x.State == "bad") + " KO, " + l.Count(x => x.State == "warn") + " alerte(s)";
            stat.ForeColor = Col(w);
        }
        void Pv()
        {
            var c = Cur(); if (c == null) return;
            if (lastChecks == null) { CyberForm.Info(this, "Lance d'abord la recette de ce PC."); return; }
            var w = Recette.Worst(lastChecks);
            var a = c.Assets.FirstOrDefault(x => x.Name.Equals(Recette.LastPc, StringComparison.OrdinalIgnoreCase));
            if (a == null) { a = new Asset { Id = Store.NewId(), Kind = "PC", Name = Recette.LastPc, Added = DateTime.Now }; c.Assets.Add(a); }
            var i = new Intervention
            {
                Id = Store.NewId(), ClientId = c.Id, AssetId = a.Id, Date = DateTime.Today, RunningSince = DateTime.Now, Tech = Prefs.Ini.Get("Tech", ""), Type = "Livraison", Lieu = "Sur site",
                Demande = "Livraison et mise en service du poste " + Recette.LastPc + ".", Actions = Recette.Text(Recette.LastPc, lastChecks),
                Result = w == "ok" ? "Résolu" : w == "warn" ? "Partiellement résolu" : "Non résolu"
            };
            Store.D.Interventions.Add(i); Store.Save(); lastPv = i;
            Shell.EditIntervention(i);
        }
        void SendPv()
        {
            var c = Cur(); if (c == null) return;
            if (lastPv == null) { CyberForm.Info(this, "Fais d'abord le PV (signature du client)."); return; }
            if (!Glpi.Configured || c.GlpiEntity == 0) { CyberForm.Info(this, "GLPI non configuré ou client non lié à une entité (Connecteurs > Synchroniser)."); return; }
            var i = lastPv; var pcId = Recette.LastGlpiId; var pc = Recette.LastPc;
            Jobs.Run("Livraison " + pc + " → GLPI", null, j =>
            {
                var html = Path.Combine(Report.Dir, i.Date.ToString("yyyy-MM-dd") + "_PV-livraison_" + pc + ".html");
                File.WriteAllText(html, Report.Html(i), new UTF8Encoding(false));
                var doc = Pdf.FromHtml(html) ?? html; j.Log("PV : " + doc, "muted");
                var id = Glpi.Session(t =>
                {
                    var tid = i.GlpiTicket > 0 ? i.GlpiTicket : Glpi.NewTicket(t, c.GlpiEntity, "Livraison du poste " + pc, Glpi.H2(Report.Text(i)), 2, 2);
                    if (pcId > 0) try { Glpi.LinkComputer(t, tid, pcId); } catch { }
                    Glpi.Task(t, tid, Glpi.H2("Livraison, mise en service et recette du poste " + pc + "."), i.TotalMinutes);
                    Glpi.Upload(t, doc, c.GlpiEntity, tid);
                    if (i.Result == "Résolu") Glpi.Solve(t, tid, Glpi.H2("Poste livré et conforme. PV signé joint."));
                    return tid;
                });
                i.GlpiTicket = id; Store.Save();
                j.Log("Ticket GLPI n°" + id + (i.Result == "Résolu" ? " créé et résolu" : " créé (réserves à lever)") + ", PV joint : " + Glpi.TicketUrl(id), "ok");
            });
        }
        void Escrow()
        {
            var c = Cur(); if (c == null) return;
            try { var id = Recette.Escrow(c); CyberForm.Info(this, "Clé de récupération séquestrée dans le coffre (ID " + id + ").\nGLPI ne reçoit jamais la clé."); }
            catch (Exception ex) { CyberForm.Warn(this, ex.Message); }
        }

        // ---- B
        void Enroll()
        {
            var c = Cur(true); if (c == null) return; var code = Glpi.CodeOf(c.Name);
            int next = 1;
            if (Glpi.Configured) try { next = Glpi.Session(t => Glpi.NextNumber(Glpi.Computers(t).Select(x => Http.S(x, "name")).Concat(c.Assets.Select(a => a.Name)), code)); }
                catch (Exception ex) { CyberForm.Warn(this, "GLPI injoignable, numéro calculé depuis la fiche client : " + ex.Message); next = Glpi.NextNumber(c.Assets.Select(a => a.Name), code); }
            else next = Glpi.NextNumber(c.Assets.Select(a => a.Name), code);
            var yn = new[] { "Oui", "Non" };
            var fName = new F("Nom du PC (renommage au prochain redémarrage)", Regex.IsMatch(Environment.MachineName, "^" + Regex.Escape(Glpi.PcPrefix(code)) + @"\d{2}$") ? Environment.MachineName : Glpi.PcName(code, next)) { Required = true };
            var fRen = new F("Renommer le PC", "Oui") { Options = yn, Editable = false };
            var fGlpi = new F("Installer GLPI Agent (étiquette " + code + ")", "Oui") { Options = yn, Editable = false };
            var fMsi = new F("MSI GLPI Agent local (vide = dernière version GitHub, empreinte vérifiée)", Prefs.Ini.Get("GlpiAgentMsi", ""));
            var fUser = new F("Compte des agents (accès HTTP au serveur, vide = aucun)", Prefs.Ini.Get("GlpiAgentUser", ""));
            var fPass = new F("Mot de passe du compte des agents", Secrets.Get("GlpiAgentPass")) { Password = true };
            var fTr = new F("Installer l'agent Tactical RMM", "Oui") { Options = yn, Editable = false };
            var fDep = new F("Lien de déploiement Tactical RMM du client (Agents > Déploiements)", Secrets.Get("TrmmDeploy." + c.Id));
            var fBl = new F("Activer BitLocker + séquestrer la clé dans le coffre", "Oui") { Options = yn, Editable = false };
            if (!Dlg.Edit(this, "Enrôler ce PC — " + c.Name, fName, fRen, fGlpi, fMsi, fUser, fPass, fTr, fDep, fBl)) return;
            if (!Regex.IsMatch(fName.Value, "^" + Regex.Escape(Glpi.PcPrefix(code)) + @"\d{2}$")) { CyberForm.Warn(this, "Nom attendu : " + Glpi.PcPrefix(code) + "NN"); return; }
            if (fBl.Value == "Oui" && !Vault.Unlocked) { CyberForm.Warn(this, "Déverrouille le coffre avant : la clé BitLocker y sera séquestrée."); return; }
            if (fTr.Value == "Oui") try { Trmm.CheckDeployUrl(fDep.Value); } catch (Exception ex) { CyberForm.Warn(this, ex.Message); return; }
            Prefs.Ini.Set("GlpiAgentMsi", fMsi.Value); Prefs.Ini.Set("GlpiAgentUser", fUser.Value); Prefs.Save();
            Secrets.Set("GlpiAgentPass", fPass.Value); if (fDep.Value.Length > 0) Secrets.Set("TrmmDeploy." + c.Id, fDep.Value);
            Jobs.Run("Enrôlement " + fName.Value, "servicing", j =>
            {
                if (fGlpi.Value == "Oui") { var msi = fMsi.Value.Length > 0 ? fMsi.Value : Enrol.DownloadAgent(j.Log); j.Check(); Enrol.InstallAgent(msi, code, fUser.Value, fPass.Value, j.Log); }
                j.Check(); if (fTr.Value == "Oui") Enrol.InstallTrmm(fDep.Value, j.Log);
                j.Check(); if (fBl.Value == "Oui") { Enrol.BitLocker(j.Log); var id = Recette.Escrow(c); j.Log("Clé de récupération séquestrée dans le coffre (ID " + id + ").", "ok"); }
                j.Check(); if (fRen.Value == "Oui") Enrol.Rename(fName.Value, j.Log);
                if (!c.Assets.Any(a => a.Name.Equals(fName.Value, StringComparison.OrdinalIgnoreCase))) { c.Assets.Add(new Asset { Id = Store.NewId(), Kind = "PC", Name = fName.Value, Added = DateTime.Now, Notes = "enrôlé par AMI·OPS le " + DateTime.Now.ToString("dd/MM/yyyy") }); Store.Save(); }
                j.Log("Enrôlement terminé. Redémarre le PC, puis lance la recette (l'inventaire GLPI arrive en quelques minutes).", "ok");
            });
            stat.Text = "Enrôlement lancé (suivi dans Tâches).";
        }

        // ---- D / E
        void LoadParc()
        {
            var c = Cur(); if (c == null) return;
            if (!Glpi.Configured && !Trmm.Configured) { CyberForm.Info(this, "Configure GLPI et/ou Tactical RMM dans Connecteurs."); return; }
            stat.Text = "Lecture du parc…"; stat.ForeColor = Theme.Current.Muted;
            Jobs.Run("État du parc " + c.Name, null, j => { var rows = Parc.Load(c, j.Log); lastRows = rows; j.Log(rows.Count + " poste(s), " + rows.Count(r => r.Level == 2) + " à traiter, " + rows.Count(r => r.Level == 1) + " à surveiller.", "ok"); Ui(() => ShowRows(rows)); }, Out);
        }
        void ShowRows(List<ParcRow> rows)
        {
            grid.BeginUpdate(); grid.Items.Clear(); grid.Columns.Clear();
            grid.Col("Poste", 125).Col("Vu le", 85).Col("Système", 175).Col("Disque C:", 70).Col("BitLocker", 70).Col("Antivirus", 140).Col("Constats (détail en bas)", 470);
            foreach (var r in rows)
                grid.Items.Add(new ListViewItem(new[] { r.Pc, r.LastSeen.Year > 2000 ? r.LastSeen.ToString("dd/MM HH:mm") : "—", ShortOs(r), r.FreePct >= 0 ? r.FreePct.ToString("0") + " %" : "—",
                    r.Encrypted == 1 ? "oui" : r.Encrypted == 0 ? "NON" : r.Encrypted == 2 ? "partiel" : "—", r.AvName.Length > 0 ? Regex.Replace(r.AvName, @"Endpoint Security( for Windows)?", "KES") : "—", r.Issues.Count == 0 ? "conforme" : string.Join(" ; ", r.Issues) })
                { Tag = r, ForeColor = r.Level == 0 ? Theme.Current.Ok : r.Level == 1 ? Theme.Current.Warn : Theme.Current.Bad });
            grid.EndUpdate();
            stat.Text = rows.Count + " poste(s) · " + rows.Count(r => r.Level == 0) + " conformes · " + rows.Count(r => r.Level == 1) + " à surveiller · " + rows.Count(r => r.Level == 2) + " à traiter"; stat.ForeColor = Theme.Current.Muted;
        }
        static string ShortOs(ParcRow r) { return Regex.Replace((r.OsName + " " + r.OsVersion).Trim(), @"\s*(Professionnel|Professional)", " Pro").Replace("Microsoft ", ""); }

        // détail complet de la ligne sélectionnée (colonnes trop étroites pour tout afficher)
        void ShowDetail()
        {
            var r = grid.Sel as ParcRow; var x = grid.Sel as Chk; if (r == null && x == null) return;
            box.Clear();
            if (x != null) { box.Line("// " + x.Name, "accent", true); box.Line(x.Detail, x.State == "na" ? "muted" : x.State); return; }
            box.Line("// " + r.Pc + " — " + (r.Level == 0 ? "conforme" : r.Level == 1 ? "à surveiller" : "à traiter"), "accent", true);
            box.Line("Système : " + (r.OsName + " " + r.OsVersion).Trim() + " · antivirus : " + (r.AvName.Length > 0 ? r.AvName : "—") + (r.TrmmStatus.Length > 0 ? " · Tactical RMM : " + r.TrmmStatus : "") + (r.GlpiId > 0 ? " · GLPI n°" + r.GlpiId : " · absent de GLPI"), "text");
            foreach (var i in r.Issues) box.Line("✗ " + i, r.Level == 2 ? "bad" : "warn");
        }

        void TicketRow()
        {
            var c = Cur(); var r = grid.Sel as ParcRow; if (c == null) return;
            if (r == null) { CyberForm.Info(this, "Charge l'état du parc et sélectionne un poste."); return; }
            if (!Glpi.Configured || c.GlpiEntity == 0) { CyberForm.Info(this, "GLPI non configuré ou client non lié à une entité."); return; }
            var txt = "Constats sur le poste " + r.Pc + " (" + DateTime.Now.ToString("dd/MM/yyyy") + ") :\n- " + string.Join("\n- ", r.Issues.DefaultIfEmpty("aucun"));
            Jobs.Run("Ticket GLPI " + r.Pc, null, j => { var id = Glpi.Session(t => { var tid = Glpi.NewTicket(t, c.GlpiEntity, "Poste " + r.Pc + " : " + (r.Issues.FirstOrDefault() ?? "contrôle"), Glpi.H2(txt), 1, 1); if (r.GlpiId > 0) try { Glpi.LinkComputer(t, tid, r.GlpiId); } catch { } return tid; }); j.Log("Ticket n°" + id + " : " + Glpi.TicketUrl(id), "ok"); }, Out);
        }
        void MonthlyOne()
        {
            var c = Cur(); if (c == null) return;
            var rows = lastRows; var month = DateTime.Today.AddDays(1 - DateTime.Today.Day);
            Jobs.Run("Rapport mensuel " + c.Name, null, j =>
            {
                if (rows == null || rows.Count == 0) rows = Parc.Load(c, j.Log);
                var f = Monthly.Write(c, rows, month); j.Log("Rapport : " + f, "ok");
                if (Glpi.Configured && c.GlpiEntity > 0) try { Glpi.Session(t => Glpi.Upload(t, f, c.GlpiEntity, 0)); j.Log("Joint aux documents de l'entité GLPI.", "ok"); } catch (Exception ex) { j.Log("GLPI : " + ex.Message, "warn"); }
                try { Process.Start(f); } catch { }
            }, Out);
        }
        void MonthlyAll()
        {
            var list = Store.D.Clients.Where(c => c.GlpiEntity > 0 || Glpi.CodeOf(c.Name).Length > 0).ToList();
            if (list.Count == 0 || !CyberForm.Ask(this, "Générer le rapport de conformité de " + list.Count + " client(s) ?")) return;
            var month = DateTime.Today.AddDays(1 - DateTime.Today.Day);
            Jobs.Run("Rapports mensuels (" + list.Count + " clients)", null, j =>
            {
                int n = 0;
                foreach (var c in list) { j.Check(); try { var rows = Parc.Load(c, (s, cl) => { }); if (rows.Count == 0) continue; Monthly.Write(c, rows, month); n++; j.Progress = (double)n / list.Count; j.Log("✓ " + c.Name + " : " + rows.Count + " poste(s)", "ok"); } catch (Exception ex) { j.Log("✗ " + c.Name + " : " + ex.Message, "bad"); } }
                j.Log(n + " rapport(s) dans " + Path.Combine(Report.Dir, "Conformité"), "ok");
            }, Out);
        }

        // ---- G
        void Tickets()
        {
            var c = Cur(); if (c == null) return;
            if (!Glpi.Configured || c.GlpiEntity == 0) { CyberForm.Info(this, "GLPI non configuré ou client non lié à une entité."); return; }
            List<Dictionary<string, object>> l;
            try { l = Glpi.Session(t => Glpi.OpenTickets(t, c.GlpiEntity)); } catch (Exception ex) { CyberForm.Warn(this, ex.Message); return; }
            using (var f = new TicketsForm(c, l)) f.ShowDialog(this);
        }

        // ---- F
        void NasToGlpi()
        {
            if (!Glpi.Configured) { CyberForm.Info(this, "Configure GLPI dans Connecteurs."); return; }
            var c = Cur(); var all = c == null || !CyberForm.Ask(this, "Contrôler uniquement les NAS de « " + c.Name + " » ?\n(Non = tous les NAS de NasConnect)");
            Jobs.Run("NAS → GLPI", "net", j =>
            {
                var nas = NasConnect.Store.LoadNas();
                foreach (var n in nas)
                {
                    j.Check();
                    var owner = Store.D.Clients.FirstOrDefault(x => x.GlpiEntity > 0 && (Store.Norm(x.Name) == Store.Norm(n.Client) || (Glpi.CodeOf(n.Client).Length > 0 && Glpi.CodeOf(x.Name) == Glpi.CodeOf(n.Client))));
                    if (!all && owner != c) continue;
                    if (owner == null) { j.Log("· " + n.Name + " : client « " + n.Client + " » non lié à GLPI, ignoré", "muted"); continue; }
                    var rep = NasConnect.Health.Check(n);
                    if (rep.Worst < NasConnect.Lvl.Warn) { j.Log("✓ " + n.Name + " : sain", "ok"); continue; }
                    var title = "NAS « " + n.Name + " » : " + (rep.Worst == NasConnect.Lvl.Bad ? "CRITIQUE" : "alerte");
                    var id = Glpi.Session(t =>
                    {
                        var open = Glpi.OpenTickets(t, owner.GlpiEntity).FirstOrDefault(x => Http.S(x, "1").StartsWith("NAS « " + n.Name + " »"));
                        var body = "<pre>" + Fmt.Html(rep.Text()) + "</pre>";
                        if (open != null) { int oid; int.TryParse(Http.S(open, "2"), out oid); Glpi.Followup(t, oid, body); return oid; }
                        return Glpi.NewTicket(t, owner.GlpiEntity, title, body, 1, 1);
                    });
                    j.Log("✗ " + title + " → ticket n°" + id, rep.Worst == NasConnect.Lvl.Bad ? "bad" : "warn");
                }
            }, Out);
        }
    }

    // ===================================================================== G. Tickets GLPI d'un client (suivi, tâche + temps, résolution)
    class TicketsForm : CyberForm
    {
        readonly Grid grid;
        public TicketsForm(Client cl, List<Dictionary<string, object>> l) : base("TICKETS GLPI", 900, 520)
        {
            SetSubtitle(cl.Name + "  //  " + l.Count + " ticket(s) ouvert(s)  //  by alex.b"); StartPosition = FormStartPosition.CenterParent;
            grid = new Grid { Dock = DockStyle.Fill }; grid.Col("N°", 70).Col("Titre", 470).Col("Statut", 150).Col("Ouvert le", 130);
            foreach (var d in l) { int st; int.TryParse(Http.S(d, "12"), out st); grid.Items.Add(new ListViewItem(new[] { Http.S(d, "2"), Http.S(d, "1"), st > 0 && st < Glpi.Statuts.Length ? Glpi.Statuts[st] : Http.S(d, "12"), Http.S(d, "15") }) { Tag = d }); }
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Theme.S(46), Padding = new Padding(Theme.S(8)) };
            Action<string, Action<int>> b = (t, a) => { var ch = new Chip(t) { AccentOf = th => th.Accent }; ch.Click += (s, e) => { var d = grid.Sel as Dictionary<string, object>; int id; if (d == null || !int.TryParse(Http.S(d, "2"), out id)) { Info(this, "Sélectionne un ticket."); return; } try { a(id); } catch (Exception ex) { Warn(this, ex.Message); } }; bar.Controls.Add(ch); };
            b("Ajouter un suivi…", id => { var s = Dlg.Ask(this, "Suivi du ticket " + id, "Texte (visible du demandeur)", "", false); if (!string.IsNullOrWhiteSpace(s)) { Glpi.Session(t => { Glpi.Followup(t, id, Glpi.H2(s)); return 0; }); Info(this, "Suivi ajouté."); } });
            b("Tâche + temps passé…", id => { var fT = new F("Travail réalisé", "") { Multi = true, Required = true }; var fM = new F("Durée (minutes)", "30") { Required = true }; int m; if (Dlg.Edit(this, "Tâche sur le ticket " + id, fT, fM) && int.TryParse(fM.Value, out m) && m >= 0) { Glpi.Session(t => { Glpi.Task(t, id, Glpi.H2(fT.Value), m); return 0; }); Info(this, "Tâche ajoutée (" + m + " min)."); } });
            b("Résoudre…", id => { var s = Dlg.Ask(this, "Résoudre le ticket " + id, "Solution", "", false); if (!string.IsNullOrWhiteSpace(s) && Ask(this, "Résoudre le ticket " + id + " ?")) { Glpi.Session(t => { Glpi.Solve(t, id, Glpi.H2(s)); return 0; }); Info(this, "Ticket résolu."); } });
            b("Ouvrir dans GLPI", id => Process.Start(Glpi.TicketUrl(id)));
            Body.Controls.Add(grid); Body.Controls.Add(bar);
        }
    }
}
