// Tech IT AB - page Cyber : scans Nmap encadrés (périmètre, autorisation, journal), règles de risque ANSSI/CIS,
// cyber du poste (chasse aux menaces, persistance, connexions, mot de passe compromis, fichier suspect), rapport client.
// Aucun module offensif : catégories Nmap exploit, dos, brute, malware, fuzzer et intrusive refusées (seule exception
// documentée : ssl-enum-ciphers, classé « intrusive » par Nmap uniquement parce qu'il ouvre beaucoup de connexions).
// Développé by alex.b
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;
using AlexB;

namespace AmiOps
{
    class CyFinding { public string Level = "info", Host = "", Item = "", Title = "", Fix = "", Ref = ""; }   // Level : bad, warn, info, ok

    // ===================================================================== périmètre autorisé (garde-fou)
    static class Scope
    {
        public class Range { public uint A, B; public string Src = ""; public long Count { get { return (long)B - A + 1; } } }

        public static uint Ip(string s)
        {
            IPAddress a; if (!IPAddress.TryParse(s, out a) || a.AddressFamily != AddressFamily.InterNetwork) throw new FormatException("adresse IPv4 invalide : " + s);
            var b = a.GetAddressBytes(); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }
        public static string Str(uint v) { return (v >> 24) + "." + ((v >> 16) & 255) + "." + ((v >> 8) & 255) + "." + (v & 255); }

        // 192.168.1.10 · 192.168.1.0/24 · 192.168.1.10-50 · 192.168.1.10-192.168.1.60
        public static Range Parse(string t)
        {
            t = (t ?? "").Trim(); var m = Regex.Match(t, @"^(\d{1,3}(?:\.\d{1,3}){3})/(\d{1,2})$");
            if (m.Success)
            {
                int p = int.Parse(m.Groups[2].Value); if (p > 32) throw new FormatException("masque invalide : " + t);
                uint ip = Ip(m.Groups[1].Value), mask = p == 0 ? 0 : uint.MaxValue << (32 - p);
                return new Range { A = ip & mask, B = (ip & mask) | ~mask, Src = t };
            }
            m = Regex.Match(t, @"^(\d{1,3}(?:\.\d{1,3}){3})-(\d{1,3}(?:\.\d{1,3}){3}|\d{1,3})$");
            if (m.Success)
            {
                uint a = Ip(m.Groups[1].Value); var e = m.Groups[2].Value;
                uint b = e.Contains(".") ? Ip(e) : (a & 0xFFFFFF00u) | uint.Parse(e);
                if (!e.Contains(".") && uint.Parse(e) > 255) throw new FormatException("plage invalide : " + t);
                if (b < a) throw new FormatException("plage à l'envers : " + t);
                return new Range { A = a, B = b, Src = t };
            }
            if (Regex.IsMatch(t, @"^\d{1,3}(\.\d{1,3}){3}$")) { var v = Ip(t); return new Range { A = v, B = v, Src = t }; }
            throw new FormatException("cible non reconnue : " + t + " (IP, IP/masque ou plage)");
        }

        public static List<Range> ParseList(string text)
        {
            return (text ?? "").Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith("#")).Select(Parse).ToList();
        }

        public static bool Private(uint v)
        {
            return (v >> 24) == 10 || (v >> 20) == (172 << 4 | 1) || (v >> 16) == (192 << 8 | 168) || (v >> 24) == 127 || (v >> 16) == (169 << 8 | 254) || (v >> 22) == (100 << 2 | 1);
        }
        public static bool Private(Range r) { return Private(r.A) && Private(r.B); }

