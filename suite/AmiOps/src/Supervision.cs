// Tech IT AB / PC Santé AB - supervision opt-in, modèle « agent GLPI » :
//   PC Santé AB enrôlé (code client) envoie un état technique à l'endpoint d'inventaire GLPI d'alex.b,
//   SANS aucun jeton d'admin (comme un agent GLPI). Le poste client affiche en permanence qu'il est supervisé.
//   Tech IT AB (avec le jeton, uniquement sur la machine d'alex.b) relit les postes supervisés dans « Parc client ».
// Développé by alex.b
//
// RGPD : la supervision est opt-in (enrôlement par code client) et VISIBLE du client (bandeau permanent).
// Aucune collecte cachée : sans enrôlement, PC Santé AB ne contacte personne.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AlexB;

namespace AmiOps
{
    // ===================================================================== HTTP JSON (TLS vérifié, jamais contourné) — partagé par les deux éditions
    static class Http
    {
        public static object Call(string method, string url, Dictionary<string, string> headers, object body, int timeoutMs = 30000)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method; req.Timeout = timeoutMs; req.UserAgent = Ed.Id + "/" + AppInfo.Version; req.Accept = "application/json";
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

    static class Superv
    {
        public const string MarkerName = "État PC Santé AB";   // logiciel « marqueur » qui porte la dernière ligne d'état (lisible par Tech IT AB)

        public static string Url { get { return Prefs.Ini.Get("SupervUrl", "").Trim(); } }
        public static string Code { get { return Prefs.Ini.Get("SupervCode", "").Trim().ToUpperInvariant(); } }
        public static bool On { get { return Prefs.Ini.GetBool("SupervOn", false); } }
        public static bool Consented { get { return Prefs.Ini.GetBool("SupervConsent", false); } }

        public static bool CodeOk(string code) { return Regex.IsMatch(code ?? "", @"^CLI-\d{2}-\d{2}-\d{4}$"); }
        public static bool UrlOk(string u) { return Regex.IsMatch(u ?? "", @"^https://[A-Za-z0-9.\-]+(:\d+)?/[\w./\-]*$", RegexOptions.IgnoreCase); }

        // enrôlé = configuré ET activé ; la supervision ne s'exécute qu'en plus si l'exe est authentique (anti-copie)
        public static bool Enrolled { get { return On && UrlOk(Url) && CodeOk(Code); } }
        public static bool Active { get { return Enrolled && Authenticity.Trusted; } }

        public static string Banner
        {
            get { return Enrolled ? "ⓘ  Ce poste est supervisé par alex.b (maintenance) — code client " + Code + ". Un état technique est envoyé à votre prestataire." : null; }
        }

        public static string ConsentText
        {
            get
            {
                return "Ce poste va être supervisé par votre prestataire alex.b (maintenance à distance), pour le code client " + Code + ".\n\n" +
                       "Données envoyées à chaque état : nom du PC, numéro de série, modèle, version de Windows, espace disque, antivirus et un bilan technique.\n" +
                       "AUCUN contenu personnel (documents, mots de passe, navigation) n'est envoyé.\n\n" +
                       "L'envoi se fait vers le serveur GLPI de votre prestataire. Vous pouvez le désactiver à tout moment dans Paramètres.\n\n" +
                       "Acceptez-vous la supervision de ce poste ?";
            }
        }

        public static void SetEnrolment(string url, string code, bool on)
        {
            Prefs.Ini.Set("SupervUrl", (url ?? "").Trim()); Prefs.Ini.Set("SupervCode", (code ?? "").Trim().ToUpperInvariant());
            Prefs.Ini.Set("SupervOn", on); Prefs.Save();
        }
        public static void SetConsent(bool ok) { Prefs.Ini.Set("SupervConsent", ok); Prefs.Save(); }

        // ------------------------------------------------------------------ état technique du PC (lecture WMI, rapide)
        static object W(string q, string prop) { try { using (var s = new ManagementObjectSearcher(q)) foreach (ManagementObject o in s.Get()) return o[prop]; } catch { } return null; }
        static string Ws(string q, string prop) { return Convert.ToString(W(q, prop) ?? "").Trim(); }

        public class Health { public string Name = "", Serial = "", Maker = "", Model = "", Uuid = "", User = "", Domain = "", Os = "", OsVersion = "", Cpu = ""; public int Cores, RamMb; public long DiskTotalMb, DiskFreeMb; public string Av = "", AvState = ""; public bool AvEnabled, AvFresh; public DateTime Boot, Install; public int UptimeDays; public string Summary = ""; }

