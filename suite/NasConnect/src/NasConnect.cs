// NasConnect - lecteurs réseau NAS en un clic (local ou à distance via Tailscale / VPN),
// santé du NAS Synology (disques, RAID, volumes), alertes et checkup hebdomadaire.
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

[assembly: AssemblyTitle("NasConnect")]
[assembly: AssemblyDescription("Lecteurs réseau NAS et santé du NAS")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("NasConnect")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace NasConnect
{
    static class Program
    {
        public const string ShowEvent = "Local\\NasConnect_show_alexb";

        [STAThread]
        static int Main(string[] args)
        {
            AppInfo.Name = "NasConnect"; AppInfo.Id = "NasConnect"; AppInfo.Version = "1.3.0"; AppInfo.MachineWide = false;
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; } catch { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            Dsm.InstallCertPolicy();
            string a = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            Hardening.Apply();
            Elevation.DpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            CyberForm.LoadTheme();

            // nettoyage appelé par le désinstallateur
            if (a == "--uninstall-cleanup") { try { Checkup.Schedule(false); } catch { } TrayApp.AutoStart = false; return 0; }

            // checkup hebdomadaire lancé par le Planificateur de tâches
            if (a == "--checkup")
            {
                bool c;
                using (var m = new Mutex(true, "Local\\NasConnect_checkup_alexb", out c))
                {
                    if (!c) return 0;
                    Checkup.RunScheduled();
                    GC.KeepAlive(m);
                }
                return 0;
            }

            bool created;
            using (var mutex = new Mutex(true, "Local\\NasConnect_alexb", out created))
            {
                if (!created)
                {
                    try { using (var ev = EventWaitHandle.OpenExisting(ShowEvent)) ev.Set(); } catch { }
                    return 0;
                }
                Application.ThreadException += (s, e) => { Log.Write("ERREUR UI : " + e.Exception); };
                Log.Write("Démarrage NasConnect " + AppInfo.Version);
                Application.Run(new TrayApp(a == "--tray"));
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }

    enum Lvl { Ok, Info, Warn, Bad }

    // ===================================================================== Modèles
    class Share
    {
        public string Name = "", Unc = "", Letter = "Z", User = "", Remote = "";
        public bool Persistent = true, SaveCred = true;
        public string Host { get { var m = Regex.Match(Unc, @"^\\\\([^\\]+)"); return m.Success ? m.Groups[1].Value : ""; } }
        public string Drive { get { return Letter.TrimEnd(':').ToUpperInvariant() + ":"; } }
        public string UncFor(string host) { return Regex.Replace(Unc, @"^\\\\[^\\]+", "\\\\" + host); }
        public bool Matches(string remote)
        {
            if (remote == null) return false;
            var r = remote.TrimEnd('\\');
            return r.Equals(Unc.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                   (Remote.Length > 0 && r.Equals(UncFor(Remote).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
    }

    class NasDevice
    {
        public string Client = "", Name = "", QcId = "", Host = "", Remote = "", User = "";
        public int Port = 5001;
        public string CredKey { get { return "NasConnect/DSM/" + Name; } }
        public const string DefaultKey = "NasConnect/DSM/__defaut__";
        public string DsmUrl { get { return QcId.Length > 0 ? "https://" + QcId + ".quickconnect.to" : "https://" + Host + ":" + Port; } }

        // identifiants : ceux du NAS, sinon les identifiants par défaut
        public bool GetCred(out string user, out string pass)
        {
            pass = Net.ReadPassword(CredKey, 1); user = User;
            if (pass != null && user.Length > 0) return true;
            pass = Net.ReadPassword(DefaultKey, 1);
            user = Net.ReadUser(DefaultKey, 1);
            if (User.Length > 0) user = User;
            return pass != null && !string.IsNullOrEmpty(user);
        }
    }

    // ===================================================================== Windows : lecteurs réseau + identifiants
    static class Net
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class NETRESOURCE { public int dwScope = 0, dwType = 1, dwDisplayType = 0, dwUsage = 0; public string lpLocalName, lpRemoteName, lpComment, lpProvider; }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetAddConnection2(NETRESOURCE nr, string password, string user, int flags);
        [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetCancelConnection2(string name, int flags, bool force);
        [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetGetConnection(string local, StringBuilder remote, ref int len);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CREDENTIAL
        {
            public int Flags, Type; public string TargetName, Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist, AttributeCount;
            public IntPtr Attributes; public string TargetAlias, UserName;
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWrite(ref CREDENTIAL c, int flags);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDelete(string target, int type, int flags);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredRead(string target, int type, int flags, out IntPtr cred);
        [DllImport("advapi32.dll")] static extern void CredFree(IntPtr p);

        public static string Remote(string drive)
        {
            var sb = new StringBuilder(512); int len = sb.Capacity;
            return WNetGetConnection(drive, sb, ref len) == 0 ? sb.ToString() : null;
        }

        public static int Connect(string drive, string unc, string user, string password, bool persistent)
        {
            var nr = new NETRESOURCE { lpLocalName = drive, lpRemoteName = unc.TrimEnd('\\') };
            int rc = WNetAddConnection2(nr, string.IsNullOrEmpty(password) ? null : password, string.IsNullOrWhiteSpace(user) ? null : user.Trim(), persistent ? 0x1 : 0x4);
            Log.Write("Connexion " + drive + " " + unc + " => " + rc);
            return rc;
        }

        public static int Disconnect(string drive, bool force)
        {
            int rc = WNetCancelConnection2(drive, 0x1, force);
            Log.Write("Déconnexion " + drive + " => " + rc);
            return rc;
        }

        // type 2 = identifiant réseau Windows (utilisé par SMB), type 1 = générique (compte DSM)
        public static bool SaveCred(string target, string user, string password, int type)
        {
            var blob = Marshal.StringToCoTaskMemUni(password);
            try
            {
                var c = new CREDENTIAL { Type = type, TargetName = target, UserName = user, Persist = 2, CredentialBlob = blob, CredentialBlobSize = password.Length * 2, Comment = "NasConnect (by alex.b)" };
                bool ok = CredWrite(ref c, 0);
                Log.Write("Identifiants mémorisés pour " + target + " => " + ok);
                return ok;
            }
            finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
        }

        public static string ReadPassword(string target, int type)
        {
            IntPtr p;
            if (!CredRead(target, type, 0, out p)) return null;
            try
            {
                var c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
                return c.CredentialBlobSize > 0 ? Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2) : "";
            }
            finally { CredFree(p); }
        }

        public static string ReadUser(string target, int type)
        {
            IntPtr p;
            if (!CredRead(target, type, 0, out p)) return null;
            try { return ((CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL))).UserName; }
            finally { CredFree(p); }
        }

        public static bool HasCred(string target, int type) { IntPtr p; if (!CredRead(target, type, 0, out p)) return false; CredFree(p); return true; }
        public static void DeleteCred(string target, int type) { CredDelete(target, type, 0); Log.Write("Identifiants supprimés : " + target); }

        public static string Error(int rc)
        {
            switch (rc)
            {
                case 0: return "connecté";
                case 5: return "accès refusé : ce compte n'a pas les droits sur ce partage";
                case 53: case 1203: return "chemin introuvable : NAS éteint, adresse fausse, ou hors réseau local sans Tailscale/VPN";
                case 67: return "nom de partage introuvable : vérifier le dossier partagé sur le NAS";
                case 85: return "lettre déjà utilisée par un autre lecteur";
                case 86: case 1326: return "identifiant ou mot de passe incorrect";
                case 1219: return "déjà connecté à ce serveur avec un AUTRE compte";
                case 1222: return "réseau absent";
                case 1244: return "authentification requise";
                case 1272: return "accès invité refusé par Windows : crée un compte sur le NAS et renseigne-le";
                case 2202: return "nom d'utilisateur invalide";
                case 2401: return "des fichiers sont ouverts sur ce lecteur";
                case 2250: return "ce lecteur n'est pas connecté";
                default: return "erreur Windows " + rc;
            }
        }

        public static bool Port(string host, int port, int timeout = 1500)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeout)) return false;
                    c.EndConnect(ar); return true;
                }
            }
            catch { return false; }
        }
    }

    // ===================================================================== Stockage (aucun mot de passe dans les fichiers)
    // dossier des données : celui de NasConnect, même quand le module tourne dans AMI·OPS
    static class NasPaths { public static string Dir; public static string D { get { return Dir ?? AppInfo.DataDir; } } }

    static class Store
    {
        static string SharesFile { get { return Path.Combine(NasPaths.D, "partages.txt"); } }
        static string NasFile { get { return Path.Combine(NasPaths.D, "nas.txt"); } }
        public static string HistoryFile { get { return Path.Combine(NasPaths.D, "historique-checkup.txt"); } }
        public static string LastReportFile { get { return Path.Combine(NasPaths.D, "dernier-checkup.txt"); } }

        static IEnumerable<string[]> Lines(string f)
        {
            if (!File.Exists(f)) yield break;
            foreach (var raw in File.ReadAllLines(f, Encoding.UTF8))
                if (raw.Trim().Length > 0 && !raw.StartsWith("#")) yield return raw.Split('|');
        }
        static string Clean(string s) { return (s ?? "").Replace("|", " ").Replace("\r", "").Replace("\n", ""); }

        public static List<Share> LoadShares()
        {
            var l = new List<Share>();
            foreach (var p in Lines(SharesFile))
                if (p.Length >= 6)
                    l.Add(new Share { Name = p[0], Unc = p[1], Letter = p[2], User = p[3], Persistent = p[4] == "1", SaveCred = p[5] == "1", Remote = p.Length > 6 ? p[6] : "" });
            return l;
        }

        public static void SaveShares(List<Share> list)
        {
            var sb = new StringBuilder("# NasConnect (by alex.b) - Nom|UNC|Lettre|Utilisateur|Reconnexion|Mémoriser|Adresse distante (Tailscale/VPN) — aucun mot de passe ici\r\n");
            foreach (var s in list) sb.AppendLine(string.Join("|", Clean(s.Name), Clean(s.Unc), Clean(s.Letter), Clean(s.User), s.Persistent ? "1" : "0", s.SaveCred ? "1" : "0", Clean(s.Remote)));
            File.WriteAllText(SharesFile, sb.ToString(), new UTF8Encoding(false));
        }

        public static List<NasDevice> LoadNas()
        {
            var l = new List<NasDevice>();
            foreach (var p in Lines(NasFile))
            {
                int port;
                if (p.Length >= 7)        // 1.2 : Client|Nom|QuickConnect ID|Adresse locale|Adresse distante|Port|Compte
                {
                    int.TryParse(p[5], out port);
                    l.Add(new NasDevice { Client = p[0], Name = p[1], QcId = p[2], Host = p[3], Remote = p[4], Port = port > 0 ? port : 5001, User = p[6] });
                }
                else if (p.Length >= 6)   // 1.1 : Nom|Adresse locale|Adresse distante|QuickConnect ID|Port|Compte
                {
                    int.TryParse(p[4], out port);
                    l.Add(new NasDevice { Name = p[0], Host = p[1], Remote = p[2], QcId = p[3], Port = port > 0 ? port : 5001, User = p[5] });
                }
            }
            return l;
        }

        public static void SaveNas(List<NasDevice> list)
        {
            var sb = new StringBuilder("# NasConnect (by alex.b) - Client|Nom|QuickConnect ID|Adresse locale|Adresse distante|Port DSM HTTPS|Compte DSM — mots de passe dans le Gestionnaire d'identification Windows\r\n");
            foreach (var n in list.OrderBy(x => x.Client).ThenBy(x => x.Name))
                sb.AppendLine(string.Join("|", Clean(n.Client), Clean(n.Name), Clean(n.QcId), Clean(n.Host), Clean(n.Remote), n.Port, Clean(n.User)));
            File.WriteAllText(NasFile, sb.ToString(), new UTF8Encoding(false));
        }
    }

    // ===================================================================== API DSM (Synology)
    class DsmException : Exception
    {
        public int Code;
        public DsmException(int code, string msg) : base(msg) { Code = code; }
    }

    class Dsm
    {
        static readonly HashSet<string> trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly string baseUrl;
        string sid;

        // DSM utilise souvent un certificat auto-signé : accepté UNIQUEMENT pour les adresses de NAS configurées
        static string PinFile { get { return Path.Combine(NasPaths.D, "certificats-nas.txt"); } }
        static readonly object pinLock = new object();
        public static string LastCertError;

        // Certificat valide (QuickConnect, Let's Encrypt) : accepté normalement.
        // Certificat auto-signé d'un NAS configuré : mémorisé à la 1re connexion, refusé s'il change ensuite (attaque possible).
        public static void InstallCertPolicy()
        {
            ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, err) =>
            {
                if (err == System.Net.Security.SslPolicyErrors.None) return true;
                var req = sender as HttpWebRequest;
                if (req == null || cert == null) return false;
                string host = req.Address.Host;
                bool isTrusted; lock (trusted) isTrusted = trusted.Contains(host);
                if (!isTrusted) return false;
                string thumb = cert.GetCertHashString();
                lock (pinLock)
                {
                    var pins = LoadPins();
                    string known;
                    if (!pins.TryGetValue(host, out known))
                    {
                        pins[host] = thumb; SavePins(pins);
                        Log.Write("Certificat mémorisé pour " + host + " : " + thumb);
                        return true;
                    }
                    if (known.Equals(thumb, StringComparison.OrdinalIgnoreCase)) return true;
                    LastCertError = "certificat de " + host + " DIFFÉRENT de celui mémorisé : connexion refusée (renouvellement ? sinon attaque possible). « Modifier » le NAS puis « Enregistrer » pour accepter le nouveau.";
                    Log.Write("ALERTE certificat changé pour " + host + " : attendu " + known + ", reçu " + thumb);
                    return false;
                }
            };
        }

        static Dictionary<string, string> LoadPins()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(PinFile)) foreach (var l in File.ReadAllLines(PinFile)) { var p = l.Split('='); if (p.Length == 2) d[p[0].Trim()] = p[1].Trim(); } } catch { }
            return d;
        }
        static void SavePins(Dictionary<string, string> d)
        {
            try { File.WriteAllLines(PinFile, d.Select(kv => kv.Key + "=" + kv.Value)); } catch { }
        }
        public static void ForgetPins(params string[] hosts)
        {
            lock (pinLock) { var d = LoadPins(); foreach (var h in hosts) if (!string.IsNullOrEmpty(h)) d.Remove(h); SavePins(d); }
        }

        public static void Trust(string host) { lock (trusted) trusted.Add(host); }

        public Dsm(string baseUrl)
        {
            Trust(new Uri(baseUrl).Host);
            this.baseUrl = baseUrl.TrimEnd('/') + "/webapi/entry.cgi";
        }

        Dictionary<string, object> Call(params string[] kv)
        {
            var body = new StringBuilder();
            for (int i = 0; i + 1 < kv.Length; i += 2) body.Append((i > 0 ? "&" : "") + kv[i] + "=" + Uri.EscapeDataString(kv[i + 1]));
            if (sid != null) body.Append("&_sid=" + Uri.EscapeDataString(sid));
            var req = (HttpWebRequest)WebRequest.Create(baseUrl);
            req.Method = "POST"; req.ContentType = "application/x-www-form-urlencoded"; req.Timeout = 20000; req.ReadWriteTimeout = 20000;
            req.UserAgent = "NasConnect/" + AppInfo.Version + " (by alex.b)";
            var bytes = Encoding.UTF8.GetBytes(body.ToString());
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            string json;
            using (var resp = (HttpWebResponse)req.GetResponse()) using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = r.ReadToEnd();
            var obj = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>;
            if (obj == null) throw new DsmException(-1, "réponse DSM illisible");
            object ok; obj.TryGetValue("success", out ok);
            if (!(ok is bool) || !(bool)ok)
            {
                int code = -1;
                object er; if (obj.TryGetValue("error", out er) && er is Dictionary<string, object>) { object c; if (((Dictionary<string, object>)er).TryGetValue("code", out c)) code = Convert.ToInt32(c); }
                throw new DsmException(code, AuthError(code));
            }
            object data; obj.TryGetValue("data", out data);
            LastRaw = json;
            return data as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        public string LastRaw;

        static string AuthError(int code)
        {
            switch (code)
            {
                case 400: return "compte ou mot de passe DSM incorrect";
                case 401: return "compte DSM désactivé";
                case 402: return "permission refusée (le compte doit être administrateur pour lire le stockage)";
                case 403: return "vérification en 2 étapes demandée : utiliser un compte DSM dédié sans 2FA";
                case 404: return "code 2FA refusé";
                case 407: return "adresse IP bloquée par le NAS (blocage automatique DSM)";
                case 105: return "permission refusée sur cette API (compte non administrateur ?)";
                case 119: return "session DSM expirée";
                default: return "erreur DSM " + code;
            }
        }

        public void Login(string user, string pass)
        {
            var d = Call("api", "SYNO.API.Auth", "version", "6", "method", "login", "account", user, "passwd", pass, "session", "NasConnect", "format", "sid");
            object s; d.TryGetValue("sid", out s);
            sid = s as string;
            if (sid == null) throw new DsmException(-1, "connexion DSM sans jeton de session");
        }

        public void Logout() { try { Call("api", "SYNO.API.Auth", "version", "6", "method", "logout", "session", "NasConnect"); } catch { } }

        public Dictionary<string, object> SystemInfo() { try { return Call("api", "SYNO.Core.System", "version", "1", "method", "info"); } catch { return new Dictionary<string, object>(); } }
        public Dictionary<string, object> Storage() { return Call("api", "SYNO.Storage.CGI.Storage", "version", "1", "method", "load_info"); }
    }

    // ===================================================================== Bilan de santé
    class HealthItem
    {
        public string Kind, Name, Detail; public Lvl Level;
        public string Title { get { return (Name ?? "").StartsWith(Kind ?? "", StringComparison.OrdinalIgnoreCase) || (Name ?? "").StartsWith("Drive", StringComparison.OrdinalIgnoreCase) && Kind == "Disque" ? Name : Kind + " " + Name; } }
    }

    class HealthReport
    {
        public string Nas, Client = "", Via = "", Error;
        public DateTime When = DateTime.Now;
        public List<HealthItem> Items = new List<HealthItem>();
        public Lvl Worst { get { return Error != null ? Lvl.Bad : Items.Count == 0 ? Lvl.Info : Items.Max(i => i.Level); } }

        public string Text()
        {
            var sb = new StringBuilder();
            sb.AppendLine((Client.Length > 0 ? Client + " / " : "") + "NAS « " + Nas + " » — " + When.ToString("dd/MM/yyyy HH:mm") + (Via.Length > 0 ? " — via " + Via : ""));
            if (Error != null) sb.AppendLine("  [!!] " + Error);
            foreach (var i in Items) sb.AppendLine("  " + (i.Level == Lvl.Bad ? "[!!]" : i.Level == Lvl.Warn ? "[! ]" : "[OK]") + " " + i.Title + " : " + i.Detail);
            return sb.ToString();
        }
    }

    // ===================================================================== Résolution QuickConnect (protocole non documenté par Synology)
    static class QuickConnect
    {
        static object Post(string url, string json, int timeout)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = timeout; req.ReadWriteTimeout = timeout;
            req.UserAgent = "NasConnect/" + AppInfo.Version + " (by alex.b)";
            var b = Encoding.UTF8.GetBytes(json);
            using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            using (var r = new StreamReader(((HttpWebResponse)req.GetResponse()).GetResponseStream(), Encoding.UTF8))
            {
                var txt = r.ReadToEnd();
                LastText = txt;
                return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(txt);
            }
        }

        [ThreadStatic] static string LastText;

        static string Req(string id, string command)
        {
            id = id.Replace("\"", "");
            return "[{\"version\":1,\"command\":\"" + command + "\",\"stop_when_error\":false,\"stop_when_success\":false,\"id\":\"dsm_portal_https\",\"serverID\":\"" + id + "\"}," +
                   "{\"version\":1,\"command\":\"" + command + "\",\"stop_when_error\":false,\"stop_when_success\":false,\"id\":\"dsm_portal\",\"serverID\":\"" + id + "\"}]";
        }

        static IEnumerable<Dictionary<string, object>> Items(object o)
        {
            if (o is Dictionary<string, object>) return new[] { (Dictionary<string, object>)o };
            if (o is object[]) return ((object[])o).OfType<Dictionary<string, object>>();
            return Enumerable.Empty<Dictionary<string, object>>();
        }
        static Dictionary<string, object> D(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null && !(v is Dictionary<string, object>) && !(v is object[]) ? Convert.ToString(v) : null; }
        static int I(Dictionary<string, object> d, string k) { int n; return int.TryParse(S(d, k), out n) ? n : 0; }
        static IEnumerable<object> A(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v is object[] ? (object[])v : new object[0]; }
        static bool Usable(string h) { return !string.IsNullOrWhiteSpace(h) && h != "NULL" && h != "0.0.0.0" && h != "::"; }

        // Renvoie les adresses DSM candidates (de la plus directe au relais Synology)
        public static List<string> Candidates(string id, StringBuilder diag)
        {
            var list = new List<string>();
            string region = null, control = null;
            try
            {
                var res = Post("https://global.quickconnect.to/Serv.php", Req(id, "get_server_info"), 15000);
                diag.AppendLine("get_server_info : " + LastText);
                foreach (var it in Items(res))
                {
                    int errno = I(it, "errno");
                    if (errno != 0) { diag.AppendLine("errno " + errno + (errno == 4 ? " (QuickConnect ID inconnu ?)" : "")); continue; }
                    var svc = D(it, "service"); var srv = D(it, "server"); var sd = D(it, "smartdns"); var env = D(it, "env");
                    bool https = S(it, "id") != "dsm_portal" ;
                    string scheme = https ? "https" : "http";
                    int port = I(svc, "port"); if (port == 0) port = https ? 5001 : 5000;
                    int ext = I(svc, "ext_port");
                    region = region ?? S(env, "relay_region"); control = control ?? S(env, "control_host");
                    if (sd != null)
                    {
                        if (Usable(S(sd, "host"))) list.Add(scheme + "://" + S(sd, "host") + ":" + port);
                        foreach (var lan in A(sd, "lan")) if (Usable(Convert.ToString(lan))) list.Add(scheme + "://" + lan + ":" + port);
                        if (Usable(S(sd, "external"))) list.Add(scheme + "://" + S(sd, "external") + ":" + (ext > 0 ? ext : port));
                    }
                    if (srv != null)
                    {
                        foreach (var i in A(srv, "interface").OfType<Dictionary<string, object>>()) if (Usable(S(i, "ip"))) list.Add(scheme + "://" + S(i, "ip") + ":" + port);
                        var ex = D(srv, "external");
                        if (Usable(S(ex, "ip"))) list.Add(scheme + "://" + S(ex, "ip") + ":" + (ext > 0 ? ext : port));
                        if (Usable(S(srv, "ddns"))) list.Add(scheme + "://" + S(srv, "ddns") + ":" + (ext > 0 ? ext : port));
                        if (Usable(S(srv, "fqdn"))) list.Add(scheme + "://" + S(srv, "fqdn") + ":" + (ext > 0 ? ext : port));
                    }
                }
            }
            catch (Exception ex) { diag.AppendLine("get_server_info : ERREUR " + ex.Message); }

            // relais Synology : demande d'ouverture du tunnel puis adresse <id>.<région>.quickconnect.to
            if (control != null)
            {
                try { Post("https://" + control + "/Serv.php", Req(id, "request_tunnel"), 20000); diag.AppendLine("request_tunnel : " + LastText); }
                catch (Exception ex) { diag.AppendLine("request_tunnel : ERREUR " + ex.Message); }
            }
            if (region != null) list.Add("https://" + id + "." + region + ".quickconnect.to");
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    // ===================================================================== Bilan de santé
    static class Health
    {
        static string S(Dictionary<string, object> d, params string[] keys)
        {
            foreach (var k in keys) { object v; if (d != null && d.TryGetValue(k, out v) && v != null && !(v is Dictionary<string, object>) && !(v is object[])) { var s = Convert.ToString(v); if (s.Length > 0) return s; } }
            return null;
        }
        static Dictionary<string, object> D(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        static IEnumerable<Dictionary<string, object>> A(Dictionary<string, object> d, string k)
        {
            object v; if (d != null && d.TryGetValue(k, out v) && v is object[]) return ((object[])v).OfType<Dictionary<string, object>>();
            return Enumerable.Empty<Dictionary<string, object>>();
        }

        static Lvl Classify(string s)
        {
            if (string.IsNullOrEmpty(s)) return Lvl.Info;
            var x = s.ToLowerInvariant();
            if (x == "normal" || x == "healthy" || x == "ok" || x == "good" || x == "initialized" || x == "ready" || x == "background") return Lvl.Ok;
            if (x.Contains("crash") || x.Contains("fail") || x.Contains("degrad") || x.Contains("abnormal") || x.Contains("danger") || x.Contains("damage") || x.Contains("broken") || x.Contains("critical")) return Lvl.Bad;
            return Lvl.Warn;
        }
        static Lvl Max(Lvl a, Lvl b) { return (Lvl)Math.Max((int)a, (int)b); }
        static double Num(string s) { double d; return double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0; }

        static bool Probe(string baseUrl)
        {
            try
            {
                Dsm.Trust(new Uri(baseUrl).Host);
                var req = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + "/webapi/query.cgi?api=SYNO.API.Info&version=1&method=query&query=SYNO.API.Auth");
                req.Timeout = 6000; req.ReadWriteTimeout = 6000; req.AllowAutoRedirect = false;
                using (var r = new StreamReader(((HttpWebResponse)req.GetResponse()).GetResponseStream()))
                    return r.ReadToEnd().Contains("\"success\":true");
            }
            catch { return false; }
        }

        public static string DiagFile(NasDevice n) { return Path.Combine(NasPaths.D, "diag", Regex.Replace(n.Name, @"[^\w\-]", "_") + ".txt"); }

        public static HealthReport Check(NasDevice n)
        {
            var rep = new HealthReport { Nas = n.Name, Client = n.Client };
            var diag = new StringBuilder("NasConnect " + AppInfo.Version + " — " + DateTime.Now + " — " + n.Name + "\r\n");
            string baseUrl = null;
            try
            {
                // 1. adresse locale, 2. adresse distante (Tailscale/VPN), 3. QuickConnect
                var tries = new List<string>();
                if (n.Host.Length > 0) tries.Add("https://" + n.Host + ":" + n.Port);
                if (n.Remote.Length > 0) tries.Add("https://" + n.Remote + ":" + n.Port);
                foreach (var t in tries) { diag.AppendLine("essai " + t); if (Probe(t)) { baseUrl = t; break; } }
                if (baseUrl == null && n.QcId.Length > 0)
                {
                    foreach (var t in QuickConnect.Candidates(n.QcId, diag))
                    {
                        diag.AppendLine("essai " + t);
                        if (Probe(t)) { baseUrl = t; break; }
                    }
                }
                if (baseUrl == null)
                {
                    rep.Error = "NAS injoignable" + (n.QcId.Length > 0 ? " (QuickConnect « " + n.QcId + " » : aucune adresse DSM ne répond)" : "");
                    return rep;
                }
                var u = new Uri(baseUrl);
                rep.Via = u.Host.EndsWith(".quickconnect.to", StringComparison.OrdinalIgnoreCase) ? "QuickConnect " + (u.Host.Contains(".direct.") ? "direct" : "relais") : u.Host;
                diag.AppendLine("=> utilisé : " + baseUrl);

                string user, pass;
                if (!n.GetCred(out user, out pass)) { rep.Error = "identifiants DSM manquants : « Modifier » le NAS ou renseigner les identifiants par défaut"; return rep; }

                var api = new Dsm(baseUrl);
                try
                {
                    api.Login(user, pass);
                    var sys = api.SystemInfo();
                    string sModel = S(sys, "model"), fw = S(sys, "firmware_ver"), sTemp = S(sys, "sys_temp", "temperature"), up = S(sys, "up_time");
                    if (sModel != null || fw != null)
                    {
                        double t = Num(sTemp);
                        rep.Items.Add(new HealthItem { Kind = "Système", Name = sModel ?? "", Detail = (fw ?? "") + (t > 0 ? "  ·  " + t + " °C" : "") + (up != null ? "  ·  allumé depuis " + up : ""), Level = t >= 70 ? Lvl.Bad : t >= 60 ? Lvl.Warn : Lvl.Ok });
                    }
                    var st = api.Storage();
                    diag.AppendLine("stockage : " + api.LastRaw);

                    foreach (var p in A(st, "storagePools"))
                    {
                        string status = S(p, "status");
                        string raid = S(p, "device_type", "raidType", "raid_type") ?? "?";
                        string name = S(p, "desc", "display_name", "id", "pool_path") ?? "pool";
                        int nd = 0; object dk; if (p.TryGetValue("disks", out dk) && dk is object[]) nd = ((object[])dk).Length;
                        rep.Items.Add(new HealthItem { Kind = "RAID", Name = name, Detail = raid.Replace("_", " ").ToUpperInvariant() + (nd > 0 ? " · " + nd + " disques" : "") + " · état : " + (status ?? "?"), Level = Classify(status) });
                    }
                    foreach (var v in A(st, "volumes"))
                    {
                        string status = S(v, "status");
                        string name = S(v, "display_name", "vol_path", "id", "desc") ?? "volume";
                        var size = D(v, "size");
                        double total = Num(S(size, "total")), used = Num(S(size, "used"));
                        double pct = total > 0 ? used / total * 100 : -1;
                        var lvl = Classify(status);
                        if (pct >= 95) lvl = Max(lvl, Lvl.Bad); else if (pct >= 85) lvl = Max(lvl, Lvl.Warn);
                        rep.Items.Add(new HealthItem { Kind = "Volume", Name = name, Detail = "état : " + (status ?? "?") + (pct >= 0 ? " · " + pct.ToString("0") + " % utilisé (" + Fmt.Size(used) + " / " + Fmt.Size(total) + ")" : ""), Level = lvl });
                    }
                    int dc = 0;
                    foreach (var d in A(st, "disks"))
                    {
                        dc++;
                        string name = S(d, "longName", "name", "id") ?? ("disque " + dc);
                        string model = S(d, "model", "vendor") ?? "";
                        string status = S(d, "status", "overview_status"), smart = S(d, "smart_status");
                        double temp = Num(S(d, "temp")), size = Num(S(d, "size_total"));
                        string life = S(d, "remain_life"); double lifeN = Num(life);
                        var lvl = Max(Classify(status), Classify(smart));
                        if (temp >= 60) lvl = Max(lvl, Lvl.Bad); else if (temp >= 50) lvl = Max(lvl, Lvl.Warn);
                        if (life != null && lifeN > 0 && lifeN <= 100) { if (lifeN < 10) lvl = Max(lvl, Lvl.Bad); else if (lifeN < 30) lvl = Max(lvl, Lvl.Warn); }
                        rep.Items.Add(new HealthItem
                        {
                            Kind = "Disque", Name = name,
                            Detail = model.Trim() + (size > 0 ? " · " + Fmt.Size(size) : "") + " · état : " + (status ?? "?") + " · SMART : " + (smart ?? "?") + (temp > 0 ? " · " + temp + " °C" : "") + (life != null && lifeN > 0 && lifeN <= 100 ? " · vie restante " + lifeN + " %" : ""),
                            Level = lvl
                        });
                    }
                    if (dc == 0 && rep.Items.All(i => i.Kind == "Système")) rep.Error = "réponse DSM sans disques ni volumes (format inattendu) : voir le diagnostic";
                }
                finally { api.Logout(); }
            }
            catch (DsmException ex) { rep.Error = "DSM : " + ex.Message; }
            catch (WebException ex) { rep.Error = Dsm.LastCertError != null && ex.Status == WebExceptionStatus.TrustFailure ? Dsm.LastCertError : "DSM injoignable : " + ex.Message; }
            catch (Exception ex) { rep.Error = "erreur : " + ex.Message; Log.Write(ex.ToString()); }
            finally
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(DiagFile(n))); File.WriteAllText(DiagFile(n), diag.ToString() + "\r\nRÉSULTAT : " + rep.Worst + (rep.Error != null ? " — " + rep.Error : ""), Encoding.UTF8); } catch { }
            }
            Log.Write("Santé " + n.Name + " => " + rep.Worst + (rep.Error != null ? " (" + rep.Error + ")" : ""));
            return rep;
        }
    }

    // ===================================================================== Checkup hebdomadaire (Planificateur de tâches)
    static class Checkup
    {
        const string TaskName = "NasConnect Checkup";

        public static bool IsScheduled() { string o; return Proc.Run(Proc.Sys("schtasks.exe"), "/query /tn \"" + TaskName + "\"", 10000, out o) == 0; }

        public static bool Schedule(bool on)
        {
            string o;
            if (!on) { Proc.Run(Proc.Sys("schtasks.exe"), "/delete /tn \"" + TaskName + "\" /f", 10000, out o); return true; }
            var now = DateTime.Now;
            var next = now.Date.AddDays(((int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7) % 7).AddHours(12);
            if (next <= now) next = next.AddDays(7);
            string user = SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Author>alex.b</Author><Description>Checkup hebdomadaire des NAS (disques, RAID, volumes) - NasConnect by alex.b</Description></RegistrationInfo>\r\n" +
"  <Triggers><CalendarTrigger><StartBoundary>" + next.ToString("yyyy-MM-ddTHH:mm:ss") + "</StartBoundary><Enabled>true</Enabled>" +
"<ScheduleByWeek><DaysOfWeek><Sunday /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek></CalendarTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
"    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
"    <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>\r\n" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"    <Hidden>false</Hidden>\r\n" +
"    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
"    <WakeToRun>false</WakeToRun>\r\n" +
"    <ExecutionTimeLimit>PT3H</ExecutionTimeLimit>\r\n" +
"    <Priority>7</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>\"" + SecurityElement.Escape(Application.ExecutablePath) + "\"</Command><Arguments>--checkup</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
            var tmp = Path.Combine(Path.GetTempPath(), "nasconnect-checkup.xml");
            try
            {
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                int rc = Proc.Run(Proc.Sys("schtasks.exe"), "/create /tn \"" + TaskName + "\" /xml \"" + tmp + "\" /f", 15000, out o);
                Log.Write("Checkup planifié => " + rc + " " + o);
                return rc == 0;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // vérifie une liste de NAS en parallèle (8 à la fois) ; progress(n fait, total)
        public static List<HealthReport> Run(List<NasDevice> list, Action<int, int, HealthReport> progress)
        {
            var reports = new List<HealthReport>();
            int done = 0;
            Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = 8 }, n =>
            {
                var r = Health.Check(n);
                lock (reports) { reports.Add(r); done++; }
                if (progress != null) progress(done, list.Count, r);
            });
            return reports.OrderByDescending(r => r.Worst).ThenBy(r => r.Client).ThenBy(r => r.Nas).ToList();
        }

        public static void Save(List<HealthReport> reports)
        {
            int bad = reports.Count(r => r.Worst == Lvl.Bad && r.Error == null), warn = reports.Count(r => r.Worst == Lvl.Warn), err = reports.Count(r => r.Error != null), ok = reports.Count(r => r.Worst <= Lvl.Info);
            var sb = new StringBuilder();
            sb.AppendLine("CHECKUP NAS — " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " — NasConnect " + AppInfo.Version + " (by alex.b)");
            sb.AppendLine(reports.Count + " NAS : " + ok + " sain(s), " + warn + " alerte(s), " + bad + " critique(s), " + err + " injoignable(s) / en erreur");
            sb.AppendLine();
            foreach (var r in reports) sb.AppendLine(r.Text());
            try
            {
                File.WriteAllText(Store.LastReportFile, sb.ToString(), Encoding.UTF8);
                File.AppendAllText(Store.HistoryFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "  " + reports.Count + " NAS : " + ok + " OK, " + warn + " alerte, " + bad + " critique, " + err + " erreur" +
                    (bad + warn + err > 0 ? "  →  " + string.Join(", ", reports.Where(r => r.Worst >= Lvl.Warn).Select(r => r.Nas + (r.Error != null ? " (erreur)" : r.Worst == Lvl.Bad ? " (CRITIQUE)" : " (alerte)"))) : "") + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        // exécution planifiée : notification si tout va bien, fenêtre d'alerte sinon
        public static void RunScheduled()
        {
            Log.Write("Checkup planifié démarré");
            var list = Store.LoadNas();
            if (list.Count == 0) return;
            var reports = Run(list, null);
            Save(reports);
            if (reports.Any(r => r.Worst >= Lvl.Warn)) { Application.Run(new AlertForm(reports)); return; }
            using (var ni = new NotifyIcon { Icon = TrayApp.MakeIcon(Theme.Make(true).Ok, "N"), Visible = true, Text = "NasConnect by alex.b" })
            {
                ni.ShowBalloonTip(8000, "Checkup NAS — tout est OK", reports.Count + " NAS vérifié(s) : disques, RAID et volumes sains.", ToolTipIcon.Info);
                var t = new System.Windows.Forms.Timer { Interval = 10000 };
                t.Tick += (s, e) => Application.ExitThread(); t.Start();
                Application.Run();
                ni.Visible = false;
            }
        }
    }

    // ===================================================================== Fenêtre d'alerte (checkup)
    class AlertForm : CyberForm
    {
        public AlertForm(List<HealthReport> reports) : base("ALERTE_NAS", 900, 640)
        {
            TopMost = true;
            var problems = reports.Where(r => r.Worst >= Lvl.Warn).ToList();
            bool critical = problems.Any(r => r.Worst == Lvl.Bad);
            int ok = reports.Count - problems.Count;
            SetSubtitle("Checkup du " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + "  //  " + reports.Count + " NAS  //  by alex.b");
            Func<float, int> k = Theme.S;
            var card = new Card { Location = new Point(k(16), k(12)), Size = new Size(Body.Width - k(32), k(70)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            card.StatusOf = critical ? (Func<Theme, Color>)(t => t.Bad) : (Func<Theme, Color>)(t => t.Warn);
            card.Controls.Add(new Label { Text = (critical ? "CRITIQUE" : "ALERTE") + " : " + problems.Count + " NAS SUR " + reports.Count + " DEMANDENT TON ATTENTION", AutoSize = true, Font = new Font("Consolas", 13f, FontStyle.Bold), Location = new Point(k(18), k(10)), Tag = critical ? "bad" : "warn" });
            card.Controls.Add(new Label { Text = ok + " NAS sains (non détaillés). Vérifie les NAS ci-dessous dans DSM et la sauvegarde de leurs données.", AutoSize = true, Location = new Point(k(20), k(40)), Tag = "muted" });
            Body.Controls.Add(card);
            var box = new LogBox { Location = new Point(k(16), k(94)), Size = new Size(Body.Width - k(32), Body.Height - k(150)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Body.Controls.Add(box);
            var bOpen = new Chip("Ouvrir NasConnect") { Primary = true, AccentOf = t => t.Accent, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var bClose = new Chip("Fermer") { AccentOf = t => t.Muted, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            bOpen.Location = new Point(k(16), Body.Height - k(44)); bClose.Location = new Point(bOpen.Right + k(8), Body.Height - k(44));
            bOpen.Click += (s, e) => { try { Process.Start(Application.ExecutablePath); } catch { } Close(); };
            bClose.Click += (s, e) => Close();
            Body.Controls.Add(bOpen); Body.Controls.Add(bClose);
            Shown += (s, e) => { foreach (var r in problems) Render(box, r, true); System.Media.SystemSounds.Exclamation.Play(); };
        }

        public static void Render(LogBox box, HealthReport r, bool problemsOnly)
        {
            var t = Theme.Current;
            var col = r.Worst == Lvl.Bad ? t.Bad : r.Worst == Lvl.Warn ? t.Warn : r.Worst == Lvl.Ok ? t.Ok : t.Accent;
            box.Line("// " + (r.Client.Length > 0 ? r.Client.ToUpperInvariant() + " / " : "") + r.Nas.ToUpperInvariant() + "  —  " + r.When.ToString("dd/MM HH:mm") + (r.Via.Length > 0 ? "  —  " + r.Via : ""), col, true);
            if (r.Error != null) box.Line("  ✗ " + r.Error, t.Bad);
            foreach (var i in r.Items.OrderByDescending(x => x.Level))
            {
                if (problemsOnly && i.Level < Lvl.Warn) continue;
                var c = i.Level == Lvl.Bad ? t.Bad : i.Level == Lvl.Warn ? t.Warn : i.Level == Lvl.Ok ? t.Ok : t.Text;
                string mark = i.Level == Lvl.Bad ? "✗ CRITIQUE" : i.Level == Lvl.Warn ? "! ALERTE  " : "✓ OK      ";
                box.Line("  " + mark + "  " + (i.Title + " ").PadRight(30, '·') + " " + i.Detail, c);
            }
            box.Line("", t.Text);
        }
    }

    // ===================================================================== Dialogue de saisie générique
    class Field { public string Label, Value, Kind = "text"; public string[] Options; public bool Check; public Control Ctl; }

    class FieldsDialog : Form
    {
        public bool Ok;
        readonly List<Field> fields;

        public FieldsDialog(string title, string hint, List<Field> f)
        {
            fields = f;
            var t = Theme.Current; Func<float, int> k = Theme.S;
            Text = title + " · by alex.b";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            BackColor = t.Bg; Font = new Font("Segoe UI", 9.5f); KeyPreview = true; TopMost = true;
            int y = k(16), W = k(560);
            Controls.Add(new Label { Text = "> " + title.ToUpperInvariant(), Tag = "accent", AutoSize = true, Font = new Font("Consolas", 11.5f, FontStyle.Bold), Location = new Point(k(16), y) });
            y += k(34);
            if (!string.IsNullOrEmpty(hint)) { var h = new Label { Text = hint, Tag = "muted", AutoSize = true, MaximumSize = new Size(W, 0), Location = new Point(k(16), y) }; Controls.Add(h); y += h.PreferredHeight + k(10); }
            foreach (var x in fields)
            {
                if (x.Kind == "check")
                {
                    x.Ctl = new CheckBox { Text = x.Label, Checked = x.Check, AutoSize = true, Location = new Point(k(16), y) };
                    Controls.Add(x.Ctl); y += k(28); continue;
                }
                Controls.Add(new Label { Text = x.Label, AutoSize = true, Location = new Point(k(16), y + k(4)) });
                if (x.Kind == "combo")
                {
                    var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Location = new Point(k(250), y), Width = k(326) };
                    cb.Items.AddRange(x.Options); cb.SelectedItem = x.Value; if (cb.SelectedIndex < 0 && cb.Items.Count > 0) cb.SelectedIndex = 0;
                    x.Ctl = cb;
                }
                else x.Ctl = new TextBox { Text = x.Value ?? "", UseSystemPasswordChar = x.Kind == "password", BorderStyle = BorderStyle.FixedSingle, Location = new Point(k(250), y), Width = k(326), Font = new Font("Consolas", 10f) };
                Controls.Add(x.Ctl); y += k(36);
            }
            y += k(8);
            var ok = new Chip("Enregistrer") { Primary = true, AccentOf = c => c.Ok };
            var cancel = new Chip("Annuler") { AccentOf = c => c.Muted };
            cancel.Location = new Point(k(16) + W - cancel.Width, y); ok.Location = new Point(cancel.Left - k(8) - ok.Width, y);
            ok.Click += (s, e) => { Ok = true; Close(); };
            cancel.Click += (s, e) => Close();
            Controls.Add(ok); Controls.Add(cancel);
            ClientSize = new Size(W + k(32), y + k(48));
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Ok = true; Close(); } };
            CyberForm.Recolor(this);
            Shown += (s, e) => { var first = fields.FirstOrDefault(x => x.Ctl is TextBox); if (first != null) first.Ctl.Focus(); };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Dwm.Style(this); }

        public string V(int i)
        {
            var c = fields[i].Ctl;
            if (c is CheckBox) return ((CheckBox)c).Checked ? "1" : "0";
            if (c is ComboBox) return Convert.ToString(((ComboBox)c).SelectedItem);
            return c.Text.Trim();
        }
        public bool B(int i) { return V(i) == "1"; }
    }

    // ===================================================================== Icône près de l'horloge
    class TrayApp : ApplicationContext
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        public readonly MainForm Form;
        readonly EventWaitHandle showEvent;
        readonly System.Windows.Forms.Timer timer;
        IntPtr hIcon = IntPtr.Zero;
        int pinTries;
        public Lvl LastHealth = Lvl.Info;

        public TrayApp(bool startInTray)
        {
            Form = new MainForm(this);
            var h = Form.Handle;
            menu = new ContextMenuStrip { Renderer = new CyberRenderer(), ShowImageMargin = false };
            menu.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };
            tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "NasConnect by alex.b" };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
            bool cn;
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEvent, out cn);
            new Thread(() => { while (true) { try { showEvent.WaitOne(); Form.BeginInvoke(new Action(ShowMain)); } catch { return; } } }) { IsBackground = true }.Start();
            timer = new System.Windows.Forms.Timer { Interval = 5000 };
            timer.Tick += (s, e) => { if (pinTries++ < 6) TrayPin(); UpdateIcon(); timer.Interval = 30000; };
            timer.Start();
            UpdateIcon();
            // premier lancement : démarrage automatique avec Windows (modifiable dans le menu de l'icône)
            if (Prefs.Ini.Get("AutoStartInit", "0") != "1") { AutoStart = true; Prefs.Ini.Set("AutoStartInit", "1"); Prefs.Save(); }
            if (!startInTray) ShowMain();
        }

        public static Icon MakeIcon(Color c, string letter)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(Color.FromArgb(7, 10, 17))) g.FillEllipse(b, 1, 1, 30, 30);
                    using (var p = new Pen(c, 3f)) g.DrawEllipse(p, 2.5f, 2.5f, 27, 27);
                    using (var f = new Font("Consolas", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    using (var br = new SolidBrush(c)) g.DrawString(letter, f, br, new RectangleF(0, 1, 32, 32), sf);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        public void UpdateIcon()
        {
            var n = Theme.Make(true);
            var shares = Form.Shares;
            int conn = shares.Count(s => s.Matches(Net.Remote(s.Drive)));
            Color c; string txt;
            if (LastHealth == Lvl.Bad) { c = n.Bad; txt = "NAS : CRITIQUE"; }
            else if (LastHealth == Lvl.Warn) { c = n.Warn; txt = "NAS : alerte"; }
            else if (shares.Count == 0) { c = n.Muted; txt = "aucun partage"; }
            else if (conn == shares.Count) { c = n.Ok; txt = conn + " lecteur(s) connecté(s)"; }
            else if (conn > 0) { c = n.Accent; txt = conn + "/" + shares.Count + " lecteurs connectés"; }
            else { c = n.Muted; txt = "lecteurs déconnectés"; }
            var ic = MakeIcon(c, "N");
            var old = tray.Icon; tray.Icon = ic;
            if (old != null) { var oh = old.Handle; old.Dispose(); try { DestroyIcon(oh); } catch { } }
            var t = "NasConnect by alex.b\n" + txt;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void TrayPin()
        {
            try
            {
                string exe = Path.GetFileName(Application.ExecutablePath);
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return;
                    foreach (var name in root.GetSubKeyNames())
                        using (var k = root.OpenSubKey(name, true))
                        {
                            var p = k == null ? null : k.GetValue("ExecutablePath") as string;
                            if (p == null || !p.EndsWith("\\" + exe, StringComparison.OrdinalIgnoreCase)) continue;
                            var cur = k.GetValue("IsPromoted");
                            if (cur == null || Convert.ToInt32(cur) != 1) k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                        }
                }
            }
            catch { }
        }

        public void ShowMain()
        {
            if (!Form.Visible) Form.Show();
            if (Form.WindowState == FormWindowState.Minimized) Form.WindowState = FormWindowState.Normal;
            Form.Activate(); Form.BringToFront();
        }

        public void Balloon(string title, string text, ToolTipIcon icon) { tray.ShowBalloonTip(6000, title, text, icon); }

        void BuildMenu()
        {
            foreach (ToolStripItem it in menu.Items.Cast<ToolStripItem>().ToList()) it.Dispose();
            menu.Items.Clear(); menu.Renderer = new CyberRenderer();
            menu.Items.Add(new ToolStripMenuItem("NASCONNECT  ·  by alex.b") { Enabled = false, Font = new Font("Consolas", 9.5f, FontStyle.Bold) });
            menu.Items.Add(new ToolStripSeparator());
            Add("Ouvrir NasConnect", ShowMain);
            Add("Tout connecter", () => Form.ConnectAll());
            Add("Tout déconnecter", () => Form.DisconnectAll(false));
            Add("Vérifier la santé des NAS", () => { ShowMain(); Form.ShowHealth(); Form.CheckAll(); });
            menu.Items.Add(new ToolStripSeparator());
            var auto = new ToolStripMenuItem("Démarrer avec Windows") { Checked = AutoStart };
            auto.Click += (s, e) => AutoStart = !AutoStart;
            menu.Items.Add(auto);
            Add("Ouvrir le journal", Log.Open);
            menu.Items.Add(new ToolStripSeparator());
            Add("Quitter", Quit);
        }

        void Add(string t, Action a) { var it = new ToolStripMenuItem(t); it.Click += (s, e) => a(); menu.Items.Add(it); }

        // démarrage sans droits admin : clé Run de l'utilisateur (indispensable pour voir les lecteurs dans l'Explorateur)
        public static bool AutoStart
        {
            get { try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return k != null && k.GetValue("NasConnect") != null; } catch { return false; } }
            set
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                        if (value) k.SetValue("NasConnect", "\"" + Application.ExecutablePath + "\" --tray"); else k.DeleteValue("NasConnect", false);
                }
                catch (Exception ex) { Log.Write("Démarrage auto : " + ex.Message); }
            }
        }

        public void Quit()
        {
            tray.Visible = false; tray.Dispose();
            Form.AllowClose = true; Form.Close();
            ExitThread();
        }
    }

    class CyberRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using (var b = new SolidBrush(Theme.Current.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using (var p = new Pen(Theme.Current.Accent)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { using (var b = new SolidBrush(Theme.Current.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var t = Theme.Current; var c = e.Item.Selected && e.Item.Enabled ? Theme.Blend(t.Surface, t.Accent, 0.22) : t.Surface;
            using (var b = new SolidBrush(c)) e.Graphics.FillRectangle(b, 2, 0, e.Item.Width - 4, e.Item.Height);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Theme.Current.Text : Theme.Current.Muted; base.OnRenderItemText(e); }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { int y = e.Item.Height / 2; using (var p = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            using (var f = new Font("Segoe UI Symbol", 9f)) using (var b = new SolidBrush(Theme.Current.Accent)) e.Graphics.DrawString("✓", f, b, e.ImageRectangle.X, e.ImageRectangle.Y - 1);
        }
    }

    // ===================================================================== Fenêtre principale
    class MainForm : CyberForm
    {
        readonly TrayApp app;
        public List<Share> Shares = Store.LoadShares();
        List<NasDevice> nasList = Store.LoadNas();
        readonly Dictionary<string, HealthReport> lastReports = new Dictionary<string, HealthReport>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, bool?> reach = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        readonly Panel drivesView, healthView, list;
        readonly Chip tabDrives, tabHealth;
        readonly Label status;
        public bool AllowClose;
        bool busy;

        public MainForm(TrayApp owner) : base("NAS_CONNECT", 1040, 860)
        {
            app = owner;
            SetSubtitle("Parc NAS via QuickConnect + lecteurs réseau + santé  //  by alex.b");
            Func<float, int> k = Theme.S;

            tabHealth = new Chip("MES NAS (QuickConnect + santé)") { Active = true, AccentOf = t => t.Ok, Location = new Point(k(16), k(10)) };
            tabDrives = new Chip("LECTEURS RÉSEAU") { AccentOf = t => t.Accent };
            tabDrives.Location = new Point(tabHealth.Right + k(8), k(10));
            tabDrives.Click += (s, e) => ShowDrives();
            tabHealth.Click += (s, e) => ShowHealth();
            Body.Controls.Add(tabDrives); Body.Controls.Add(tabHealth);

            // ---------------- vue LECTEURS
            drivesView = new Panel { Location = new Point(0, k(48)), Size = new Size(Body.Width, Body.Height - k(48)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            var bAdd = new Chip("+ Ajouter un partage") { Primary = true, AccentOf = t => t.Ok, Location = new Point(k(16), k(4)) };
            var bAll = new Chip("Tout connecter") { AccentOf = t => t.Accent };
            var bNone = new Chip("Tout déconnecter") { AccentOf = t => t.Bad };
            var bRef = new Chip("↻ Actualiser") { AccentOf = t => t.Muted };
            var bCred = new Chip("Identifiants Windows") { AccentOf = t => t.Accent2 };
            bAll.Location = new Point(bAdd.Right + k(8), k(4)); bNone.Location = new Point(bAll.Right + k(8), k(4));
            bRef.Location = new Point(bNone.Right + k(8), k(4)); bCred.Location = new Point(bRef.Right + k(8), k(4));
            bAdd.Click += (s, e) => EditShare(null);
            bAll.Click += (s, e) => ConnectAll();
            bNone.Click += (s, e) => DisconnectAll(true);
            bRef.Click += (s, e) => Reload(true);
            bCred.Click += (s, e) => { try { Process.Start(Proc.Sys("control.exe"), "/name Microsoft.CredentialManager"); } catch { } };
            status = new Label { AutoSize = true, Location = new Point(k(18), k(44)), Tag = "muted", Font = new Font("Consolas", 9f) };
            list = new Panel { Location = new Point(k(10), k(66)), Size = new Size(drivesView.Width - k(20), drivesView.Height - k(72)), AutoScroll = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            drivesView.Controls.AddRange(new Control[] { bAdd, bAll, bNone, bRef, bCred, status, list });
            Body.Controls.Add(drivesView);

            // ---------------- vue MES NAS
            healthView = new Panel { Location = drivesView.Location, Size = drivesView.Size, Anchor = drivesView.Anchor, Visible = false };
            BuildParcView(healthView);
            Body.Controls.Add(healthView);

            list.Resize += (s, e) => BuildShares();
            Shown += (s, e) => { ShowHealth(); Reload(true); FillList(); RefreshSchedule(); LoadLastReport(); };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; Hide();
                if (Prefs.Ini.Get("TrayTip", "0") != "1") { app?.Balloon("NasConnect — by alex.b", "Toujours actif près de l'horloge. Clic sur l'icône pour rouvrir.", ToolTipIcon.Info); Prefs.Ini.Set("TrayTip", "1"); Prefs.Save(); }
                return;
            }
            base.OnFormClosing(e);
        }

        void Say(string s, string color = "muted") { status.Text = s; status.Tag = color; status.ForeColor = Theme.Current.ByName(color); }

        public void ShowDrives() { drivesView.Visible = true; healthView.Visible = false; tabDrives.Active = true; tabHealth.Active = false; tabDrives.Invalidate(); tabHealth.Invalidate(); }
        public void ShowHealth() { drivesView.Visible = false; healthView.Visible = true; tabDrives.Active = false; tabHealth.Active = true; tabDrives.Invalidate(); tabHealth.Invalidate(); }

        // ================================================================== LECTEURS
        void Reload(bool checkReach)
        {
            BuildShares();
            if (!checkReach) return;
            var hosts = Shares.SelectMany(s => new[] { s.Host, s.Remote }).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            lock (reach) foreach (var h in hosts) reach[h] = null;
            BuildShares();
            Task.Run(() =>
            {
                Parallel.ForEach(hosts, h => { bool ok = Net.Port(h, 445); lock (reach) reach[h] = ok; });
                BeginInvoke(new Action(() => { BuildShares(); app?.UpdateIcon(); }));
            });
        }

        bool? Reach(string h) { bool? r; lock (reach) reach.TryGetValue(h ?? "", out r); return r; }

        void BuildShares()
        {
            Func<float, int> k = Theme.S;
            list.SuspendLayout();
            foreach (Control c in list.Controls.Cast<Control>().ToList()) c.Dispose();
            list.Controls.Clear();
            int w = list.ClientSize.Width - k(12), y = k(6), connected = 0;
            if (Shares.Count == 0)
                list.Controls.Add(new Label { Text = "Aucun partage enregistré.\n« + Ajouter un partage » : ex. \\\\192.168.1.20\\Compta sur la lettre Z:,\navec en option l'adresse Tailscale du NAS pour y accéder hors du bureau.", AutoSize = true, Location = new Point(k(8), y), Tag = "muted" });
            foreach (var s in Shares)
            {
                var remote = Net.Remote(s.Drive);
                bool mine = s.Matches(remote), occupied = remote != null && !mine;
                bool viaRemote = mine && s.Remote.Length > 0 && remote.TrimEnd('\\').Equals(s.UncFor(s.Remote).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                if (mine) connected++;
                bool? rl = Reach(s.Host), rr = s.Remote.Length > 0 ? Reach(s.Remote) : (bool?)false;

                var card = new Card { Location = new Point(k(6), y), Width = w, Height = k(96) };
                card.StatusOf = mine ? (Func<Theme, Color>)(x => x.Ok) : occupied ? (Func<Theme, Color>)(x => x.Warn) : (Func<Theme, Color>)(x => x.Muted);
                card.Controls.Add(new Label { Text = s.Drive + "  " + s.Name, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Location = new Point(k(16), k(8)) });
                card.Controls.Add(new Label { Text = s.Unc + (s.Remote.Length > 0 ? "   ⇄ distant : " + s.Remote : "") + "   ·   " + (s.User.Length > 0 ? s.User : "compte de la session"), AutoSize = true, Font = new Font("Consolas", 9f), Location = new Point(k(17), k(34)), Tag = "muted" });
                var st = new Label { Text = mine ? (viaRemote ? "● CONNECTÉ (DISTANT)" : "● CONNECTÉ") : occupied ? "⚠ " + s.Drive + " PRIS PAR " + remote : "○ DÉCONNECTÉ", AutoSize = true, Font = new Font("Consolas", 8.5f, FontStyle.Bold), Tag = mine ? "ok" : occupied ? "warn" : "muted" };
                card.Controls.Add(st); st.Location = new Point(w - TextRenderer.MeasureText(st.Text, st.Font).Width - k(14), k(12));
                string rt = rl == null ? "test…" : rl.Value ? "NAS joignable en local" : rr == true ? "hors bureau : joignable via " + s.Remote : s.Remote.Length > 0 && rr == null ? "test distant…" : "NAS INJOIGNABLE";
                var re = new Label { Text = rt, AutoSize = true, Font = new Font("Consolas", 8f), Tag = rl == true ? "ok" : rr == true ? "accent" : rl == null || rr == null ? "muted" : "bad" };
                card.Controls.Add(re); re.Location = new Point(w - TextRenderer.MeasureText(re.Text, re.Font).Width - k(14), k(34));

                Share sh = s;
                var chips = new List<Chip>();
                var bConn = new Chip(mine ? "Déconnecter" : "Connecter") { Primary = !mine, AccentOf = mine ? (Func<Theme, Color>)(x => x.Bad) : (Func<Theme, Color>)(x => x.Ok), Enabled = !busy };
                bConn.Click += (o, e) => { if (mine) Disconnect(sh); else Connect(sh, null); };
                chips.Add(bConn);
                var bOpen = new Chip("Ouvrir") { AccentOf = x => x.Accent, Enabled = mine };
                bOpen.Click += (o, e) => { try { Process.Start("explorer.exe", sh.Drive + "\\"); } catch { } };
                chips.Add(bOpen);
                var bEdit = new Chip("Modifier") { AccentOf = x => x.Accent2, Enabled = !busy };
                bEdit.Click += (o, e) => EditShare(sh);
                chips.Add(bEdit);
                var bDel = new Chip("Supprimer") { AccentOf = x => x.Muted, Enabled = !busy };
                bDel.Click += (o, e) => DeleteShare(sh);
                chips.Add(bDel);
                int x0 = k(14);
                foreach (var c in chips) { c.Location = new Point(x0, k(58)); card.Controls.Add(c); x0 += c.Width + k(6); }
                list.Controls.Add(card);
                y += card.Height + k(10);
            }
            Recolor(list);
            list.ResumeLayout();
            if (!busy) Say(Shares.Count + " partage(s) · " + connected + " connecté(s) — mots de passe dans le Gestionnaire d'identification Windows");
        }

        IEnumerable<string> FreeLetters(Share current)
        {
            var used = new HashSet<string>(Shares.Where(x => x != current).Select(x => x.Letter.ToUpperInvariant()));
            var sys = new HashSet<string>(DriveInfo.GetDrives().Where(d => d.DriveType != DriveType.Network).Select(d => d.Name.Substring(0, 1).ToUpperInvariant()));
            for (char c = 'Z'; c >= 'D'; c--) { var l = c.ToString(); if (!sys.Contains(l) && (!used.Contains(l) || (current != null && current.Letter.ToUpperInvariant() == l))) yield return l + ":"; }
        }

        void EditShare(Share s)
        {
            var cur = s ?? new Share();
            var f = new List<Field>
            {
                new Field { Label = "Nom affiché", Value = cur.Name },
                new Field { Label = "Chemin local (\\\\nas\\partage)", Value = cur.Unc },
                new Field { Label = "Adresse distante (Tailscale / VPN)", Value = cur.Remote },
                new Field { Label = "Lettre", Kind = "combo", Options = FreeLetters(s).ToArray(), Value = cur.Letter.ToUpperInvariant() + ":" },
                new Field { Label = "Utilisateur (vide = session)", Value = cur.User },
                new Field { Label = "Mot de passe", Kind = "password" },
                new Field { Label = "Reconnecter à chaque ouverture de session", Kind = "check", Check = cur.Persistent },
                new Field { Label = "Mémoriser le mot de passe (Gestionnaire d'identification Windows)", Kind = "check", Check = cur.SaveCred },
            };
            using (var d = new FieldsDialog(s == null ? "Ajouter un partage" : "Modifier le partage",
                "Chemin local : ex. \\\\192.168.1.20\\Compta. Adresse distante : IP ou nom Tailscale du NAS (ex. 100.101.102.103 ou mon-nas), utilisée automatiquement hors du bureau. QuickConnect ne permet pas les lecteurs réseau.", f))
            {
                d.ShowDialog(this);
                if (!d.Ok) return;
                var lv = d.V(3) ?? "";
                var r = new Share { Name = d.V(0), Unc = d.V(1).TrimEnd('\\'), Remote = d.V(2).Trim().Trim('\\'), Letter = lv.Length > 0 ? lv.Substring(0, 1) : cur.Letter, User = d.V(4), Persistent = d.B(6), SaveCred = d.B(7) };
                if (!Regex.IsMatch(r.Unc, @"^\\\\[^\\/:*?""<>|]+\\[^\\/:*?""<>|]+(\\.*)?$")) { Warn(this, "Chemin invalide. Format attendu : \\\\serveur\\partage"); return; }
                if (r.Name.Length == 0) r.Name = r.Unc;
                string pw = d.V(5);
                if (s == null) Shares.Add(r); else Shares[Shares.IndexOf(s)] = r;
                if (r.SaveCred && pw.Length > 0 && r.User.Length > 0) { Net.SaveCred(r.Host, r.User, pw, 2); if (r.Remote.Length > 0) Net.SaveCred(r.Remote, r.User, pw, 2); }
                Store.SaveShares(Shares);
                Reload(true);
                if (Ask(this, "Connecter « " + r.Name + " » maintenant ?")) Connect(r, pw.Length > 0 ? pw : null);
            }
        }

        void DeleteShare(Share s)
        {
            if (!Ask(this, "Retirer « " + s.Name + " » de la liste ?")) return;
            if (s.Matches(Net.Remote(s.Drive)) && Ask(this, "Déconnecter aussi le lecteur " + s.Drive + " ?")) Net.Disconnect(s.Drive, false);
            if (Net.HasCred(s.Host, 2) && !Shares.Any(x => x != s && x.Host.Equals(s.Host, StringComparison.OrdinalIgnoreCase)) && Ask(this, "Supprimer aussi le mot de passe mémorisé pour " + s.Host + " ?"))
            { Net.DeleteCred(s.Host, 2); if (s.Remote.Length > 0) Net.DeleteCred(s.Remote, 2); }
            Shares.Remove(s); Store.SaveShares(Shares); Reload(false); app?.UpdateIcon();
        }

        // choisit l'adresse : locale si le NAS répond, sinon distante (Tailscale/VPN)
        string PickUnc(Share s, out string via)
        {
            via = "local";
            if (Net.Port(s.Host, 445)) return s.Unc;
            if (s.Remote.Length > 0 && Net.Port(s.Remote, 445, 2500)) { via = "distant (" + s.Remote + ")"; return s.UncFor(s.Remote); }
            return null;
        }

        public void Connect(Share s, string password)
        {
            var remote = Net.Remote(s.Drive);
            if (remote != null && !s.Matches(remote))
            {
                if (!Ask(this, s.Drive + " est déjà connecté à " + remote + ".\nLe remplacer par " + s.Unc + " ?")) return;
                Net.Disconnect(s.Drive, false);
            }
            Say("connexion de " + s.Drive + "…", "accent"); Application.DoEvents();
            string via;
            var unc = PickUnc(s, out via);
            if (unc == null)
            {
                Say(s.Drive + " : NAS injoignable", "bad");
                Warn(this, s.Name + "\n\nLe NAS ne répond ni en local (" + s.Host + ")" + (s.Remote.Length > 0 ? " ni via " + s.Remote : "") + ".\n\n" +
                    (s.Remote.Length == 0 ? "Hors du bureau : installe Tailscale sur le NAS et le PC, puis renseigne l'adresse Tailscale du NAS dans « Modifier »." : "Vérifie que Tailscale (ou le VPN) est connecté sur ce PC."));
                return;
            }
            int rc = Net.Connect(s.Drive, unc, s.User, password, s.Persistent);
            if ((rc == 86 || rc == 1326 || rc == 1244 || rc == 5) && password == null)
            {
                var f = new List<Field> { new Field { Label = "Utilisateur", Value = s.User }, new Field { Label = "Mot de passe", Kind = "password" }, new Field { Label = "Mémoriser", Kind = "check", Check = s.SaveCred } };
                using (var d = new FieldsDialog("Identifiants pour " + s.Host, Net.Error(rc), f))
                {
                    d.ShowDialog(this);
                    if (d.Ok)
                    {
                        s.User = d.V(0); s.SaveCred = d.B(2); Store.SaveShares(Shares);
                        if (s.SaveCred && d.V(1).Length > 0 && s.User.Length > 0) { Net.SaveCred(s.Host, s.User, d.V(1), 2); if (s.Remote.Length > 0) Net.SaveCred(s.Remote, s.User, d.V(1), 2); }
                        rc = Net.Connect(s.Drive, unc, s.User, d.V(1), s.Persistent);
                    }
                }
            }
            if (rc == 1219 && Ask(this, Net.Error(rc) + ".\n\nDéconnecter toutes les connexions vers ce serveur puis réessayer ?"))
            {
                DropHost(new Uri("file:" + unc.Replace('\\', '/')).Host);
                rc = Net.Connect(s.Drive, unc, s.User, password, s.Persistent);
            }
            BuildShares(); app?.UpdateIcon();
            if (rc == 0) Say(s.Drive + " connecté — " + via, "ok");
            else { Say(s.Drive + " : " + Net.Error(rc), "bad"); if (rc != 1219) Warn(this, s.Name + " (" + unc + ")\n\n" + Net.Error(rc)); }
        }

        void Disconnect(Share s)
        {
            int rc = Net.Disconnect(s.Drive, false);
            if (rc == 2401 && Ask(this, "Des fichiers sont ouverts sur " + s.Drive + ". Forcer la déconnexion ?")) rc = Net.Disconnect(s.Drive, true);
            BuildShares(); app?.UpdateIcon();
            Say(rc == 0 ? s.Drive + " déconnecté" : s.Drive + " : " + Net.Error(rc), rc == 0 ? "muted" : "bad");
        }

        void DropHost(string host)
        {
            string o; Proc.Run(Proc.Sys("net.exe"), "use", 15000, out o);
            foreach (Match m in Regex.Matches(o, @"\b([D-Z]:)\s+\\\\" + Regex.Escape(host) + @"\\", RegexOptions.IgnoreCase)) Net.Disconnect(m.Groups[1].Value, false);
            foreach (Match m in Regex.Matches(o, @"(\\\\" + Regex.Escape(host) + @"\\\S+)", RegexOptions.IgnoreCase)) { string o2; Proc.Run(Proc.Sys("net.exe"), "use \"" + m.Groups[1].Value + "\" /delete /y", 15000, out o2); }
        }

        public void ConnectAll()
        {
            int ok = 0, ko = 0;
            foreach (var s in Shares)
            {
                var r = Net.Remote(s.Drive);
                if (s.Matches(r)) { ok++; continue; }
                if (r != null) { ko++; continue; }
                string via; var unc = PickUnc(s, out via);
                if (unc != null && Net.Connect(s.Drive, unc, s.User, null, s.Persistent) == 0) ok++; else ko++;
            }
            BuildShares(); app?.UpdateIcon();
            Say(ok + " connecté(s)" + (ko > 0 ? ", " + ko + " en échec (clique sur Connecter pour le détail)" : ""), ko > 0 ? "warn" : "ok");
            if (!Visible) app?.Balloon("NasConnect", ok + " lecteur(s) connecté(s)" + (ko > 0 ? ", " + ko + " en échec" : ""), ko > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }

        public void DisconnectAll(bool ask)
        {
            if (ask && !Ask(this, "Déconnecter tous les lecteurs de la liste ?")) return;
            foreach (var s in Shares) if (s.Matches(Net.Remote(s.Drive))) Net.Disconnect(s.Drive, false);
            BuildShares(); app?.UpdateIcon(); Say("lecteurs déconnectés");
        }

        // ================================================================== MES NAS (parc QuickConnect)
        ListView lv;
        TextBox search;
        Label parcStatus;
        NeonBar parcBar;
        Chip bSched;
        LogBox healthBox;

        void BuildParcView(Panel v)
        {
            Func<float, int> k = Theme.S;
            var bAdd = new Chip("+ Ajouter") { Primary = true, AccentOf = t => t.Ok, Location = new Point(k(16), k(4)) };
            var bImp = new Chip("Importer…") { AccentOf = t => t.Accent };
            var bDef = new Chip("Identifiants par défaut") { AccentOf = t => t.Accent2 };
            var bChk = new Chip("▶ Vérifier tout") { AccentOf = t => t.Ok };
            bSched = new Chip("Checkup dimanche 12 h : …") { AccentOf = t => t.Accent2 };
            var bHist = new Chip("Historique") { AccentOf = t => t.Muted };
            var row = new Chip[] { bAdd, bImp, bDef, bChk, bSched, bHist };
            Action place = () => { int x = k(16); foreach (var c in row) { c.Location = new Point(x, k(4)); x = c.Right + k(6); } };
            place();
            bAdd.Click += (s, e) => EditNas(null);
            bImp.Click += (s, e) => Import();
            bDef.Click += (s, e) => EditDefaultCred();
            bChk.Click += (s, e) => CheckAll();
            bSched.Click += (s, e) => { ToggleSchedule(); };
            bHist.Click += (s, e) => { try { if (!File.Exists(Store.HistoryFile)) File.WriteAllText(Store.HistoryFile, "# Historique des checkups NAS (NasConnect by alex.b)\r\n", Encoding.UTF8); Process.Start("notepad.exe", "\"" + Store.HistoryFile + "\""); } catch { } };
            schedPlace = place;

            var lbl = new Label { Text = "Rechercher", AutoSize = true, Location = new Point(k(18), k(48)), Tag = "muted" };
            search = new TextBox { Location = new Point(k(100), k(44)), Width = k(280), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10f) };
            search.TextChanged += (s, e) => FillList();
            parcStatus = new Label { AutoSize = true, Location = new Point(k(396), k(48)), Tag = "muted", Font = new Font("Consolas", 9f) };
            parcBar = new NeonBar { Location = new Point(k(16), k(74)), Width = v.Width - k(32), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

            lv = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, OwnerDraw = true, BorderStyle = BorderStyle.None,
                Location = new Point(k(16), k(92)), Size = new Size(v.Width - k(32), k(330)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9.5f)
            };
            lv.Columns.Add("État", k(110)); lv.Columns.Add("Client", k(150)); lv.Columns.Add("NAS", k(170));
            lv.Columns.Add("QuickConnect ID", k(160)); lv.Columns.Add("Dernier contrôle", k(120)); lv.Columns.Add("Détail", k(400));
            lv.DrawColumnHeader += (s, e) =>
            {
                var t = Theme.Current;
                using (var b = new SolidBrush(t.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var p = new Pen(t.Border)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, new Font("Segoe UI Semibold", 9f), Rectangle.Inflate(e.Bounds, -6, 0), t.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
            lv.DrawItem += (s, e) => { };
            lv.DrawSubItem += (s, e) =>
            {
                var t = Theme.Current;
                bool sel = e.Item.Selected;
                using (var b = new SolidBrush(sel ? Theme.Blend(t.Surface, t.Accent, 0.22) : (e.ItemIndex % 2 == 0 ? t.Surface : Theme.Blend(t.Surface, t.Surface2, 0.6)))) e.Graphics.FillRectangle(b, e.Bounds);
                Color fore = t.Text;
                if (e.ColumnIndex == 0 || e.ColumnIndex == 5) fore = t.ByName(e.Item.Tag as string ?? "muted");
                if (e.ColumnIndex == 3 || e.ColumnIndex == 4) fore = t.Muted;
                var f = e.ColumnIndex == 0 ? new Font("Consolas", 8.5f, FontStyle.Bold) : e.ColumnIndex == 3 ? new Font("Consolas", 9f) : lv.Font;
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, f, Rectangle.Inflate(e.Bounds, -6, 0), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            lv.DoubleClick += (s, e) => { var n = Selected().FirstOrDefault(); if (n != null) OpenDsm(n); };
            lv.SelectedIndexChanged += (s, e) => ShowSelectedReport();
            lv.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { var n = Selected().FirstOrDefault(); if (n != null) OpenDsm(n); } if (e.KeyCode == Keys.Delete) DeleteNas(); if (e.Control && e.KeyCode == Keys.C) { var n = Selected().FirstOrDefault(); if (n != null) CopyPass(n); } };

            var aOpen = new Chip("Ouvrir DSM (QuickConnect)") { Primary = true, AccentOf = t => t.Accent };
            var aCopy = new Chip("Copier le mot de passe") { AccentOf = t => t.Accent2 };
            var aChk = new Chip("Vérifier la sélection") { AccentOf = t => t.Ok };
            var aEdit = new Chip("Modifier") { AccentOf = t => t.Muted };
            var aDel = new Chip("Supprimer") { AccentOf = t => t.Bad };
            var aDiag = new Chip("Diagnostic") { AccentOf = t => t.Muted };
            var acts = new Chip[] { aOpen, aCopy, aChk, aEdit, aDel, aDiag };
            int ax = k(16);
            foreach (var c in acts) { c.Location = new Point(ax, k(430)); c.Anchor = AnchorStyles.Top | AnchorStyles.Left; ax = c.Right + k(6); }
            aOpen.Click += (s, e) => { var n = Selected().FirstOrDefault(); if (n != null) OpenDsm(n); };
            aCopy.Click += (s, e) => { var n = Selected().FirstOrDefault(); if (n != null) CopyPass(n); };
            aChk.Click += (s, e) => RunChecks(Selected());
            aEdit.Click += (s, e) => { var n = Selected().FirstOrDefault(); if (n != null) EditNas(n); };
            aDel.Click += (s, e) => DeleteNas();
            aDiag.Click += (s, e) => { var n = Selected().FirstOrDefault(); if (n == null) return; var f = Health.DiagFile(n); if (File.Exists(f)) Process.Start("notepad.exe", "\"" + f + "\""); else Info(this, "Pas encore de diagnostic : lance « Vérifier » sur ce NAS."); };

            healthBox = new LogBox { Location = new Point(k(16), k(470)), Size = new Size(v.Width - k(32), v.Height - k(478)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            v.Controls.AddRange(new Control[] { bAdd, bImp, bDef, bChk, bSched, bHist, lbl, search, parcStatus, parcBar, lv });
            v.Controls.AddRange(acts);
            v.Controls.Add(healthBox);
            Action fit = () => { try { lv.Columns[5].Width = Math.Max(k(160), lv.ClientSize.Width - lv.Columns.Cast<ColumnHeader>().Take(5).Sum(c => c.Width) - 4); } catch { } };
            v.Resize += (s, e) => fit();
            lv.Resize += (s, e) => fit();
            lv.HandleCreated += (s, e) => fit();

            healthBox.Line("NasConnect " + AppInfo.Version + " · Développé by alex.b", "accent", true);
            healthBox.Line("Double-clic (ou Entrée) sur un NAS : ouvre DSM via QuickConnect et copie le mot de passe (effacé du presse-papiers après 30 s).", "muted");
            healthBox.Line("« Importer… » : export des mots de passe de Chrome / Edge / Firefox (CSV) ou fichier CSV Client;QuickConnectID;Identifiant;MotDePasse.", "muted");
            healthBox.Line("Santé : compte DSM administrateur sans vérification en 2 étapes. Sécurité : crée sur chaque NAS un compte DÉDIÉ à NasConnect (mot de passe unique), garde ton compte personnel avec 2FA pour te connecter à DSM.", "warn");
            healthBox.Line("Mots de passe chiffrés par Windows, copie hors historique du presse-papiers, certificats des NAS mémorisés (alerte s'ils changent). Résolution QuickConnect non documentée : en cas d'échec, « Diagnostic ».", "muted");
        }

        Action schedPlace;

        List<NasDevice> Selected()
        {
            var names = lv.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Name).ToList();
            return nasList.Where(n => names.Contains(n.Name)).ToList();
        }

        void FillList()
        {
            var q = (search.Text ?? "").Trim().ToLowerInvariant();
            var sel = new HashSet<string>(lv.SelectedItems.Cast<ListViewItem>().Select(i => i.Name));
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var n in nasList.OrderBy(x => x.Client).ThenBy(x => x.Name))
            {
                if (q.Length > 0 && !(n.Client + " " + n.Name + " " + n.QcId + " " + n.Host).ToLowerInvariant().Contains(q)) continue;
                HealthReport r; lastReports.TryGetValue(n.Name, out r);
                string st, tag, detail;
                if (r == null) { st = "○ non vérifié"; tag = "muted"; detail = n.Host.Length > 0 ? "local " + n.Host : ""; }
                else if (r.Error != null) { st = "✗ ERREUR"; tag = "bad"; detail = r.Error; }
                else if (r.Worst == Lvl.Bad) { st = "✗ CRITIQUE"; tag = "bad"; detail = string.Join(" · ", r.Items.Where(i => i.Level == Lvl.Bad).Select(i => i.Title)); }
                else if (r.Worst == Lvl.Warn) { st = "! ALERTE"; tag = "warn"; detail = string.Join(" · ", r.Items.Where(i => i.Level == Lvl.Warn).Select(i => i.Title)); }
                else { st = "● SAIN"; tag = "ok"; detail = r.Items.Count(i => i.Kind == "Disque") + " disques, " + r.Items.Count(i => i.Kind == "Volume") + " volume(s) — via " + r.Via; }
                var it = new ListViewItem(new[] { st, n.Client, n.Name, n.QcId, r != null ? r.When.ToString("dd/MM HH:mm") : "—", detail }) { Name = n.Name, Tag = tag };
                lv.Items.Add(it);
                if (sel.Contains(n.Name)) it.Selected = true;
            }
            lv.EndUpdate();
            int bad = lastReports.Values.Count(r => r.Worst == Lvl.Bad), warn = lastReports.Values.Count(r => r.Worst == Lvl.Warn);
            if (!busy) { parcStatus.Text = nasList.Count + " NAS · " + lv.Items.Count + " affiché(s)" + (lastReports.Count > 0 ? " · " + bad + " critique/erreur · " + warn + " alerte" : ""); parcStatus.ForeColor = Theme.Current.Muted; }
        }

        void ShowSelectedReport()
        {
            var n = Selected().FirstOrDefault();
            if (n == null || busy) return;
            HealthReport r;
            healthBox.Clear();
            if (lastReports.TryGetValue(n.Name, out r)) AlertForm.Render(healthBox, r, false);
            else healthBox.Line(n.Name + " : pas encore vérifié — « Vérifier la sélection ».", "muted");
        }

        // ---------------------------------------------------------- connexion DSM
        System.Windows.Forms.Timer clipTimer;
        void CopyPass(NasDevice n)
        {
            string user, pass;
            if (!n.GetCred(out user, out pass)) { Warn(this, "Aucun mot de passe enregistré pour " + n.Name + " (ni identifiants par défaut)."); return; }
            SetPrivateClipboard(pass);
            if (clipTimer != null) clipTimer.Dispose();
            clipTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            clipTimer.Tick += (s, e) => { clipTimer.Stop(); try { if (Clipboard.ContainsText() && Clipboard.GetText() == pass) Clipboard.Clear(); } catch { } };
            clipTimer.Start();
            app?.Balloon(n.Name, "Identifiant : " + user + "\nMot de passe copié (Ctrl+V) — effacé dans 30 s.", ToolTipIcon.Info);
            parcStatus.Text = n.Name + " : identifiant « " + user + " », mot de passe copié (30 s)"; parcStatus.ForeColor = Theme.Current.Accent;
        }

        // texte copié SANS entrer dans l'historique du presse-papiers (Win+V) ni dans la synchronisation cloud
        static void SetPrivateClipboard(string text)
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
            Clipboard.SetDataObject(data, false);
        }

        void OpenDsm(NasDevice n)
        {
            try { Process.Start(n.DsmUrl); } catch (Exception ex) { Warn(this, "Impossible d'ouvrir le navigateur : " + ex.Message); return; }
            CopyPass(n);
        }

        // ---------------------------------------------------------- ajout / modification
        void EditNas(NasDevice n)
        {
            var cur = n ?? new NasDevice();
            var clients = nasList.Select(x => x.Client).Where(c => c.Length > 0).Distinct().OrderBy(c => c).ToArray();
            var f = new List<Field>
            {
                new Field { Label = "Client", Value = cur.Client },
                new Field { Label = "Nom du NAS", Value = cur.Name },
                new Field { Label = "QuickConnect ID (ou lien)", Value = cur.QcId },
                new Field { Label = "Identifiant DSM (vide = défaut)", Value = cur.User },
                new Field { Label = "Mot de passe (vide = inchangé)", Kind = "password" },
                new Field { Label = "Adresse locale (optionnel)", Value = cur.Host },
                new Field { Label = "Adresse Tailscale/VPN (optionnel)", Value = cur.Remote },
                new Field { Label = "Port DSM HTTPS", Value = cur.Port.ToString() },
            };
            using (var d = new FieldsDialog(n == null ? "Ajouter un NAS" : "Modifier le NAS",
                "Colle le QuickConnect ID ou le lien (ex. https://client-dupont.quickconnect.to). Identifiant/mot de passe vides = identifiants par défaut. Le mot de passe est chiffré par Windows (Gestionnaire d'identification)." + (clients.Length > 0 ? " Clients existants : " + string.Join(", ", clients.Take(12)) + (clients.Length > 12 ? "…" : "") : ""), f))
            {
                d.ShowDialog(this);
                if (!d.Ok) return;
                int port; if (!int.TryParse(d.V(7), out port) || port < 1 || port > 65535) port = 5001;
                var qc = ParseQc(d.V(2));
                var r = new NasDevice { Client = d.V(0), Name = d.V(1).Length > 0 ? d.V(1) : qc, QcId = qc, User = d.V(3), Host = d.V(5).Trim('\\', '/'), Remote = d.V(6).Trim('\\', '/'), Port = port };
                if (r.Name.Length == 0) { Warn(this, "Indique au moins un nom ou un QuickConnect ID."); return; }
                if (r.QcId.Length == 0 && r.Host.Length == 0 && r.Remote.Length == 0) { Warn(this, "Indique un QuickConnect ID ou une adresse."); return; }
                if (nasList.Any(x => x != n && x.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase))) { Warn(this, "Un NAS porte déjà ce nom."); return; }
                string pw = d.V(4);
                if (n != null && !n.Name.Equals(r.Name, StringComparison.Ordinal))
                {
                    var old = Net.ReadPassword(n.CredKey, 1);
                    Net.DeleteCred(n.CredKey, 1);
                    if (old != null && pw.Length == 0) Net.SaveCred(r.CredKey, r.User.Length > 0 ? r.User : n.User, old, 1);
                    HealthReport rep; if (lastReports.TryGetValue(n.Name, out rep)) { lastReports.Remove(n.Name); lastReports[r.Name] = rep; }
                }
                if (pw.Length > 0) Net.SaveCred(r.CredKey, r.User.Length > 0 ? r.User : (Net.ReadUser(NasDevice.DefaultKey, 1) ?? ""), pw, 1);
                if (n == null) nasList.Add(r); else nasList[nasList.IndexOf(n)] = r;
                Dsm.ForgetPins(r.Host, r.Remote, n != null ? n.Host : null, n != null ? n.Remote : null);
                Store.SaveNas(nasList);
                FillList();
            }
        }

        static string ParseQc(string s)
        {
            s = (s ?? "").Trim();
            var m = Regex.Match(s, @"^(?:https?://)?([A-Za-z0-9\-]+)(?:\.[a-z]{2,4}\d*)?\.quickconnect\.to", RegexOptions.IgnoreCase);
            if (m.Success && !m.Groups[1].Value.Equals("global", StringComparison.OrdinalIgnoreCase)) return m.Groups[1].Value;
            m = Regex.Match(s, @"quickconnect\.to/([A-Za-z0-9\-]+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            return Regex.IsMatch(s, @"^[A-Za-z0-9\-]+$") ? s : "";
        }

        void EditDefaultCred()
        {
            var f = new List<Field>
            {
                new Field { Label = "Identifiant DSM par défaut", Value = Net.ReadUser(NasDevice.DefaultKey, 1) ?? "" },
                new Field { Label = "Mot de passe (vide = inchangé)", Kind = "password" },
            };
            using (var d = new FieldsDialog("Identifiants par défaut", "Utilisés pour tous les NAS qui n'ont pas leurs propres identifiants. Chiffrés par Windows. Conseil : un mot de passe différent par NAS reste plus sûr.", f))
            {
                d.ShowDialog(this);
                if (!d.Ok) return;
                var pw = d.V(1).Length > 0 ? d.V(1) : Net.ReadPassword(NasDevice.DefaultKey, 1);
                if (d.V(0).Length == 0 || string.IsNullOrEmpty(pw)) { Warn(this, "Identifiant et mot de passe requis."); return; }
                Net.SaveCred(NasDevice.DefaultKey, d.V(0), pw, 1);
                Info(this, "Identifiants par défaut enregistrés.");
            }
        }

        void DeleteNas()
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            if (!Ask(this, "Supprimer " + sel.Count + " NAS de la liste ? (leurs mots de passe enregistrés seront effacés de Windows)")) return;
            foreach (var n in sel) { Net.DeleteCred(n.CredKey, 1); nasList.Remove(n); lastReports.Remove(n.Name); }
            Store.SaveNas(nasList); FillList();
        }

        // ---------------------------------------------------------- import
        void Import()
        {
            using (var d = new OpenFileDialog { Filter = "Fichiers CSV (*.csv)|*.csv|Tous les fichiers|*.*", Title = "Export des mots de passe du navigateur ou CSV Client;QuickConnectID;Identifiant;MotDePasse" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                int added = 0, updated = 0, skipped = 0;
                try
                {
                    var lines = File.ReadAllLines(d.FileName, Encoding.UTF8).Where(l => l.Trim().Length > 0).ToList();
                    if (lines.Count == 0) return;
                    char sep = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : ',';
                    var head = SplitCsv(lines[0], sep).Select(h => h.Trim().ToLowerInvariant()).ToList();
                    int iUrl = head.FindIndex(h => h == "url" || h == "origin_url" || h == "login_uri");
                    int iUser = head.FindIndex(h => h == "username" || h == "login_username" || h == "identifiant" || h == "user");
                    int iPass = head.FindIndex(h => h == "password" || h == "login_password" || h == "motdepasse" || h == "mot de passe");
                    int iName = head.FindIndex(h => h == "name" || h == "nom");
                    int iClient = head.FindIndex(h => h == "client" || h == "folder");
                    int iQc = head.FindIndex(h => h == "quickconnectid" || h == "quickconnect" || h == "qcid");
                    bool hasHeader = iUrl >= 0 || iPass >= 0 || iQc >= 0;
                    if (!hasHeader) { iClient = 0; iQc = 1; iUser = 2; iPass = 3; }
                    foreach (var line in lines.Skip(hasHeader ? 1 : 0))
                    {
                        var c = SplitCsv(line, sep);
                        Func<int, string> at = i => i >= 0 && i < c.Count ? c[i].Trim() : "";
                        string qc = ParseQc(iQc >= 0 ? at(iQc) : at(iUrl));
                        if (qc.Length == 0) { skipped++; continue; }   // ligne sans lien QuickConnect (autres sites)
                        string user = at(iUser), pass = at(iPass), client = at(iClient), name = qc;
                        var ex = nasList.FirstOrDefault(x => x.QcId.Equals(qc, StringComparison.OrdinalIgnoreCase));
                        if (ex == null) { ex = new NasDevice { Client = client, Name = name, QcId = qc, User = user }; nasList.Add(ex); added++; }
                        else { if (user.Length > 0) ex.User = user; if (client.Length > 0) ex.Client = client; updated++; }
                        if (pass.Length > 0) Net.SaveCred(ex.CredKey, ex.User, pass, 1);
                    }
                    Store.SaveNas(nasList); FillList();
                }
                catch (Exception ex) { Warn(this, "Import impossible : " + ex.Message); return; }
                string msg = added + " NAS ajouté(s), " + updated + " mis à jour, " + skipped + " ligne(s) ignorée(s) (pas de lien QuickConnect).\n\n" +
                             "Ce fichier contient des mots de passe EN CLAIR. Le supprimer maintenant ?";
                if (Ask(this, msg)) { if (Hardening.Shred(d.FileName)) Info(this, "Fichier écrasé puis supprimé.\nSi une copie existe ailleurs (Téléchargements, corbeille, OneDrive), supprime-la aussi."); else Warn(this, "Suppression impossible : supprime le fichier à la main."); }
            }
        }

        static List<string> SplitCsv(string line, char sep)
        {
            var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (q) { if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(ch); }
                else if (ch == '"') q = true;
                else if (ch == sep) { r.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            r.Add(sb.ToString());
            return r;
        }

        // ---------------------------------------------------------- vérifications
        public void CheckAll() { RunChecks(nasList.ToList()); }

        void RunChecks(List<NasDevice> targets)
        {
            if (busy) return;
            if (targets.Count == 0) { Warn(this, nasList.Count == 0 ? "Ajoute ou importe d'abord des NAS." : "Sélectionne au moins un NAS."); return; }
            busy = true; UseWaitCursor = true;
            healthBox.Clear();
            healthBox.Line("Vérification de " + targets.Count + " NAS (8 en parallèle)…", "accent", true);
            parcBar.Value = 0;
            bool all = targets.Count == nasList.Count;
            Task.Run(() =>
            {
                var reps = Checkup.Run(targets, (done, total, r) => BeginInvoke(new Action(() =>
                {
                    parcBar.Value = (double)done / total;
                    parcStatus.Text = "vérification " + done + "/" + total + " — " + r.Nas + " : " + (r.Error != null ? "erreur" : r.Worst.ToString());
                    parcStatus.ForeColor = Theme.Current.Accent;
                    lastReports[r.Nas] = r;
                })));
                if (all) Checkup.Save(reps);
                BeginInvoke(new Action(() =>
                {
                    busy = false; UseWaitCursor = false;
                    healthBox.Clear();
                    var problems = reps.Where(x => x.Worst >= Lvl.Warn).ToList();
                    healthBox.Line(reps.Count + " NAS vérifié(s) : " + (reps.Count - problems.Count) + " sain(s), " + problems.Count + " à surveiller.", problems.Count > 0 ? "warn" : "ok", true);
                    healthBox.Line("", "text");
                    foreach (var r in problems) AlertForm.Render(healthBox, r, true);
                    if (problems.Count == 0) healthBox.Line("Tout est sain. Clique sur un NAS pour voir son détail.", "ok");
                    if (app != null) app.LastHealth = lastReports.Values.Count == 0 ? Lvl.Info : lastReports.Values.Max(x => x.Worst);
                    app?.UpdateIcon();
                    FillList();
                    if (problems.Count > 0 && !Visible) app?.Balloon("NasConnect — " + problems.Count + " NAS à surveiller", "Clic sur l'icône pour le détail.", ToolTipIcon.Warning);
                }));
            });
        }

        void LoadLastReport()
        {
            try
            {
                if (!File.Exists(Store.LastReportFile)) return;
                var txt = File.ReadAllText(Store.LastReportFile);
                if (app != null) app.LastHealth = txt.Contains("[!!]") ? Lvl.Bad : txt.Contains("[! ]") ? Lvl.Warn : Lvl.Ok;
                app?.UpdateIcon();
                healthBox.Line("", "text");
                healthBox.Line("// DERNIER CHECKUP : " + File.GetLastWriteTime(Store.LastReportFile).ToString("dd/MM/yyyy HH:mm"), "accent", true);
                foreach (var l in File.ReadAllLines(Store.LastReportFile, Encoding.UTF8).Skip(1).Take(2)) healthBox.Line(l, "muted");
            }
            catch { }
        }

        void RefreshSchedule()
        {
            Task.Run(() =>
            {
                bool on = Checkup.IsScheduled();
                BeginInvoke(new Action(() => { bSched.SetText(on ? "✓ Checkup dimanche 12 h : ACTIVÉ" : "Checkup dimanche 12 h : désactivé"); if (schedPlace != null) schedPlace(); }));
            });
        }

        void ToggleSchedule()
        {
            bool on = Checkup.IsScheduled();
            if (!on && nasList.Count == 0) { Warn(this, "Ajoute d'abord des NAS."); return; }
            if (!Checkup.Schedule(!on)) Warn(this, "Impossible de créer la tâche planifiée (voir le journal).");
            else if (!on) Info(this, "Checkup planifié chaque dimanche à 12 h (lancé au démarrage suivant si le PC était éteint).\nTout est sain = notification ; un problème = fenêtre d'alerte avec la liste des NAS concernés.");
            RefreshSchedule();
        }
    }
}