        // cibles : IP, réseaux, plages ou noms (résolus ICI : Nmap reçoit les adresses vérifiées, pas le nom)
        public static List<Range> Targets(string text, Func<string, IPAddress[]> resolve)
        {
            var l = new List<Range>();
            foreach (var t in (text ?? "").Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Regex.IsMatch(t, @"^\d")) { l.Add(Parse(t)); continue; }
                if (!Regex.IsMatch(t, @"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}$")) throw new FormatException("cible invalide : " + t);
                var ips = resolve(t).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
                if (ips.Count == 0) throw new FormatException("nom introuvable : " + t);
                l.AddRange(ips.Select(a => { var v = Ip(a.ToString()); return new Range { A = v, B = v, Src = t }; }));
            }
            if (l.Count == 0) throw new FormatException("aucune cible");
            return l;
        }

        // null = autorisé ; sinon la raison du refus
        public static string Check(Client c, List<Range> targets, DateTime now)
        {
            if (c == null) return "choisis le client concerné";
            if (c.CyberAuth.Year < 2000) return "aucune autorisation écrite enregistrée pour ce client (bouton « Périmètre et autorisation »)";
            if ((now - c.CyberAuth).TotalDays > 365) return "autorisation du " + c.CyberAuth.ToString("dd/MM/yyyy") + " expirée (plus d'un an) : à renouveler";
            List<Range> scope; try { scope = ParseList(c.CyberScope); } catch (Exception ex) { return "périmètre du client invalide : " + ex.Message; }
            if (scope.Count == 0) return "périmètre du client vide";
            if (targets.Sum(t => t.Count) > 65536) return "trop d'adresses (plus de 65 536) : découpe le scan";
            foreach (var t in targets)
                if (!scope.Any(s => t.A >= s.A && t.B <= s.B)) return Str(t.A) + (t.B != t.A ? "-" + Str(t.B) : "") + " (" + t.Src + ") est HORS du périmètre autorisé de " + c.Name;
            return null;
        }

        // réseaux IPv4 du PC (préremplissage du périmètre)
        public static List<string> LocalNets()
        {
            var l = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                    foreach (var u in ni.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && x.IPv4Mask != null))
                    {
                        uint ip = Ip(u.Address.ToString()), mask = Ip(u.IPv4Mask.ToString()); int p = 0; for (uint m = mask; m != 0; m <<= 1) p++;
                        if (p >= 16 && p < 32 && Private(ip)) l.Add(Str(ip & mask) + "/" + p);
                    }
            }
            catch { }
            return l.Distinct().ToList();
        }
    }

    // ===================================================================== Nmap : profils fixes, exécution, lecture du XML
    static class Nmap
    {
        public class Profile { public string Id, Name, Hint, Args; public bool Ports = true; }
        const string RiskPorts = "T:21,22,23,25,53,80,110,111,135,139,143,389,443,445,465,587,636,993,995,1433,1521,2049,3306,3389,5432,5900,5985,5986,6379,8000,8080,8443,9200,27017,U:53,161,1900";

        public static readonly Profile[] Profiles =
        {
            new Profile { Id = "decouverte", Name = "Découverte des machines", Hint = "qui est sur le réseau (ARP/ping), sans scanner les ports", Args = "-sn -T4", Ports = false },
            new Profile { Id = "ports", Name = "Ports courants + versions", Hint = "1 000 ports TCP les plus courants, versions des services", Args = "-sS -sV --version-light -T4 --top-ports 1000" },
            new Profile { Id = "risque", Name = "Services à risque", Hint = "FTP, Telnet, SMB, RDP, VNC, bases, SNMP, UPnP, admin web + contrôles sûrs", Args = "-sS -sU -sV -T4 -p " + RiskPorts + " --script ftp-anon,snmp-sysdescr,vnc-info,rdp-enum-encryption,http-title,smb-protocols,smb2-security-mode,smb-security-mode" },
            new Profile { Id = "vuln", Name = "Vulnérabilités connues (sûres)", Hint = "scripts Nmap à la fois « vuln » et « safe » : sans risque pour la cible", Args = "-sS -sV -T4 --top-ports 1000 --script \"vuln and safe\"" },
            new Profile { Id = "tls", Name = "TLS et certificats", Hint = "protocoles (TLS 1.0/1.1), chiffrements, expiration des certificats", Args = "-sS -sV -T4 -p 443,465,636,993,995,3389,5986,8443 --script ssl-enum-ciphers,ssl-cert" },
            new Profile { Id = "smb", Name = "Partages Windows (SMB)", Hint = "SMBv1, signature des échanges", Args = "-sS -T4 -p 139,445 --script smb-protocols,smb2-security-mode,smb-security-mode" },
            new Profile { Id = "complet", Name = "Audit complet (long)", Hint = "les 65 535 ports TCP, versions, système d'exploitation", Args = "-sS -sV -O -T4 -p-" },
        };

        // scripts autorisés ; tout le reste (dont les catégories offensives) est refusé
        static readonly HashSet<string> AllowedScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "ftp-anon", "snmp-sysdescr", "vnc-info", "rdp-enum-encryption", "http-title", "smb-protocols", "smb2-security-mode", "smb-security-mode", "ssl-cert", "ssl-enum-ciphers", "vuln and safe" };

        // garde-fou testable : null si les arguments sont acceptables
        public static string CheckArgs(string args)
        {
            if (Regex.IsMatch(args, @"--script-args|--script-updatedb|--datadir|--servicedb|--versiondb|--resume|-iL\b|--stylesheet|--privileged|-oN|-oG|-oS|-oA", RegexOptions.IgnoreCase)) return "option interdite";
            foreach (Match m in Regex.Matches(args, @"--script[ =](""[^""]*""|\S+)"))
            {
                var v = m.Groups[1].Value.Trim('"');
                if (AllowedScripts.Contains(v)) continue;
                foreach (var s in v.Split(','))
                    if (!AllowedScripts.Contains(s.Trim())) return "script Nmap non autorisé : " + s.Trim();
            }
            return null;
        }

        public static string Exe
        {
            get
            {
                var p = Prefs.Ini.Get("NmapPath", "");
                if (p.Length > 0 && File.Exists(p)) return p;
                foreach (var sf in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
                {
                    var root = Environment.GetFolderPath(sf); if (root.Length == 0) continue;
                    var f = Path.Combine(root, @"Nmap\nmap.exe"); if (File.Exists(f)) return f;
                }
                return null;
            }
        }
        public static string Version()
        {
            var exe = Exe; if (exe == null) return null;
            try { string o; Proc.Run(exe, "--version", 15000, out o); var m = Regex.Match(o, @"Nmap version ([\d.]+\w*)"); return m.Success ? m.Groups[1].Value : "?"; } catch { return "?"; }
        }

        public static string ScansDir(Client c) { var d = Path.Combine(Store.Dir, "scans", Regex.Replace(c.Id, @"[^\w\-]", "")); Directory.CreateDirectory(d); return d; }

        // lance Nmap sur des adresses déjà vérifiées ; renvoie le chemin du XML
        public static string Run(Client c, Profile p, List<Scope.Range> targets, Action<string, string> log)
        {
            var exe = Exe; if (exe == null) throw new Exception("Nmap n'est pas installé sur ce PC (nmap.org/download, avec Npcap)");
            var bad = CheckArgs(p.Args); if (bad != null) throw new Exception("profil refusé : " + bad);
            var xml = Path.Combine(ScansDir(c), DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + p.Id + ".xml");
            var tgt = string.Join(" ", targets.Select(t => t.A == t.B ? Scope.Str(t.A) : Scope.Str(t.A) + "-" + Scope.Str(t.B)).Select(Expand));
            var args = p.Args + " --stats-every 15s -oX \"" + xml + "\" " + tgt;
            Audit(c, p, tgt, "début");
            log("nmap " + p.Args + " " + tgt, "muted");
            string o; int rc;
            try
            {
                rc = Proc.Run(exe, args, 6 * 3600 * 1000, Encoding.UTF8, line =>
                {
                    var m = Regex.Match(line, @"About ([\d.]+)% done");
                    if (m.Success) { var j = Job.Current; double pc; if (j != null && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out pc)) j.Progress = pc / 100; }
                    else if (Regex.IsMatch(line, @"^(Nmap scan report|Discovered open port|Host is up|WARNING|QUITTING)")) log(line.Trim(), line.StartsWith("WARNING") || line.StartsWith("QUITTING") ? "warn" : "text");
                }, out o);
            }
            catch (OperationCanceledException) { Audit(c, p, tgt, "annulé"); throw; }
            Audit(c, p, tgt, "code " + rc);
            if (!File.Exists(xml)) throw new Exception("Nmap n'a produit aucun résultat (code " + rc + ") : " + o.Trim().Split('\n').LastOrDefault());
            return xml;
        }

        // une plage a.b.c.d-e.f.g.h n'est pas une syntaxe Nmap : conversion en blocs CIDR exacts
        static string Expand(string t)
        {
            if (!t.Contains("-")) return t;
            var p = t.Split('-'); uint a = Scope.Ip(p[0]), b = Scope.Ip(p[1]); var l = new List<string>();
            while (a <= b)
            {
                int bits = 0; while (bits < 32 && (a & ((1u << (bits + 1)) - 1)) == 0 && a + ((1u << (bits + 1)) - 1) <= b && bits + 1 < 32) bits++;
                l.Add(Scope.Str(a) + "/" + (32 - bits)); var next = a + (1u << bits); if (next == 0) break; a = next;
            }
            return string.Join(" ", l);
        }

        public static void Audit(Client c, Profile p, string targets, string state)
        {
            try { File.AppendAllText(Path.Combine(Store.Dir, "cyber-audit.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + Environment.UserName + "@" + Environment.MachineName + " | " + c.Name + " | " + p.Id + " | " + targets + " | " + state + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        // ---------------- XML
        public class Port { public string Proto = "", State = "", Service = "", Product = "", Version = ""; public int Num; public Dictionary<string, string> Scripts = new Dictionary<string, string>(); }
        public class Host { public string Ip = "", Mac = "", Vendor = "", Name = "", Os = ""; public List<Port> Ports = new List<Port>(); public Dictionary<string, string> Scripts = new Dictionary<string, string>(); }

        static string A(XmlNode n, string a) { var x = n == null || n.Attributes == null ? null : n.Attributes[a]; return x == null ? "" : x.Value; }

        public static List<Host> Parse(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null })) doc.Load(r);
            var l = new List<Host>();
            foreach (XmlNode h in doc.SelectNodes("/nmaprun/host"))
            {
                if (A(h.SelectSingleNode("status"), "state") == "down") continue;
                var host = new Host();
                foreach (XmlNode a in h.SelectNodes("address")) { if (A(a, "addrtype") == "ipv4") host.Ip = A(a, "addr"); else if (A(a, "addrtype") == "mac") { host.Mac = A(a, "addr"); host.Vendor = A(a, "vendor"); } }
                host.Name = A(h.SelectSingleNode("hostnames/hostname"), "name");
                host.Os = A(h.SelectSingleNode("os/osmatch"), "name");
                foreach (XmlNode p in h.SelectNodes("ports/port"))
                {
                    var pt = new Port { Proto = A(p, "protocol"), State = A(p.SelectSingleNode("state"), "state") };
                    int n; int.TryParse(A(p, "portid"), out n); pt.Num = n;
                    var s = p.SelectSingleNode("service"); pt.Service = A(s, "name"); pt.Product = A(s, "product"); pt.Version = A(s, "version");
                    foreach (XmlNode sc in p.SelectNodes("script")) pt.Scripts[A(sc, "id")] = A(sc, "output");
                    host.Ports.Add(pt);
                }
                foreach (XmlNode sc in h.SelectNodes("hostscript/script")) host.Scripts[A(sc, "id")] = A(sc, "output");
                l.Add(host);
            }
            return l;
        }
        public static bool Open(Port p) { return p.State == "open" || (p.Proto == "udp" && p.State == "open|filtered" && p.Scripts.Count > 0); }

        // nouveaux ports ouverts / ports refermés depuis le scan précédent du même profil
        public static List<CyFinding> Diff(List<Host> before, List<Host> now)
        {
            Func<List<Host>, HashSet<string>> keys = hs => new HashSet<string>(hs.SelectMany(h => h.Ports.Where(Open).Select(p => h.Ip + " " + p.Num + "/" + p.Proto)));
            var a = keys(before); var b = keys(now); var l = new List<CyFinding>();
            foreach (var k in b.Except(a).OrderBy(x => x)) { var s = k.Split(' '); l.Add(new CyFinding { Level = "warn", Host = s[0], Item = s[1], Title = "NOUVEAU port ouvert depuis le scan précédent", Fix = "Vérifier qu'il est voulu ; sinon le fermer (pare-feu, service).", Ref = "CIS 4.4" }); }
            foreach (var k in a.Except(b).OrderBy(x => x)) { var s = k.Split(' '); l.Add(new CyFinding { Level = "info", Host = s[0], Item = s[1], Title = "Port refermé depuis le scan précédent", Fix = "", Ref = "" }); }
            return l;
        }
        public static string Previous(string xmlFile)
        {
            var dir = Path.GetDirectoryName(xmlFile); var prof = Regex.Match(Path.GetFileName(xmlFile), @"_(\w+)\.xml$").Groups[1].Value;
            return Directory.GetFiles(dir, "*_" + prof + ".xml").Where(f => string.CompareOrdinal(Path.GetFileName(f), Path.GetFileName(xmlFile)) < 0).OrderBy(f => f).LastOrDefault();
        }
    }

    // ===================================================================== règles de risque (ANSSI, CIS Controls v8)
    static class Risk
    {
        static CyFinding F(string lvl, Nmap.Host h, string item, string title, string fix, string rf) { return new CyFinding { Level = lvl, Host = h.Ip + (h.Name.Length > 0 ? " (" + h.Name + ")" : ""), Item = item, Title = title, Fix = fix, Ref = rf }; }

        public static List<CyFinding> Evaluate(List<Nmap.Host> hosts, DateTime now)
        {
            var l = new List<CyFinding>();
            foreach (var h in hosts)
            {
                bool pub = !Scope.Private(SafeIp(h.Ip)); string scope = pub ? " ACCESSIBLE DEPUIS INTERNET" : "";
                foreach (var p in h.Ports.Where(Nmap.Open))
                {
                    var it = p.Num + "/" + p.Proto + " " + p.Service + (p.Product.Length > 0 ? " (" + (p.Product + " " + p.Version).Trim() + ")" : "");
                    string s = null;
                    switch (p.Proto == "tcp" ? p.Num : -p.Num)
                    {
                        case 23: l.Add(F("bad", h, it, "Telnet ouvert : identifiants transmis en clair" + scope, "Désactiver Telnet, administrer en SSH.", "ANSSI R4 · CIS 4.8")); break;
                        case 21:
                            var anon = p.Scripts.TryGetValue("ftp-anon", out s) && s.IndexOf("Anonymous FTP login allowed", StringComparison.OrdinalIgnoreCase) >= 0;
                            l.Add(F(anon ? "bad" : "warn", h, it, anon ? "FTP ANONYME autorisé" + scope : "FTP : identifiants en clair" + scope, anon ? "Interdire la connexion anonyme ; passer en SFTP/FTPS." : "Remplacer par SFTP ou FTPS.", "CIS 3.10")); break;
                        case 3389:
                            var nla = p.Scripts.TryGetValue("rdp-enum-encryption", out s) ? s : "";
                            var noNla = nla.Length > 0 && nla.IndexOf("CredSSP", StringComparison.OrdinalIgnoreCase) < 0 && nla.IndexOf("NLA", StringComparison.OrdinalIgnoreCase) < 0;
                            l.Add(F(pub || noNla ? "bad" : "info", h, it, pub ? "Bureau à distance exposé sur Internet (cible n°1 des rançongiciels)" : noNla ? "Bureau à distance SANS authentification NLA" : "Bureau à distance actif sur le réseau local", pub ? "Fermer le port, passer par un VPN ; NLA + verrouillage de compte." : noNla ? "Activer l'authentification au niveau du réseau (NLA)." : "Limiter aux postes d'administration (pare-feu).", "ANSSI R28 · MITRE T1021.001")); break;
                        case 5900: case 5901:
                            var vnc = p.Scripts.TryGetValue("vnc-info", out s) ? s : "";
                            l.Add(F(Regex.IsMatch(vnc, @"None|no auth", RegexOptions.IgnoreCase) ? "bad" : "warn", h, it, Regex.IsMatch(vnc, @"None|no auth", RegexOptions.IgnoreCase) ? "VNC SANS mot de passe" + scope : "VNC ouvert" + scope, "Désinstaller ou protéger par mot de passe fort + VPN ; préférer une prise en main encadrée (TeamViewer, MeshCentral).", "CIS 4.8")); break;
                        case 445: case 139:
                            if (pub) l.Add(F("bad", h, it, "Partage Windows (SMB) exposé sur Internet", "Bloquer 139/445 sur la box/le pare-feu.", "ANSSI · CVE-2017-0144 (WannaCry)")); break;
                        case 1433: case 1521: case 3306: case 5432: case 27017: case 6379: case 9200:
                            l.Add(F(pub ? "bad" : "warn", h, it, "Base de données joignable directement" + scope, "Restreindre l'accès aux seuls serveurs applicatifs (pare-feu), désactiver l'écoute réseau si inutile.", "CIS 4.4")); break;
                        case 5985: l.Add(F(pub ? "bad" : "info", h, it, "WinRM en HTTP" + scope, "Utiliser WinRM HTTPS (5986) ou le désactiver si inutile.", "CIS 4.8")); break;
                        case -161:
                            var snmp = p.Scripts.TryGetValue("snmp-sysdescr", out s) && s.Trim().Length > 0;
                            l.Add(F(snmp ? "bad" : "warn", h, it, snmp ? "SNMP répond à la communauté « public » (" + Short(s) + ")" + scope : "SNMP ouvert" + scope, "Changer la communauté, passer en SNMPv3, filtrer l'accès.", "CIS 4.8")); break;
                        case -1900: l.Add(F(pub ? "bad" : "warn", h, it, "UPnP actif" + scope, "Désactiver l'UPnP sur la box et les équipements (ouvertures de ports automatiques).", "ANSSI")); break;
                        case 80: case 8080: case 8000:
                            if (pub || Regex.IsMatch(p.Scripts.TryGetValue("http-title", out s) ? s : "", @"login|admin|router|nas|dsm|camera|nvr|dvr|routeur|connexion", RegexOptions.IgnoreCase))
                                l.Add(F(pub ? "warn" : "info", h, it, "Interface web en HTTP non chiffré" + (s != null && s.Length > 0 ? " : « " + Short(s) + " »" : "") + scope, "Passer en HTTPS, changer les identifiants par défaut, ne pas exposer l'administration.", "CIS 4.6")); break;
                    }
                    if (p.Scripts.TryGetValue("ssl-enum-ciphers", out s))
                    {
                        if (Regex.IsMatch(s, @"SSLv[23]")) l.Add(F("bad", h, it, "SSL v2/v3 encore accepté", "Désactiver SSL v2/v3 et TLS 1.0/1.1.", "ANSSI TLS R3"));
                        else if (Regex.IsMatch(s, @"TLSv1\.[01]:")) l.Add(F("warn", h, it, "TLS 1.0/1.1 encore accepté", "N'accepter que TLS 1.2 et 1.3.", "ANSSI TLS R3"));
                        var g = Regex.Match(s, @"least strength: ([A-F])"); if (g.Success && "CDEF".Contains(g.Groups[1].Value)) l.Add(F("warn", h, it, "Chiffrements faibles (note " + g.Groups[1].Value + ")", "Retirer les suites faibles (3DES, RC4, CBC-SHA1…).", "ANSSI TLS R7"));
                    }
                    if (p.Scripts.TryGetValue("ssl-cert", out s))
                    {
                        var m = Regex.Match(s, @"Not valid after:\s*(\d{4}-\d{2}-\d{2})"); DateTime na;
                        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out na))
                        {
                            if (na < now) l.Add(F("bad", h, it, "Certificat EXPIRÉ le " + na.ToString("dd/MM/yyyy"), "Renouveler le certificat.", "CIS 3.10"));
                            else if ((na - now).TotalDays < 30) l.Add(F("warn", h, it, "Certificat expire le " + na.ToString("dd/MM/yyyy"), "Planifier le renouvellement.", "CIS 3.10"));
                        }
                        var sub = Regex.Match(s, @"Subject: (.+)"); var iss = Regex.Match(s, @"Issuer: (.+)");
                        if (sub.Success && iss.Success && sub.Groups[1].Value.Trim() == iss.Groups[1].Value.Trim()) l.Add(F("info", h, it, "Certificat auto-signé", "Normal sur un équipement interne ; à remplacer s'il est exposé aux utilisateurs.", ""));
                    }
                    foreach (var sc in p.Scripts.Where(x => x.Value.IndexOf("VULNERABLE", StringComparison.Ordinal) >= 0 && x.Value.IndexOf("NOT VULNERABLE", StringComparison.Ordinal) < 0))
                        l.Add(F("bad", h, it, "Vulnérabilité détectée (" + sc.Key + ")" + Cves(sc.Value), "Appliquer les mises à jour de l'éditeur ; isoler en attendant.", "Nmap " + sc.Key));
                }
                string hs;
                if (h.Scripts.TryGetValue("smb-protocols", out hs) && hs.IndexOf("SMBv1", StringComparison.OrdinalIgnoreCase) >= 0)
                    l.Add(F("bad", h, "445/tcp smb", "SMBv1 activé (WannaCry, NotPetya)", "Désactiver SMBv1 : Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol (NAS : SMB2 minimum).", "ANSSI · CVE-2017-0144"));
                if ((h.Scripts.TryGetValue("smb2-security-mode", out hs) && hs.IndexOf("not required", StringComparison.OrdinalIgnoreCase) >= 0) || (h.Scripts.TryGetValue("smb-security-mode", out hs) && Regex.IsMatch(hs, @"message_signing: disabled")))
                    l.Add(F("warn", h, "445/tcp smb", "Signature SMB non exigée (relais NTLM possible)", "Exiger la signature SMB (GPO « Microsoft network server: Digitally sign communications (always) »).", "ANSSI AD · MITRE T1557.001"));
                foreach (var sc in h.Scripts.Where(x => x.Value.IndexOf("VULNERABLE", StringComparison.Ordinal) >= 0 && x.Value.IndexOf("NOT VULNERABLE", StringComparison.Ordinal) < 0))
                    l.Add(F("bad", h, "hôte", "Vulnérabilité détectée (" + sc.Key + ")" + Cves(sc.Value), "Appliquer les mises à jour de l'éditeur ; isoler en attendant.", "Nmap " + sc.Key));
                if (Regex.IsMatch(h.Os, @"Windows (XP|Vista|7|8|Server 200[038])", RegexOptions.IgnoreCase)) l.Add(F("bad", h, "système", "Système plus maintenu détecté : " + h.Os, "Remplacer ou isoler du réseau.", "CIS 2.2"));
            }
            return l.OrderBy(x => x.Level == "bad" ? 0 : x.Level == "warn" ? 1 : 2).ThenBy(x => x.Host).ToList();
        }
        static uint SafeIp(string ip) { try { return Scope.Ip(ip); } catch { return 0x0A000001; } }
        static string Cves(string s) { var c = Regex.Matches(s, @"CVE-\d{4}-\d{4,7}").Cast<Match>().Select(m => m.Value).Distinct().Take(4).ToList(); return c.Count > 0 ? " : " + string.Join(", ", c) : ""; }
        static string Short(string s) { s = Regex.Replace(s ?? "", @"\s+", " ").Trim(); return s.Length > 70 ? s.Substring(0, 70) + "…" : s; }
    }

    // ===================================================================== cyber du poste (lecture seule)
    static class Poste
    {
        const string Fn = "function F($s){([string]$s) -replace '[\\r\\n|]',' '};";

        public const string HuntScript = Fn +
            "$d=(Get-Date).AddDays(-7);" +
            "function Q($h,$n){try{Get-WinEvent -FilterHashtable $h -MaxEvents $n -EA Stop}catch{@()}};" +
            "Q @{LogName='Security';Id=4625;StartTime=$d} 20000|group {[string]$_.Properties[5].Value+'@'+[string]$_.Properties[19].Value}|sort Count -Desc|select -First 15|%{'H|4625|'+$_.Count+'|'+(F $_.Name)+'|'+$_.Group[0].TimeCreated.ToString('s')};" +
            "Q @{LogName='Security';Id=4720;StartTime=$d} 200|%{'H|4720|1|'+(F $_.Properties[0].Value)+' créé par '+(F $_.Properties[4].Value)+'|'+$_.TimeCreated.ToString('s')};" +
            "Q @{LogName='Security';Id=4732;StartTime=$d} 200|?{[string]$_.Properties[4].Value -eq 'S-1-5-32-544'}|%{'H|4732|1|'+(F $_.Properties[0].Value)+' '+(F $_.Properties[1].Value)+' ajouté aux Administrateurs par '+(F $_.Properties[6].Value)+'|'+$_.TimeCreated.ToString('s')};" +
            "Q @{LogName='Security';Id=1102;StartTime=$d} 50|%{'H|1102|1|journal Sécurité effacé par '+(F $_.Properties[1].Value)+'|'+$_.TimeCreated.ToString('s')};" +
            "Q @{LogName='Security';Id=4624;StartTime=$d} 20000|?{[string]$_.Properties[8].Value -eq '10'}|group {[string]$_.Properties[5].Value+'@'+[string]$_.Properties[18].Value}|%{'H|4624|'+$_.Count+'|'+(F $_.Name)+'|'+$_.Group[0].TimeCreated.ToString('s')};" +
            "Q @{LogName='Security';Id=4698;StartTime=$d} 200|%{'H|4698|1|'+(F $_.Properties[4].Value)+' par '+(F $_.Properties[1].Value)+'|'+$_.TimeCreated.ToString('s')};" +
            "Q @{LogName='System';Id=7045;StartTime=$d} 200|%{'H|7045|1|'+(F $_.Properties[0].Value)+' → '+(F $_.Properties[1].Value)+'|'+$_.TimeCreated.ToString('s')};" +
            "Q @{LogName='Microsoft-Windows-PowerShell/Operational';Id=4104;StartTime=$d} 3000|?{$_.Level -eq 3 -or [string]$_.Properties[2].Value -match 'FromBase64String|DownloadString|DownloadFile|Net\\.WebClient|Invoke-Expression|IEX\\s*\\(|-enc(odedcommand)?\\s|AmsiUtils|Mimikatz|Invoke-Shellcode|-ExecutionPolicy\\s+Bypass'}|select -First 30|%{$t=[string]$_.Properties[2].Value;'H|4104|1|'+(F $t.Substring(0,[Math]::Min(160,$t.Length)))+'|'+$_.TimeCreated.ToString('s')};" +
            "Q @{LogName='Microsoft-Windows-Windows Defender/Operational';Id=1116,1117;StartTime=$d} 200|%{'H|'+$_.Id+'|1|'+(F $_.Properties[7].Value)+'|'+$_.TimeCreated.ToString('s')};" +
            "'H|END|0||'";

        public const string PersistScript = Fn +
            "function P($c){$c=[Environment]::ExpandEnvironmentVariables(([string]$c).Trim());if($c -match '^\"([^\"]+)\"'){return $matches[1]};if($c -match '^(.+?\\.(exe|dll|bat|cmd|ps1|vbs|js|com|scr|hta))\\b'){return $matches[1]};return ($c -split ' ')[0]};" +
            "$sc=@{};function S($p){if(-not $p -or -not (Test-Path -LiteralPath $p -PathType Leaf -EA SilentlyContinue)){return 'Absent|'};if(-not $sc.ContainsKey($p)){$s=Get-AuthenticodeSignature -LiteralPath $p -EA SilentlyContinue;$sc[$p]=[string]$s.Status+'|'+(F ([string]$s.SignerCertificate.Subject -replace '^CN=\"?([^,\"]+).*','$1'))};return $sc[$p]};" +
            "function O($t,$n,$c){$x=P $c;'P|'+$t+'|'+(F $n)+'|'+(F $c)+'|'+(F $x)+'|'+(S $x)};" +
            "$roots=@('HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion','HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion')+@(Get-ChildItem Registry::HKEY_USERS -EA SilentlyContinue|?{$_.PSChildName -match '^S-1-5-21-[\\d-]+$'}|%{'Registry::HKEY_USERS\\'+$_.PSChildName+'\\Software\\Microsoft\\Windows\\CurrentVersion'});" +
            "foreach($r in $roots){foreach($k in 'Run','RunOnce'){$p=Get-ItemProperty ($r+'\\'+$k) -EA SilentlyContinue;if($p){$p.PSObject.Properties|?{$_.Name -notlike 'PS*'}|%{O ('Registre '+$k) $_.Name $_.Value}}}};" +
            "$sh=New-Object -ComObject WScript.Shell;@(\"$env:ProgramData\\Microsoft\\Windows\\Start Menu\\Programs\\Startup\")+@(Get-ChildItem C:\\Users -Directory -EA SilentlyContinue|%{$_.FullName+'\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\Startup'})|?{Test-Path $_}|%{Get-ChildItem $_ -File -EA SilentlyContinue}|?{$_.Name -ne 'desktop.ini'}|%{$t=$(if($_.Extension -eq '.lnk'){$l=$sh.CreateShortcut($_.FullName);'\"'+$l.TargetPath+'\" '+$l.Arguments}else{$_.FullName});O 'Dossier Démarrage' $_.Name $t};" +
            "Get-ScheduledTask -EA SilentlyContinue|?{$_.TaskPath -notlike '\\Microsoft\\*' -and $_.State -ne 'Disabled'}|%{$n=$_.TaskPath+$_.TaskName;$_.Actions|?{$_.Execute}|%{O 'Tâche planifiée' $n ('\"'+$_.Execute+'\" '+$_.Arguments)}};" +
            "Get-CimInstance Win32_Service -EA SilentlyContinue|?{$_.StartMode -eq 'Auto' -and $_.PathName}|%{O 'Service' ($_.Name+' ('+$_.DisplayName+')') $_.PathName};" +
            "Get-CimInstance -Namespace root\\subscription -ClassName __EventConsumer -EA SilentlyContinue|%{'P|WMI|'+(F $_.Name)+'|'+(F ([string]$_.CommandLineTemplate+[string]$_.ScriptText))+'||WMI|'};" +
            "'P|END|||||'";

        public const string ConnScript = Fn +
            "$sg=@{};Get-NetTCPConnection -State Listen,Established -EA SilentlyContinue|%{$pr=Get-Process -Id $_.OwningProcess -EA SilentlyContinue;$p=[string]$pr.Path;" +
            "if($p -and -not $sg.ContainsKey($p)){$sg[$p]=[string](Get-AuthenticodeSignature -LiteralPath $p -EA SilentlyContinue).Status};" +
            "'C|'+$_.State+'|'+$_.LocalAddress+'|'+$_.LocalPort+'|'+$_.RemoteAddress+'|'+$_.RemotePort+'|'+$_.OwningProcess+'|'+(F $pr.ProcessName)+'|'+(F $p)+'|'+$(if($p){$sg[$p]}else{''})};'C|END|||||||||'";

        static List<string[]> Rows(string output, string tag, int n)
        {
            return (output ?? "").Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.StartsWith(tag + "|")).Select(x => { var p = x.Split('|'); return p.Length >= n ? p : p.Concat(Enumerable.Repeat("", n - p.Length)).ToArray(); }).ToList();
        }
        public static bool Complete(string output, string tag) { return Rows(output, tag, 2).Any(r => r[1] == "END"); }

        static readonly Dictionary<string, string[]> Hunt = new Dictionary<string, string[]>
        {   // id → niveau, titre, MITRE ATT&CK, recommandation
            { "4625", new[] { "warn", "Échecs de connexion", "T1110 Force brute", "Vérifier la source ; verrouillage de compte, bloquer l'IP, mots de passe forts." } },
            { "4720", new[] { "warn", "Compte utilisateur créé", "T1136.001 Création de compte local", "Confirmer que la création est légitime." } },
            { "4732", new[] { "bad", "Ajout au groupe Administrateurs", "T1098 Manipulation de compte", "Confirmer ; sinon retirer le compte et changer les mots de passe." } },
            { "1102", new[] { "bad", "Journal Sécurité effacé", "T1070.001 Effacement des journaux", "Fort indicateur de compromission : investiguer." } },
            { "4624", new[] { "info", "Connexions Bureau à distance", "T1021.001 RDP", "Vérifier que chaque source est connue." } },
            { "4698", new[] { "warn", "Tâche planifiée créée", "T1053.005 Tâche planifiée", "Vérifier la tâche (Persistance)." } },
            { "7045", new[] { "warn", "Service installé", "T1543.003 Service Windows", "Vérifier l'éditeur et le chemin du service." } },
            { "4104", new[] { "bad", "PowerShell suspect", "T1059.001 PowerShell", "Analyser le script ; isoler le poste si malveillant." } },
            { "1116", new[] { "bad", "Menace détectée par Defender", "Détection antivirus", "Vérifier la quarantaine et l'origine." } },
            { "1117", new[] { "info", "Menace traitée par Defender", "Détection antivirus", "Vérifier l'origine de l'infection." } },
        };

        public static List<CyFinding> EvalHunt(string output)
        {
            var l = new List<CyFinding>();
            foreach (var r in Rows(output, "H", 5))
            {
                string[] d; if (!Hunt.TryGetValue(r[1], out d)) continue;
                int n; int.TryParse(r[2], out n); var lvl = d[0];
                if (r[1] == "4625") { if (n < 10) continue; lvl = n >= 50 ? "bad" : "warn"; }
                if (r[1] == "4624" && Regex.IsMatch(r[3], @"@(\d+\.){3}\d+$")) { var ip = r[3].Substring(r[3].LastIndexOf('@') + 1); try { if (!Scope.Private(Scope.Ip(ip))) lvl = "bad"; } catch { } }
                l.Add(new CyFinding { Level = lvl, Host = Environment.MachineName, Item = "événement " + r[1] + (n > 1 ? " ×" + n : ""), Title = d[1] + " : " + r[3] + (r[4].Length > 0 ? " (" + r[4].Replace("T", " ") + ")" : ""), Fix = d[3], Ref = "MITRE " + d[2] });
            }
            return l.OrderBy(x => x.Level == "bad" ? 0 : x.Level == "warn" ? 1 : 2).ToList();
        }

        static readonly Regex UserDir = new Regex(@"\\(AppData|Temp|Downloads|Téléchargements|Users\\Public|ProgramData\\[^\\]+\.exe$|Windows\\Temp)\\?", RegexOptions.IgnoreCase);
        static readonly Regex Lolbin = new Regex(@"powershell(\.exe)?""?\s.*(-e(nc|ncodedcommand)?\s|-w(indowstyle)?\s+h(idden)?|downloadstring|iex)|mshta(\.exe)?""?\s|wscript|cscript|rundll32(\.exe)?""?\s+[^,]*javascript|regsvr32.+/i:http|certutil.+-urlcache|bitsadmin.+/transfer", RegexOptions.IgnoreCase);

        public static List<CyFinding> EvalPersist(string output, out int total)
        {
            var l = new List<CyFinding>(); var rows = Rows(output, "P", 7).Where(r => r[1] != "END").ToList(); total = rows.Count;
            foreach (var r in rows)
            {
                string type = r[1], name = r[2], cmd = r[3], path = r[4], sig = r[5], signer = r[6];
                var item = type + " : " + name;
                if (type == "WMI") { l.Add(new CyFinding { Level = "bad", Host = Environment.MachineName, Item = item, Title = "Abonnement WMI permanent : " + Cut(cmd), Fix = "Rare en usage légitime : identifier l'auteur ; supprimer s'il est inconnu.", Ref = "MITRE T1546.003" }); continue; }
                if (Lolbin.IsMatch(cmd)) { l.Add(new CyFinding { Level = "bad", Host = Environment.MachineName, Item = item, Title = "Lancement par un interpréteur détourné : " + Cut(cmd), Fix = "Technique classique des logiciels malveillants : analyser puis supprimer.", Ref = "MITRE T1059 / T1218" }); continue; }
                if (sig == "Absent") { if (type != "Service") l.Add(new CyFinding { Level = "warn", Host = Environment.MachineName, Item = item, Title = "Programme introuvable (entrée orpheline) : " + Cut(path), Fix = "Supprimer l'entrée si le logiciel a été désinstallé.", Ref = "" }); continue; }
                if (sig != "Valid")
                    l.Add(new CyFinding { Level = UserDir.IsMatch(path) ? "bad" : "warn", Host = Environment.MachineName, Item = item, Title = "Non signé (" + sig + ")" + (UserDir.IsMatch(path) ? " dans un dossier utilisateur" : "") + " : " + Cut(path), Fix = UserDir.IsMatch(path) ? "Emplacement typique d'un logiciel malveillant : vérifier sur VirusTotal (bouton « Fichier suspect »)." : "Vérifier l'éditeur et l'utilité.", Ref = type == "Service" ? "MITRE T1543.003" : type.StartsWith("Tâche") ? "MITRE T1053.005" : "MITRE T1547.001" });
                else if (UserDir.IsMatch(path)) l.Add(new CyFinding { Level = "info", Host = Environment.MachineName, Item = item, Title = "Signé (" + signer + ") mais lancé depuis un dossier utilisateur : " + Cut(path), Fix = "Souvent légitime (Teams, OneDrive…) : vérifier l'éditeur.", Ref = "MITRE T1547.001" });
            }
            return l.OrderBy(x => x.Level == "bad" ? 0 : x.Level == "warn" ? 1 : 2).ToList();
        }

        static readonly int[] RiskyListen = { 21, 23, 3389, 5900, 5901, 5985, 1433, 3306, 5432, 6379, 27017, 9200 };
        public static List<CyFinding> EvalConn(string output, out int total)
        {
            // C|état|IP locale|port|IP distante|port|PID|processus|chemin|signature
            var l = new List<CyFinding>(); var rows = Rows(output, "C", 10).Where(r => r[1] != "END").ToList(); total = rows.Count;
            foreach (var r in rows)
            {
                string st = r[1], la = r[2], ra = r[4], proc = r[7], path = r[8], sig = r[9]; int lp, rp; int.TryParse(r[3], out lp); int.TryParse(r[5], out rp);
                var who = proc + (path.Length > 0 ? " (" + path + ")" : "");
                bool unsigned = path.Length > 0 && sig != "Valid";
                if (st == "Listen" && la != "127.0.0.1" && la != "::1")
                {
                    if (unsigned) l.Add(new CyFinding { Level = UserDir.IsMatch(path) ? "bad" : "warn", Host = Environment.MachineName, Item = "écoute " + la + ":" + lp, Title = "Port ouvert par un programme non signé : " + who, Fix = "Identifier le programme ; bloquer au pare-feu si inutile.", Ref = "MITRE T1571" });
                    else if (RiskyListen.Contains(lp)) l.Add(new CyFinding { Level = "info", Host = Environment.MachineName, Item = "écoute " + la + ":" + lp, Title = "Service sensible en écoute : " + who, Fix = "Limiter l'accès au pare-feu Windows.", Ref = "CIS 4.4" });
                }
                else if (st == "Established" && unsigned)
                {
                    bool pub; try { pub = !Scope.Private(Scope.Ip(ra)); } catch { pub = false; }
                    if (pub) l.Add(new CyFinding { Level = UserDir.IsMatch(path) ? "bad" : "warn", Host = Environment.MachineName, Item = "→ " + ra + ":" + rp, Title = "Connexion Internet d'un programme non signé : " + who, Fix = "Vérifier le programme (VirusTotal) et la destination.", Ref = "MITRE T1071" });
                }
            }
            return l.GroupBy(x => x.Item + x.Title).Select(g => g.First()).OrderBy(x => x.Level == "bad" ? 0 : x.Level == "warn" ? 1 : 2).ToList();
        }
        static string Cut(string s) { s = (s ?? "").Trim(); return s.Length > 110 ? s.Substring(0, 110) + "…" : s; }
    }

    // ===================================================================== Have I Been Pwned (k-anonymat) et VirusTotal (empreinte seule)
    static class Hibp
    {
        public static string Base = "https://api.pwnedpasswords.com";
        public static string Sha1(string s) { using (var h = SHA1.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", ""); }
        // seuls les 5 premiers caractères de l'empreinte SHA-1 quittent le PC ; réponse complétée de faux résultats (Add-Padding)
        public static int Count(string password)
        {
            var h = Sha1(password); var body = Convert.ToString(Http.Call("GET", Base + "/range/" + h.Substring(0, 5), new Dictionary<string, string> { { "Add-Padding", "true" } }, null, 20000));
            foreach (var line in body.Split('\n'))
            {
                var p = line.Trim().Split(':'); int n;
                if (p.Length == 2 && p[0].Equals(h.Substring(5), StringComparison.OrdinalIgnoreCase) && int.TryParse(p[1], out n)) return n;
            }
            return 0;
        }
    }

    static class Vt
    {
        public static string Base = "https://www.virustotal.com/api/v3";
        public static string Sha256(string file) { using (var h = SHA256.Create()) using (var f = File.OpenRead(file)) return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant(); }
        // null = inconnu de VirusTotal ; aucun envoi du fichier
        public static Dictionary<string, object> Lookup(string sha, string key)
        {
            try { return Http.Call("GET", Base + "/files/" + sha, new Dictionary<string, string> { { "x-apikey", key } }, null, 20000) as Dictionary<string, object>; }
            catch (Exception ex) { if (ex.Message.Contains("404")) return null; throw; }
        }
        public static string Verdict(Dictionary<string, object> r, out string level)
        {
            level = "info"; if (r == null) return "inconnu de VirusTotal (jamais analysé)";
            object d, a, st; Dictionary<string, object> attr = null, stats = null;
            if (r.TryGetValue("data", out d) && d is Dictionary<string, object> && ((Dictionary<string, object>)d).TryGetValue("attributes", out a)) attr = a as Dictionary<string, object>;
            if (attr != null && attr.TryGetValue("last_analysis_stats", out st)) stats = st as Dictionary<string, object>;
            if (stats == null) return "réponse VirusTotal inattendue";
            int mal, sus, tot = 0; int.TryParse(Http.S(stats, "malicious"), out mal); int.TryParse(Http.S(stats, "suspicious"), out sus);
            foreach (var v in stats.Values) { int x; if (int.TryParse(Convert.ToString(v), out x)) tot += x; }
            level = mal >= 3 ? "bad" : mal + sus > 0 ? "warn" : "ok";
            return mal + " moteur(s) sur " + tot + " le déclarent malveillant, " + sus + " suspect" + (attr != null && Http.S(attr, "meaningful_name").Length > 0 ? " · nom connu : " + Http.S(attr, "meaningful_name") : "");
        }
    }

    // ===================================================================== rapport cyber client
    static class CyberReport
    {
        public static string Html(Client c, List<CyFinding> scan, string scanInfo, List<CyFinding> poste, string posteInfo)
        {
            var co = Prefs.Ini.Get("Societe", "AMIPROTEK"); var all = scan.Concat(poste).ToList();
            var sb = new StringBuilder("<!DOCTYPE html><html lang=fr><head><meta charset=utf-8><title>Audit cyber - ").Append(Fmt.Html(c.Name)).Append("</title><style>@page{size:A4;margin:14mm}body{font-family:'Segoe UI',Arial,sans-serif;color:#1a2230;font-size:10pt;max-width:780px;margin:24px auto;padding:0 16px}")
              .Append("h1{font-size:17pt;color:#0084a8;margin:0}h2{font-size:11pt;color:#0084a8;text-transform:uppercase;border-bottom:1px solid #d6dee8;margin:18px 0 6px}table{width:100%;border-collapse:collapse}td,th{border:1px solid #d6dee8;padding:4px 6px;text-align:left;vertical-align:top}th{background:#f1f5f9}")
              .Append(".bad{color:#b0102f;font-weight:600}.warn{color:#a35d00;font-weight:600}.info{color:#4a5a6a}.ok{color:#007a43}.kpi{display:flex;gap:10px}.kpi div{flex:1;border:1px solid #d6dee8;padding:8px;text-align:center}.kpi b{display:block;font-size:18pt}small{color:#6b7a8a}footer{margin-top:20px;font-size:8pt;color:#8a97a6;text-align:center}</style></head><body>")
              .Append("<h1>Audit de sécurité</h1><div>").Append(Fmt.Html(c.Name)).Append(" · ").Append(DateTime.Now.ToString("dd/MM/yyyy")).Append("</div>")
              .Append("<h2>Synthèse</h2><div class=kpi><div><b class=bad>").Append(all.Count(x => x.Level == "bad")).Append("</b>critiques</div><div><b class=warn>").Append(all.Count(x => x.Level == "warn")).Append("</b>à corriger</div><div><b class=info>").Append(all.Count(x => x.Level == "info")).Append("</b>informations</div></div>")
              .Append("<p><small>Autorisation écrite du client : ").Append(c.CyberAuth.Year > 2000 ? c.CyberAuth.ToString("dd/MM/yyyy") + (c.CyberAuthRef.Length > 0 ? " (" + Fmt.Html(c.CyberAuthRef) + ")" : "") : "—").Append(" · périmètre : ").Append(Fmt.Html(c.CyberScope.Replace("\r", "").Replace("\n", ", "))).Append("</small></p>");
            Action<string, string, List<CyFinding>> sec = (title, info, l) =>
            {
                sb.Append("<h2>").Append(title).Append("</h2><p><small>").Append(Fmt.Html(info)).Append("</small></p>");
                if (l.Count == 0) { sb.Append("<p class=ok>Aucun point relevé.</p>"); return; }
                sb.Append("<table><tr><th>Niveau</th><th>Machine</th><th>Élément</th><th>Constat</th><th>Recommandation</th><th>Réf.</th></tr>");
                foreach (var f in l) sb.Append("<tr><td class=").Append(f.Level).Append(">").Append(f.Level == "bad" ? "Critique" : f.Level == "warn" ? "À corriger" : f.Level == "ok" ? "OK" : "Info").Append("</td><td>").Append(Fmt.Html(f.Host)).Append("</td><td>").Append(Fmt.Html(f.Item)).Append("</td><td>").Append(Fmt.Html(f.Title)).Append("</td><td>").Append(Fmt.Html(f.Fix)).Append("</td><td><small>").Append(Fmt.Html(f.Ref)).Append("</small></td></tr>");
                sb.Append("</table>");
            };
            sec("Réseau (scan Nmap)", scanInfo, scan);
            sec("Poste analysé", posteInfo, poste);
            sb.Append("<footer>").Append(Fmt.Html(co)).Append(" · audit réalisé avec autorisation du client · " + Ed.Name + " by alex.b</footer></body></html>");
            return sb.ToString();
        }
    }

    // ===================================================================== page Cyber
    class CyberPage : Page
    {
        readonly ComboBox client, profile; readonly TextBox targets; readonly Grid grid; readonly Label stat, nm; readonly LogBox box;
        List<CyFinding> scanF = new List<CyFinding>(), posteF = new List<CyFinding>(); string scanInfo = "aucun scan dans cette session", posteInfo = "aucune analyse du poste dans cette session";
        public override string Title { get { return "Cyber"; } }
        Control bBehav; HashSet<string> snapBefore;

        public CyberPage()
        {
            Lbl("CYBER", 0, k(2), "accent", 13f, true);
            nm = Lbl("", k(80), k(10), "muted");
            Lbl("Client", 0, k(46), "muted");
            client = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(k(50), k(42)), Width = k(300) }; Controls.Add(client);
            client.SelectedIndexChanged += (s, e) => ShowScope();
            int x = Btn("Périmètre et autorisation…", t => t.Warn, k(360), k(40), (s, e) => EditScope()).Right + k(10);
            Btn("Journal des scans", t => t.Muted, x, k(40), (s, e) => { var f = Path.Combine(Store.Dir, "cyber-audit.log"); if (File.Exists(f)) Process.Start("notepad.exe", "\"" + f + "\""); else CyberForm.Info(this, "Aucun scan enregistré."); });
            Lbl("Profil", 0, k(84), "muted");
            profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(k(50), k(80)), Width = k(300) };
            foreach (var p in Nmap.Profiles) profile.Items.Add(p.Name); profile.SelectedIndex = 2; Controls.Add(profile);
            targets = new TextBox { Location = new Point(k(360), k(80)), Width = k(330), Font = new Font("Consolas", 9.5f) }; Controls.Add(targets);
            Btn("▶  SCANNER", t => t.Ok, k(700), k(78), (s, e) => Scan(), true);
            int x2 = 0, y2 = k(120);
            x2 = Btn("Chasse aux menaces", t => t.Accent, x2, y2, (s, e) => RunPoste("hunt")).Right + k(6);
            x2 = Btn("Persistance", t => t.Accent, x2, y2, (s, e) => RunPoste("persist")).Right + k(6);
            x2 = Btn("Connexions", t => t.Accent, x2, y2, (s, e) => RunPoste("conn")).Right + k(6);
            x2 = Btn("Mot de passe compromis ?", t => t.Accent2, x2, y2, (s, e) => Pwned()).Right + k(6);
            x2 = Btn("Fichier suspect…", t => t.Accent2, x2, y2, (s, e) => Suspect()).Right + k(6);
            x2 = Btn("Rapport cyber", t => t.Ok, x2, y2, (s, e) => ReportCy()).Right + k(6);
            Btn("→ GLPI", t => t.Muted, x2, y2, (s, e) => ToGlpi());
            // labo défensif (Tech IT AB)
            int x3 = 0, y3 = k(158);
            x3 = Btn("E-mail suspect…", t => t.Warn, x3, y3, (s, e) => Mail()).Right + k(6);
            x3 = Btn("Lien suspect…", t => t.Warn, x3, y3, (s, e) => Link()).Right + k(6);
            x3 = Btn("Scans reçus", t => t.Accent, x3, y3, (s, e) => Scans()).Right + k(6);
            x3 = Btn("Surveiller les scans", t => t.Accent, x3, y3, (s, e) => Watch()).Right + k(6);
            x3 = Btn("Durcir anti-scan", t => t.Warn, x3, y3, (s, e) => HardenPc()).Right + k(6);
            x3 = Btn("Tester un programme…", t => t.Accent2, x3, y3, (s, e) => TestExe()).Right + k(6);
            bBehav = Btn("Comportement : photo avant", t => t.Accent2, x3, y3, (s, e) => Behaviour());
            // renforts défensifs (Sysinternals + durcissement)
            int x4 = 0, y4 = k(196);
            x4 = Btn("Sysmon", t => t.Accent, x4, y4, (s, e) => RunSysmon()).Right + k(6);
            x4 = Btn("Autoruns (démarrage complet)", t => t.Accent, x4, y4, (s, e) => RunAutoruns()).Right + k(6);
            Btn("Durcissement CIS / ANSSI", t => t.Ok, x4, y4, (s, e) => RunCis());
            stat = Lbl("", 0, k(234), "muted");
            grid = new Grid(); grid.Location = new Point(0, k(256)); Controls.Add(grid);
            box = new LogBox(); Controls.Add(box);
            grid.SelectedIndexChanged += (s, e) => { var f = grid.Sel as CyFinding; if (f == null) return; box.Clear(); box.Line("// " + f.Host + " · " + f.Item, "accent", true); box.Line(f.Title, f.Level == "ok" ? "ok" : f.Level == "info" ? "text" : f.Level); if (f.Fix.Length > 0) box.Line("→ " + f.Fix, "text"); if (f.Ref.Length > 0) box.Line("Réf. " + f.Ref, "muted"); };
            Resize += (s, e) =>
            {
                int h = ClientSize.Height - grid.Top, gh = Math.Max(k(120), h * 64 / 100);
                grid.Size = new Size(ClientSize.Width, gh); box.Location = new Point(0, grid.Bottom + k(8)); box.Size = new Size(ClientSize.Width, Math.Max(k(60), ClientSize.Height - box.Top));
            };
            Columns();
        }

        void Columns() { grid.Columns.Clear(); grid.Col("Niveau", 80).Col("Machine", 170).Col("Élément", 210).Col("Constat", 520).Col("Réf.", 150); }

        public override void OnShow()
        {
            var sel = client.SelectedItem as string; client.Items.Clear();
            foreach (var c in Store.D.Clients.OrderBy(c => c.Name)) client.Items.Add(c.Name);
            if (sel != null && client.Items.Contains(sel)) client.SelectedItem = sel; else if (client.Items.Count > 0) client.SelectedIndex = 0;
            System.Threading.Tasks.Task.Run(() => { var v = Nmap.Version(); Ui(() => { nm.Text = v == null ? "Nmap absent : installe-le depuis nmap.org (avec Npcap) pour les scans réseau ; l'analyse du poste fonctionne sans." : "Nmap " + v + " détecté · scans limités au périmètre autorisé de chaque client · aucun script offensif"; nm.ForeColor = v == null ? Theme.Current.Warn : Theme.Current.Muted; }); });
        }

        Client Cur() { return Store.D.Clients.FirstOrDefault(x => x.Name == client.SelectedItem as string); }
        void Ui(Action a) { try { if (IsHandleCreated) BeginInvoke(a); } catch { } }
        bool AskUi(string q) { bool r = false; if (InvokeRequired) Invoke(new Action(() => r = CyberForm.Ask(this, q))); else r = CyberForm.Ask(this, q); return r; }
        void Out(string s, string c) { Ui(() => box.Line(s, c)); var j = Job.Current; if (j != null) j.Record(s, c); }

        void ShowScope()
        {
            var c = Cur(); if (c == null) return;
            if (targets.Text.Length == 0 || targets.Tag as string != c.Id) { targets.Text = string.Join(" ", (c.CyberScope ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(3)); targets.Tag = c.Id; }
            stat.Text = c.CyberAuth.Year > 2000 ? "Autorisation du " + c.CyberAuth.ToString("dd/MM/yyyy") + (c.CyberAuthRef.Length > 0 ? " (" + c.CyberAuthRef + ")" : "") + " · périmètre : " + c.CyberScope.Replace("\r", "").Replace("\n", ", ") : "Aucune autorisation écrite enregistrée pour ce client : scans réseau bloqués.";
            stat.ForeColor = c.CyberAuth.Year > 2000 ? Theme.Current.Muted : Theme.Current.Warn;
        }

        void EditScope()
        {
            var c = Cur(); if (c == null) { CyberForm.Info(this, "Choisis un client."); return; }
            var fs = new F("Périmètre autorisé (une IP, un réseau CIDR ou une plage par ligne ; IP publique incluse si prévue)", c.CyberScope.Length > 0 ? c.CyberScope : string.Join("\r\n", Scope.LocalNets())) { Multi = true, Lines = 5, Required = true };
            var fa = new F("Le client m'a donné une autorisation ÉCRITE pour auditer ce périmètre", c.CyberAuth.Year > 2000 ? "Oui" : "Non") { Options = new[] { "Oui", "Non" }, Editable = false };
            var fr = new F("Référence du document (contrat, bon d'intervention, mail…)", c.CyberAuthRef);
            if (!Dlg.Edit(this, "Périmètre et autorisation — " + c.Name, fs, fa, fr)) return;
            try { var l = Scope.ParseList(fs.Value); if (l.Any(r => r.Count > 65536)) throw new FormatException("réseau plus grand qu'un /16 refusé"); }
            catch (Exception ex) { CyberForm.Warn(this, ex.Message); return; }
            if (fa.Value == "Oui" && fr.Value.Trim().Length == 0) { CyberForm.Warn(this, "Indique la référence du document d'autorisation : c'est ta preuve en cas de litige."); return; }
            c.CyberScope = fs.Value.Trim(); c.CyberAuthRef = fr.Value.Trim();
            c.CyberAuth = fa.Value == "Oui" ? (c.CyberAuth.Year > 2000 ? c.CyberAuth : DateTime.Now) : DateTime.MinValue;
            Store.Save(); targets.Tag = null; targets.Text = ""; ShowScope();
            Nmap.Audit(c, new Nmap.Profile { Id = "perimetre" }, c.CyberScope.Replace("\r", "").Replace("\n", ","), fa.Value == "Oui" ? "autorisation enregistrée (" + c.CyberAuthRef + ")" : "autorisation retirée");
        }

        void Scan()
        {
            var c = Cur(); var p = Nmap.Profiles[Math.Max(0, profile.SelectedIndex)];
            if (Nmap.Exe == null) { if (CyberForm.Ask(this, "Nmap n'est pas installé sur ce PC.\nSa licence interdit de l'intégrer à " + Ed.Name + " : installe-le depuis nmap.org (gratuit, avec Npcap).\n\nOuvrir la page de téléchargement ?")) Process.Start("https://nmap.org/download"); return; }
            List<Scope.Range> t;
            try { t = Scope.Targets(targets.Text, Dns.GetHostAddresses); } catch (Exception ex) { CyberForm.Warn(this, ex.Message); return; }
            var refus = Scope.Check(c, t, DateTime.Now);
            if (refus != null) { CyberForm.Warn(this, "Scan refusé : " + refus + "."); if (c != null) Nmap.Audit(c, p, targets.Text, "REFUSÉ : " + refus); return; }
            if (t.Any(r => !Scope.Private(r)) && !CyberForm.Ask(this, "Cible publique (Internet). Le client a bien autorisé ce test et prévenu son hébergeur/opérateur si nécessaire ?")) return;
            stat.Text = p.Name + " en cours (suivi dans Tâches)…"; stat.ForeColor = Theme.Current.Muted;
            Jobs.Run("Nmap · " + p.Name + " · " + c.Name, "nmap", j =>
            {
                var xml = Nmap.Run(c, p, t, j.Log);
                var hosts = Nmap.Parse(File.ReadAllText(xml, Encoding.UTF8));
                var f = p.Ports ? Risk.Evaluate(hosts, DateTime.Now) : new List<CyFinding>();
                if (!p.Ports) foreach (var h in hosts) f.Add(new CyFinding { Level = "info", Host = h.Ip + (h.Name.Length > 0 ? " (" + h.Name + ")" : ""), Item = h.Mac.Length > 0 ? h.Mac + " " + h.Vendor : "", Title = "Machine active" + (h.Os.Length > 0 ? " · " + h.Os : ""), Fix = "", Ref = "" });
                var prev = Nmap.Previous(xml);
                if (prev != null && p.Ports) try { f.AddRange(Nmap.Diff(Nmap.Parse(File.ReadAllText(prev, Encoding.UTF8)), hosts)); j.Log("Comparé au scan du " + File.GetLastWriteTime(prev).ToString("dd/MM/yyyy HH:mm"), "muted"); } catch { }
                var open = hosts.Sum(h => h.Ports.Count(Nmap.Open));
                scanF = f; scanInfo = p.Name + " le " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " · " + hosts.Count + " machine(s), " + open + " port(s) ouvert(s) · cibles : " + string.Join(" ", t.Select(r => r.Src).Distinct());
                j.Log(scanInfo + " · " + f.Count(x => x.Level == "bad") + " critique(s), " + f.Count(x => x.Level == "warn") + " à corriger", f.Any(x => x.Level == "bad") ? "bad" : "ok");
                Ui(() => Show(f, scanInfo));
            }, Out);
        }

        void RunPoste(string what)
        {
            var titles = new Dictionary<string, string> { { "hunt", "Chasse aux menaces (journaux, 7 jours)" }, { "persist", "Persistance (démarrage, tâches, services, WMI)" }, { "conn", "Connexions et ports en écoute" } };
            Jobs.Run(titles[what] + " · " + Environment.MachineName, null, j =>
            {
                string o; List<CyFinding> f; int total = 0; string info;
                if (what == "hunt") { o = Sh.Ps(Poste.HuntScript, 600000); f = Poste.EvalHunt(o); info = "journaux Sécurité, Système, PowerShell, Defender sur 7 jours"; }
                else if (what == "persist") { o = Sh.Ps(Poste.PersistScript, 900000); f = Poste.EvalPersist(o, out total); info = total + " point(s) de démarrage examiné(s)"; }
                else { o = Sh.Ps(Poste.ConnScript, 300000); f = Poste.EvalConn(o, out total); info = total + " connexion(s) et port(s) en écoute"; }
                if (!Poste.Complete(o, what == "hunt" ? "H" : what == "persist" ? "P" : "C")) j.Log("Analyse incomplète : " + o.Trim().Split('\n').LastOrDefault(), "warn");
                posteF.RemoveAll(x => x.Item.StartsWith(what == "hunt" ? "événement" : what == "persist" ? "§" : "→") || (what == "conn" && x.Item.StartsWith("écoute")) || (what == "persist" && !x.Item.StartsWith("événement") && !x.Item.StartsWith("→") && !x.Item.StartsWith("écoute")));
                posteF.AddRange(f); posteInfo = Environment.MachineName + " · " + DateTime.Now.ToString("dd/MM/yyyy HH:mm");
                j.Log(titles[what] + " : " + info + " · " + f.Count(x => x.Level == "bad") + " critique(s), " + f.Count(x => x.Level == "warn") + " à vérifier", f.Any(x => x.Level == "bad") ? "bad" : "ok");
                Ui(() => Show(f, titles[what] + " : " + info));
            }, Out);
        }

        // ---------------- renforts défensifs
        void RunSysmon()
        {
            Jobs.Run("Sysmon · " + Environment.MachineName, null, j =>
            {
                if (!Sysmon.Installed())
                {
                    if (!AskUi("Sysmon (Microsoft Sysinternals) journalise finement les processus, connexions et persistance — bien plus que les journaux Windows de base.\n\nL'installer (winget) et appliquer la configuration de " + Ed.Name + " (processus, réseau, injection, accès à lsass, démarrage, clés Run) ?")) { j.Log("Installation annulée.", "muted"); return; }
                    Sysmon.Install(true, j.Log);
                    j.Log("Sysmon collecte à partir de maintenant : relance « Sysmon » plus tard pour voir les événements.", "muted"); return;
                }
                var o = Sh.Ps(Sysmon.ReadScript, 300000);
                var f = Sysmon.Eval(o);
                posteF.RemoveAll(x => x.Item.StartsWith("Sysmon")); posteF.AddRange(f);
                posteInfo = Environment.MachineName + " · " + DateTime.Now.ToString("dd/MM/yyyy HH:mm");
                j.Log("Sysmon : " + f.Count(x => x.Level == "bad") + " critique(s), " + f.Count(x => x.Level == "warn") + " à vérifier (3 jours)", f.Any(x => x.Level == "bad") ? "bad" : "ok");
                Ui(() => Show(f, "Sysmon — événements à risque (3 jours)"));
            }, Out);
        }

        void RunAutoruns()
        {
            Jobs.Run("Autoruns · " + Environment.MachineName, null, j =>
            {
                var csv = Autoruns.Run(j.Log);
                int total; var f = Autoruns.Eval(csv, out total);
                posteF.RemoveAll(x => x.Item.StartsWith("Autoruns · ")); posteF.AddRange(f);
                posteInfo = Environment.MachineName + " · " + DateTime.Now.ToString("dd/MM/yyyy HH:mm");
                j.Log("Autoruns : " + total + " entrées de démarrage · " + f.Count(x => x.Level == "bad") + " critique(s), " + f.Count(x => x.Level == "warn") + " à vérifier", f.Any(x => x.Level == "bad") ? "bad" : "ok");
                Ui(() => Show(f, "Autoruns — " + total + " entrées de démarrage examinées"));
            }, Out);
        }

        void RunCis()
        {
            Jobs.Run("Durcissement CIS/ANSSI · " + Environment.MachineName, null, j =>
            {
                var o = Sh.Ps(Cis.Script, 180000);
                int score; var f = Cis.Eval(o, out score);
                posteF.RemoveAll(x => x.Item.StartsWith("CIS · ")); posteF.AddRange(f);
                posteInfo = Environment.MachineName + " · " + DateTime.Now.ToString("dd/MM/yyyy HH:mm");
                j.Log("Durcissement : " + score + "/100 conforme · " + f.Count(x => x.Level == "bad") + " critique(s), " + f.Count(x => x.Level == "warn") + " à corriger", score >= 80 ? "ok" : score >= 60 ? "warn" : "bad");
                Ui(() => Show(f, "Durcissement CIS / ANSSI — " + score + "/100 conforme"));
            }, Out);
        }

        void Show(List<CyFinding> f, string info)
        {
            grid.BeginUpdate(); grid.Items.Clear(); Columns(); var th = Theme.Current;
            foreach (var x in f) grid.Items.Add(new ListViewItem(new[] { x.Level == "bad" ? "CRITIQUE" : x.Level == "warn" ? "À CORRIGER" : x.Level == "ok" ? "OK" : "info", x.Host, x.Item, x.Title, x.Ref }) { Tag = x, ForeColor = x.Level == "bad" ? th.Bad : x.Level == "warn" ? th.Warn : x.Level == "ok" ? th.Ok : th.Muted });
            grid.EndUpdate();
            stat.Text = info + " · " + f.Count(x => x.Level == "bad") + " critique(s), " + f.Count(x => x.Level == "warn") + " à corriger, " + f.Count(x => x.Level == "info") + " info";
            stat.ForeColor = f.Any(x => x.Level == "bad") ? th.Bad : f.Any(x => x.Level == "warn") ? th.Warn : th.Ok;
            if (f.Count == 0) { box.Clear(); box.Line("Aucun point relevé.", "ok"); }
        }

        void Pwned()
        {
            var pw = Dlg.Ask(this, "Mot de passe compromis ?", "Mot de passe à vérifier (seuls 5 caractères de son empreinte SHA-1 sont envoyés, jamais le mot de passe)", "", true);
            if (string.IsNullOrEmpty(pw)) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { var n = Hibp.Count(pw); Ui(() => { if (n > 0) CyberForm.Warn(this, "COMPROMIS : ce mot de passe apparaît " + n.ToString("N0") + " fois dans des fuites de données connues.\nÀ changer partout où il est utilisé."); else CyberForm.Info(this, "Absent des fuites connues de Have I Been Pwned.\n(Ce n'est pas une garantie de solidité.)"); }); }
                catch (Exception ex) { Ui(() => CyberForm.Warn(this, "Have I Been Pwned injoignable : " + ex.Message)); }
            });
        }

        void Suspect()
        {
            string file; using (var d = new OpenFileDialog { Title = "Fichier à vérifier (il n'est jamais envoyé)", Filter = "Tous les fichiers|*.*" }) { if (d.ShowDialog(this) != DialogResult.OK) return; file = d.FileName; }
            var key = Secrets.Get("VtKey");
            if (key.Length == 0)
            {
                var k2 = Dlg.Ask(this, "VirusTotal", "Clé API VirusTotal (compte gratuit ; vide = ouvrir la page de l'empreinte dans le navigateur)", "", true);
                if (!string.IsNullOrEmpty(k2)) { Secrets.Set("VtKey", k2.Trim()); key = k2.Trim(); }
            }
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var sha = Vt.Sha256(file); string sig; string o = Sh.Ps("$s=Get-AuthenticodeSignature -LiteralPath " + Sh.Q(file) + ";[string]$s.Status+'|'+[string]$s.SignerCertificate.Subject", 30000).Trim(); sig = o;
                    if (key.Length == 0) { Ui(() => { box.Clear(); box.Line("SHA-256 : " + sha, "text"); box.Line("Signature : " + sig, "muted"); }); Process.Start("https://www.virustotal.com/gui/file/" + sha); return; }
                    string lvl; var v = Vt.Verdict(Vt.Lookup(sha, key), out lvl);
                    var f = new CyFinding { Level = lvl == "ok" ? "info" : lvl, Host = Environment.MachineName, Item = Path.GetFileName(file), Title = "VirusTotal : " + v + " · signature " + sig.Split('|')[0], Fix = lvl == "bad" ? "Supprimer/mettre en quarantaine, analyser le poste (Defender hors ligne, Safety Scanner)." : "SHA-256 " + sha, Ref = "virustotal.com/gui/file/" + sha };
                    posteF.Add(f); Ui(() => Show(new List<CyFinding> { f }, "Fichier " + Path.GetFileName(file)));
                }
                catch (Exception ex) { Ui(() => CyberForm.Warn(this, ex.Message)); }
            });
        }

        // ---------------- labo défensif
        void Mail()
        {
            var f = new F("Source complète du message (en-têtes + corps). Vide = ouvrir un fichier .eml", "") { Multi = true, Lines = 12 };
            if (!Dlg.Edit(this, "Analyse d'un e-mail suspect", f)) return;
            var raw = f.Value;
            if (raw.Trim().Length == 0) { using (var d = new OpenFileDialog { Title = "Message (.eml)", Filter = "Messages|*.eml;*.txt|Tous|*.*" }) { if (d.ShowDialog(this) != DialogResult.OK) return; raw = File.ReadAllText(d.FileName, Encoding.UTF8); } }
            var l = Phish.Analyze(raw); var v = Phish.Verdict(l);
            posteF.AddRange(l.Where(x => x.Level == "bad" || x.Level == "warn"));
            Show(l, "E-mail : " + v);
        }
        void Link()
        {
            var u = Dlg.Ask(this, "Lien suspect", "Adresse à analyser (elle n'est PAS ouverte)", "", false); if (string.IsNullOrWhiteSpace(u)) return;
            var l = LinkCheck.Analyze(u.Trim());
            if (l.Any(x => x.Title.StartsWith("Lien raccourci")) && CyberForm.Ask(this, "Révéler la destination du lien raccourci ?\n\n" + Ed.Name + " suit les redirections SANS charger les pages, mais les serveurs verront ton adresse IP : fais-le depuis une VM ou un VPN."))
                try { var chain = LinkCheck.Reveal(u.Trim()); for (int i = 1; i < chain.Count; i++) { l.Add(new CyFinding { Level = "info", Host = "redirection " + i, Item = chain[i], Title = "→ " + chain[i], Fix = "", Ref = "" }); l.AddRange(LinkCheck.Analyze(chain[i], "destination")); } }
                catch (Exception ex) { l.Add(new CyFinding { Level = "info", Host = "redirection", Item = u, Title = "Destination non révélée : " + ex.Message }); }
            Show(l, "Lien : " + (l.Any(x => x.Level == "bad") ? "DANGEREUX" : l.Any(x => x.Level == "warn") ? "suspect" : "aucun signe connu (prudence quand même)"));
        }
        void Scans()
        {
            Jobs.Run("Scans reçus par " + Environment.MachineName, null, j =>
            {
                string o; Proc.Run(Proc.Sys("cmd.exe"), "/c " + ScanWatch.Enable, 30000, out o);
                long pos; var lines = ScanWatch.Tail(0, out pos);
                if (lines.Count == 0) { j.Log("Journal du pare-feu vide : la journalisation des paquets bloqués vient d'être activée, reviens après quelques minutes.", "warn"); Ui(() => Show(new List<CyFinding>(), "Journal du pare-feu activé")); return; }
                var f = ScanWatch.Findings(ScanWatch.Parse(lines));
                j.Log(lines.Count + " ligne(s) analysée(s) · " + f.Count + " scan(s) ou sondage(s)", f.Any(x => x.Level == "bad") ? "bad" : "ok");
                posteF.RemoveAll(x => x.Ref.StartsWith("MITRE T1046")); posteF.AddRange(f);
                Ui(() => Show(f, "Scans reçus (journal du pare-feu, " + lines.Count + " lignes)"));
            }, Out);
        }
        void Watch()
        {
            Jobs.Run("Surveillance des scans (jusqu'à annulation)", "scanwatch", j =>
            {
                string o; Proc.Run(Proc.Sys("cmd.exe"), "/c " + ScanWatch.Enable, 30000, out o);
                long pos; ScanWatch.Tail(0, out pos); var recent = new List<string>(); var seen = new HashSet<string>();
                j.Log("Surveillance active : alerte dès qu'une machine sonde 5 ports ou plus en une minute.", "ok");
                while (true)
                {
                    System.Threading.Thread.Sleep(5000); j.Check();
                    recent.AddRange(ScanWatch.Tail(pos, out pos)); if (recent.Count > 20000) recent.RemoveRange(0, recent.Count - 20000);
                    foreach (var h in ScanWatch.Parse(recent))
                    {
                        if (!seen.Add(h.Src + h.First.ToString("s"))) continue;
                        var f = ScanWatch.Findings(new List<ScanWatch.Hit> { h })[0]; j.Log("✗ " + h.Src + " : " + f.Title, f.Level);
                        Ui(() => { Shell.Notify("Scan détecté depuis " + h.Src, f.Title); posteF.Add(f); });
                    }
                }
            }, Out);
        }
        void HardenPc()
        {
            // valeur multiligne stockée en base64 (1.5) : en 1.4 seules les lignes après la première étaient perdues au redémarrage
            var prev = string.Join("\n", IniLines.Unpack(Prefs.Ini.Get("HardenPrev", "")));
            if (prev.Length > 0)
            {
                if (!CyberForm.Ask(this, "Durcissement anti-scan déjà appliqué le " + Prefs.Ini.Get("HardenDate", "?") + ".\nRevenir aux réglages d'avant ?")) return;
                Jobs.Run("Retour avant durcissement", "net", j => { var o = Sh.Ps(Harden.Restore(prev), 120000); if (!o.Contains("OK|restauré")) throw new Exception(o.Trim()); Prefs.Ini.Set("HardenPrev", ""); Prefs.Save(); j.Log("Réglages d'avant restaurés.", "ok"); }, Out);
                return;
            }
            if (!CyberForm.Ask(this, "Durcir CE PC contre les scans et l'empoisonnement réseau ?\n\n• LLMNR désactivé\n• NetBIOS sur TCP/IP désactivé\n• Pare-feu actif, entrées bloquées par défaut\n• Profil Public : aucune règle entrante (furtif)\n• Journal des paquets bloqués activé\n\nLes réglages actuels sont sauvegardés : retour arrière en recliquant.\nÀ tester d'abord dans une VM (partages et bureau à distance entrants peuvent être coupés en réseau public).")) return;
            Jobs.Run("Durcissement anti-scan", "net", j =>
            {
                var o = Sh.Ps(Harden.Apply, 180000);
                Prefs.Ini.Set("HardenPrev", IniLines.Pack(o.Split('\n').Select(x => x.Trim()).Where(x => x.StartsWith("PREV|")))); Prefs.Ini.Set("HardenDate", DateTime.Now.ToString("dd/MM/yyyy HH:mm")); Prefs.Save();
                foreach (var l in o.Split('\n').Select(x => x.Trim()).Where(x => x.StartsWith("OK|"))) j.Log("✓ " + l.Substring(3), "ok");
            }, Out);
        }
        void TestExe()
        {
            string file; using (var d = new OpenFileDialog { Title = "Programme à tester (.exe, .dll)", Filter = "Programmes|*.exe;*.dll;*.sys;*.ocx|Tous|*.*" }) { if (d.ShowDialog(this) != DialogResult.OK) return; file = d.FileName; }
            System.Threading.Tasks.Task.Run(() =>
            {
                var pe = ProgTest.Read(file); var sig = Sh.Ps("$s=Get-AuthenticodeSignature -LiteralPath " + Sh.Q(file) + ";[string]$s.Status+'|'+[string]$s.SignerCertificate.Subject", 30000).Trim();
                var l = ProgTest.Evaluate(file, pe, sig);
                Ui(() => Show(l, "Programme " + Path.GetFileName(file) + " · SHA-256 " + Vt.Sha256(file).Substring(0, 16) + "… (bouton « Fichier suspect » pour VirusTotal)"));
            });
        }
        void Behaviour()
        {
            var after = snapBefore != null;
            Jobs.Run(after ? "Comportement : comparaison après exécution" : "Comportement : photo avant exécution", null, j =>
            {
                var snap = ProgTest.Snapshot(Sh.Ps(Poste.PersistScript, 900000), Sh.Ps(Poste.ConnScript, 300000));
                if (!after) { snapBefore = snap; j.Log("Photo prise (" + snap.Count + " éléments). Lance le programme à tester (de préférence dans une VM), utilise-le, puis clique « Comportement : comparer ».", "ok"); Ui(() => bBehav.Text = "Comportement : comparer"); return; }
                var f = ProgTest.Diff(snapBefore, snap); snapBefore = null;
                j.Log(f.Count(x => x.Item == "AJOUTÉ") + " ajout(s) au démarrage ou port(s) ouvert(s) par le programme", f.Any(x => x.Level == "bad") ? "bad" : "ok");
                posteF.AddRange(f.Where(x => x.Level != "info"));
                Ui(() => { bBehav.Text = "Comportement : photo avant"; Show(f, "Comportement du programme (avant / après)"); });
            }, Out);
        }

        string BuildReport(Client c)
        {
            var dir = Path.Combine(Report.Dir, "Cyber"); Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd_HHmm") + "_audit-cyber_" + Regex.Replace(c.Name, @"[^\p{L}\p{N}\-_ ]", "").Trim() + ".html");
            File.WriteAllText(file, CyberReport.Html(c, scanF, scanInfo, posteF, posteInfo), new UTF8Encoding(false));
            return Pdf.FromHtml(file) ?? file;
        }
        void ReportCy()
        {
            var c = Cur(); if (c == null) { CyberForm.Info(this, "Choisis un client."); return; }
            if (scanF.Count + posteF.Count == 0 && !CyberForm.Ask(this, "Aucun résultat dans cette session. Générer un rapport vide ?")) return;
            Jobs.Run("Rapport cyber " + c.Name, null, j => { var f = BuildReport(c); j.Log("Rapport : " + f, "ok"); try { Process.Start(f); } catch { } }, Out);
        }
        void ToGlpi()
        {
            var c = Cur(); if (c == null) return;
            if (!Glpi.Configured || c.GlpiEntity == 0) { CyberForm.Info(this, "GLPI non configuré ou client non lié à une entité."); return; }
            var bad = scanF.Concat(posteF).Count(x => x.Level == "bad");
            Jobs.Run("Audit cyber → GLPI " + c.Name, null, j =>
            {
                var f = BuildReport(c);
                var id = Glpi.Session(t =>
                {
                    var tid = bad > 0 ? Glpi.NewTicket(t, c.GlpiEntity, "Audit cyber : " + bad + " point(s) critique(s)", Glpi.H2(string.Join("\n", scanF.Concat(posteF).Where(x => x.Level == "bad").Select(x => "- " + x.Host + " · " + x.Item + " : " + x.Title + " → " + x.Fix))), 1, 1) : 0;
                    Glpi.Upload(t, f, c.GlpiEntity, tid); return tid;
                });
                j.Log(id > 0 ? "Ticket n°" + id + " créé, rapport joint : " + Glpi.TicketUrl(id) : "Rapport joint aux documents de l'entité (aucun point critique).", "ok");
            }, Out);
        }
    }
}