        public static Health Snapshot()
        {
            var h = new Health { Name = Environment.MachineName, User = Environment.UserName };
            h.Serial = Ws("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber");
            h.Maker = Ws("SELECT Manufacturer FROM Win32_ComputerSystem", "Manufacturer");
            h.Model = Ws("SELECT Model FROM Win32_ComputerSystem", "Model");
            h.Domain = Ws("SELECT Domain FROM Win32_ComputerSystem", "Domain");
            h.Uuid = Ws("SELECT UUID FROM Win32_ComputerSystemProduct", "UUID");
            h.Os = Ws("SELECT Caption FROM Win32_OperatingSystem", "Caption");
            h.OsVersion = Ws("SELECT Version FROM Win32_OperatingSystem", "Version");
            h.Cpu = Regex.Replace(Ws("SELECT Name FROM Win32_Processor", "Name"), @"\s+", " ");
            try { h.Cores = Convert.ToInt32(W("SELECT NumberOfLogicalProcessors FROM Win32_ComputerSystem", "NumberOfLogicalProcessors") ?? 0); } catch { }
            try { h.RamMb = (int)(Convert.ToUInt64(W("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", "TotalPhysicalMemory") ?? 0UL) / 1048576); } catch { }
            try { var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)); h.DiskTotalMb = d.TotalSize / 1048576; h.DiskFreeMb = d.AvailableFreeSpace / 1048576; } catch { }
            try { var b = W("SELECT LastBootUpTime FROM Win32_OperatingSystem", "LastBootUpTime"); if (b != null) { h.Boot = ManagementDateTimeConverter.ToDateTime(Convert.ToString(b)); h.UptimeDays = (int)(DateTime.Now - h.Boot).TotalDays; } } catch { }
            try { var i = W("SELECT InstallDate FROM Win32_OperatingSystem", "InstallDate"); if (i != null) h.Install = ManagementDateTimeConverter.ToDateTime(Convert.ToString(i)); } catch { }
            // antivirus : SecurityCenter2 (Defender ou tiers)
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var st = Convert.ToInt32(o["productState"]);
                        h.Av = Convert.ToString(o["displayName"]); h.AvEnabled = (st & 0x1000) != 0; h.AvFresh = (st & 0x10) == 0;
                        h.AvState = h.AvEnabled ? (h.AvFresh ? "actif, à jour" : "actif, signatures anciennes") : "DÉSACTIVÉ";
                        break;
                    }
            }
            catch { }
            int freePct = h.DiskTotalMb > 0 ? (int)(h.DiskFreeMb * 100 / h.DiskTotalMb) : -1;
            h.Summary = "disque " + (freePct >= 0 ? freePct + " % libre" : "?") + " · démarré depuis " + h.UptimeDays + " j · antivirus " + (h.Av.Length > 0 ? h.Av + " (" + h.AvState + ")" : "?");
            return h;
        }

        // inventaire GLPI natif (format GLPI 10), étiquette = code client, AUCUN jeton. Résumé porté par un logiciel « marqueur ».
        public static object Inventory(Health h, string extra)
        {
            var line = DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " — " + h.Summary + (string.IsNullOrEmpty(extra) ? "" : " · " + extra);
            var content = new Dictionary<string, object>
            {
                { "versionclient", Ed.Name + " v" + Ed.Version },
                { "accountinfo", new[] { new Dictionary<string, object> { { "keyname", "TAG" }, { "keyvalue", Code } } } },
                { "hardware", new Dictionary<string, object> { { "name", h.Name }, { "uuid", h.Uuid }, { "lastloggeduser", h.User }, { "workgroup", h.Domain } } },
                { "bios", new Dictionary<string, object> { { "ssn", h.Serial }, { "smanufacturer", h.Maker }, { "smodel", h.Model } } },
                { "operatingsystem", new Dictionary<string, object> { { "full_name", h.Os }, { "version", h.OsVersion }, { "kernel_name", "mswin32" }, { "boot_time", h.Boot == default(DateTime) ? "" : h.Boot.ToString("yyyy-MM-dd HH:mm:ss") }, { "install_date", h.Install == default(DateTime) ? "" : h.Install.ToString("yyyy-MM-dd HH:mm:ss") } } },
                { "cpus", new[] { new Dictionary<string, object> { { "name", h.Cpu }, { "core", h.Cores } } } },
                { "memories", new[] { new Dictionary<string, object> { { "capacity", h.RamMb } } } },
                { "drives", new[] { new Dictionary<string, object> { { "letter", "C:" }, { "filesystem", "NTFS" }, { "total", h.DiskTotalMb }, { "free", h.DiskFreeMb } } } },
                { "softwares", new[] { new Dictionary<string, object> { { "name", MarkerName }, { "version", line }, { "publisher", "alex.b" }, { "comments", line } } } }
            };
            if (h.Av.Length > 0) content["antivirus"] = new[] { new Dictionary<string, object> { { "name", h.Av }, { "enabled", h.AvEnabled }, { "uptodate", h.AvFresh } } };
            return new Dictionary<string, object>
            {
                { "action", "inventory" }, { "itemtype", "Computer" }, { "tag", Code },
                { "deviceid", h.Name + "-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss") }, { "content", content }
            };
        }

        public static DateTime LastSent { get { DateTime d; return DateTime.TryParse(Prefs.Ini.Get("SupervLast", ""), null, DateTimeStyles.RoundtripKind, out d) ? d : DateTime.MinValue; } }

        // envoi (tokenless). Refusé si l'exe n'est pas authentique (une copie trafiquée ne remonte pas vers le GLPI d'alex.b).
        public static void Send(string extra, Action<string, string> log)
        {
            if (!Enrolled) { log("Supervision non configurée (code client + serveur dans Paramètres).", "warn"); return; }
            if (!Authenticity.Trusted) { log("Supervision désactivée : cette copie n'est pas la version officielle signée par alex.b.", "bad"); return; }
            if (!Consented) { log("Supervision en attente d'acceptation par l'utilisateur.", "warn"); return; }
            if (!Url.StartsWith("https://")) { log("Serveur d'inventaire en https obligatoire.", "bad"); return; }
            var h = Snapshot();
            var r = Http.Call("POST", Url, null, Inventory(h, extra), 30000);
            var resp = Convert.ToString(r is Dictionary<string, object> ? Store.Json().Serialize(r) : r);
            if (resp != null && resp.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 && resp.IndexOf("\"ok\"", StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("réponse GLPI : " + (resp.Length > 200 ? resp.Substring(0, 200) : resp));
            Prefs.Ini.Set("SupervLast", DateTime.Now.ToString("o")); Prefs.Save();
            log("État envoyé à votre prestataire (" + h.Name + " · " + h.Summary + ").", "ok");
        }

        // battement quotidien au démarrage : un état par jour, en silence
        public static void Heartbeat()
        {
            if (!Active || !Consented) return;
            if ((DateTime.Now - LastSent).TotalHours < 20) return;
            try { Send("démarrage", (s, c) => Log.Write("Supervision : " + s)); } catch (Exception ex) { Log.Write("Supervision (battement) : " + ex.Message); }
        }

        // ------------------------------------------------------------------ Tech IT AB : relecture des postes supervisés (jeton d'alex.b, sa machine uniquement)
        public class Row { public string Pc = "", Serial = "", Os = "", Etat = ""; public DateTime Last; }

#if !POSTE
        public static List<Row> Read(int entityId)
        {
            return Glpi.Session(t =>
            {
                var rows = new List<Row>();
                var comps = Http.List(Http.Call("GET", Glpi.Url + "/Computer?range=0-999&expand_dropdowns=false", Glpi.Headers(t), null)).ToList();
                foreach (var pc in comps.Where(pc => { int e; return int.TryParse(Http.S(pc, "entities_id"), out e) && e == entityId; }))
                {
                    int id; int.TryParse(Http.S(pc, "id"), out id); if (id == 0) continue;
                    var r = new Row { Pc = Http.S(pc, "name"), Serial = Http.S(pc, "serial") };
                    DateTime dt; if (DateTime.TryParse(Http.S(pc, "last_inventory_update"), out dt)) r.Last = dt;
                    try
                    {
                        var full = Http.Call("GET", Glpi.Url + "/Computer/" + id + "?with_softwares=true&expand_dropdowns=true", Glpi.Headers(t), null) as Dictionary<string, object>;
                        object os; if (full != null && full.TryGetValue("operatingsystems_id", out os)) r.Os = Convert.ToString(os);
                        object sw; if (full != null && full.TryGetValue("_softwares", out sw))
                            foreach (var s in Http.List(sw)) if (Http.S(s, "softwares_name") == MarkerName || Http.S(s, "name") == MarkerName) { r.Etat = Http.S(s, "version"); break; }
                    }
                    catch { }
                    if (r.Etat.Length > 0 || r.Pc.Length > 0) rows.Add(r);
                }
                return rows.Where(r => r.Etat.Length > 0).OrderByDescending(r => r.Last).ToList();
            });
        }
#endif
    }
}
