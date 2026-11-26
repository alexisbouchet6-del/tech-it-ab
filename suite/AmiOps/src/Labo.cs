// Tech IT AB - labo défensif : analyse d'e-mail (hameçonnage), analyse de lien (IP loggers), détection des scans
// de ports visant ce PC (journal du pare-feu), durcissement anti-scan réversible, test de sécurité d'un programme.
// Tout est en lecture seule sauf le durcissement (confirmé, avec retour arrière).
// Développé by alex.b
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AlexB;

namespace AmiOps
{
    // ===================================================================== analyse de lien
    static class LinkCheck
    {
        // services connus pour enregistrer l'IP de qui clique (liste modifiable : Prefs « IpLoggers »)
        static readonly string[] Loggers = { "grabify.link", "iplogger.org", "iplogger.com", "iplogger.ru", "iplogger.co", "iplogger.info", "2no.co", "yip.su", "iplis.ru", "ipgrabber.ru", "ipgraber.ru", "blasze.com", "blasze.tk", "ps3cfw.com", "bmwforum.co", "leancoding.co", "quickmessage.us", "spottyfly.com", "stopify.co", "curiouscat.club", "catsnthings.fun", "catsnthing.com", "joinmy.site", "headshot.monster", "screenshot.best", "imageshare.best", "gamingfun.me", "partpicker.shop", "progaming.monster", "canarytokens.com", "webhook.site", "requestcatcher.com", "ipinfo.io/track" };
        static readonly string[] Shorteners = { "bit.ly", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "cutt.ly", "rebrand.ly", "shorturl.at", "rb.gy", "tiny.cc", "s.id", "t.ly", "buff.ly", "lnkd.in", "shorte.st", "adf.ly", "v.gd", "qrco.de" };
        static readonly string[] OddTlds = { "zip", "mov", "top", "xyz", "tk", "ml", "ga", "cf", "gq", "click", "country", "kim", "work", "rest", "cam", "icu" };

        public static IEnumerable<string> LoggerList { get { return Loggers.Concat(Prefs.Ini.Get("IpLoggers", "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)); } }
        static bool Is(string host, string dom) { return host == dom || host.EndsWith("." + dom); }

        // domaines à protéger contre l'usurpation (sosies)
        public static string[] Protected { get { return Prefs.Ini.Get("ProtectedDomains", "ami-tech.fr,amiprotek.fr,microsoft.com,office.com,outlook.com,google.com,apple.com,paypal.com,impots.gouv.fr,ameli.fr,laposte.fr,orange.fr,free.fr,sfr.fr,bouyguestelecom.fr,creditagricole.fr,societegenerale.fr,labanquepostale.fr").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries); } }

        public static int Lev(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i; for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++) for (int j = 1; j <= b.Length; j++) d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
        // registrable approx. : 2 derniers labels (3 pour .gouv.fr, .co.uk…)
        public static string Reg(string host)
        {
            var p = host.ToLowerInvariant().TrimEnd('.').Split('.'); if (p.Length <= 2) return string.Join(".", p);
            var two = p[p.Length - 2] + "." + p[p.Length - 1];
            return Regex.IsMatch(two, @"^(gouv|co|com|org|ac|asso)\.(fr|uk|jp|au|br)$") ? p[p.Length - 3] + "." + two : two;
        }
        // sosie d'un domaine protégé : caractères non ASCII, punycode, 1-2 lettres d'écart, marque dans un autre domaine
        public static string Lookalike(string host)
        {
            host = host.ToLowerInvariant(); var reg = Reg(host);
            foreach (var p in Protected)
            {
                if (reg == p) return null;
                var bp = p.Split('.')[0]; var br = reg.Split('.')[0];
                if (host.Any(ch => ch > 127) || host.Contains("xn--")) { var ascii = new string(host.Normalize(NormalizationForm.FormD).Where(ch => ch < 128).ToArray()); if (Reg(ascii) == p || ascii.Contains(bp)) return "caractères trompeurs imitant " + p; }
                if (br.Length >= 5 && Lev(br, bp) > 0 && Lev(br, bp) <= 2) return "sosie de " + p + " (" + Lev(br, bp) + " caractère(s) d'écart)";
                if (bp.Length >= 5 && host.Contains(bp) && reg != p) return "contient « " + bp + " » mais n'est pas " + p;
            }
            return null;
        }

        public static List<CyFinding> Analyze(string url, string where = "lien")
        {
            var l = new List<CyFinding>(); Uri u;
            var raw = (url ?? "").Trim();
            if (Regex.IsMatch(raw, @"^(javascript|data|vbscript):", RegexOptions.IgnoreCase)) { l.Add(Fd("bad", where, raw, "Lien qui exécute du code (" + raw.Split(':')[0] + ":)", "Ne pas cliquer.", "MITRE T1204.001")); return l; }
            if (!Regex.IsMatch(raw, @"^[a-z][a-z0-9+.\-]*://", RegexOptions.IgnoreCase)) raw = "http://" + raw;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out u)) { l.Add(Fd("warn", where, url, "Adresse illisible", "Ne pas cliquer.", "")); return l; }
            var host = u.Host.ToLowerInvariant(); var idn = new IdnMapping(); string uni = host; try { uni = idn.GetUnicode(host); } catch { }
            var lg = LoggerList.FirstOrDefault(d => Is(host, d.Split('/')[0]) && (d.Contains("/") ? u.AbsolutePath.StartsWith("/" + d.Split(new[] { '/' }, 2)[1]) : true));
            if (lg != null) l.Add(Fd("bad", where, url, "IP LOGGER connu (" + lg + ") : cliquer révèle ton adresse IP, ta position approximative et ton navigateur", "Ne pas cliquer ; signaler l'expéditeur.", "MITRE T1598.003"));
            if (Shorteners.Any(d => Is(host, d))) l.Add(Fd("warn", where, url, "Lien raccourci (" + host + ") : destination cachée, technique courante des IP loggers", "Révéler la destination depuis une VM ou un VPN avant d'ouvrir.", "MITRE T1598.003"));
            IPAddress ip; if (IPAddress.TryParse(host.Trim('[', ']'), out ip)) l.Add(Fd("warn", where, url, "Lien vers une adresse IP brute (pas de nom de domaine)", "Méfiance : rarement utilisé par un service légitime.", "MITRE T1566.002"));
            if (!string.IsNullOrEmpty(u.UserInfo)) l.Add(Fd("bad", where, url, "Lien piégé avec « @ » : le vrai site est " + host + ", pas « " + u.UserInfo + " »", "Ne pas cliquer.", "MITRE T1566.002"));
            var look = Lookalike(uni); if (look != null) l.Add(Fd("bad", where, url, "Domaine trompeur : " + uni + " — " + look, "Ne pas cliquer ; signaler.", "MITRE T1583.001"));
            if (host.Split('.').Length > 5) l.Add(Fd("warn", where, url, "Nom de domaine à rallonge (" + host.Split('.').Length + " niveaux)", "Le vrai domaine est la fin : " + Reg(host), "MITRE T1566.002"));
            var tld = host.Split('.').Last(); if (OddTlds.Contains(tld)) l.Add(Fd("info", where, url, "Extension de domaine souvent utilisée pour les arnaques (." + tld + ")", "Vérifier l'expéditeur.", ""));
            if (u.Scheme == "http" && Regex.IsMatch(u.PathAndQuery, @"login|signin|connexion|password|mot-de-passe|account|compte|verify|verif", RegexOptions.IgnoreCase)) l.Add(Fd("warn", where, url, "Page de connexion en HTTP non chiffré", "Ne jamais saisir d'identifiants.", ""));
            if (Regex.IsMatch(u.AbsolutePath, @"\.(exe|scr|js|vbs|hta|bat|cmd|ps1|msi|iso|img|lnk|one|zip|rar|7z)$", RegexOptions.IgnoreCase)) l.Add(Fd("warn", where, url, "Lien direct vers un fichier " + Path.GetExtension(u.AbsolutePath), "Ne pas exécuter ; vérifier sur VirusTotal.", "MITRE T1204.002"));
            return l;
        }

        // suit les redirections SANS charger les pages ; contacte les serveurs : à faire depuis une VM ou un VPN
        public static List<string> Reveal(string url, int max = 6)
        {
            var chain = new List<string> { url }; var cur = url;
            for (int i = 0; i < max; i++)
            {
                var req = (HttpWebRequest)WebRequest.Create(cur); req.Method = "HEAD"; req.AllowAutoRedirect = false; req.Timeout = 10000; req.UserAgent = "Mozilla/5.0";
                HttpWebResponse r; try { r = (HttpWebResponse)req.GetResponse(); } catch (WebException ex) { r = ex.Response as HttpWebResponse; if (r == null) break; }
                using (r) { var loc = r.Headers["Location"]; if ((int)r.StatusCode < 300 || (int)r.StatusCode >= 400 || string.IsNullOrEmpty(loc)) break; cur = new Uri(new Uri(cur), loc).AbsoluteUri; chain.Add(cur); }
            }
            return chain;
        }
        static CyFinding Fd(string lvl, string h, string item, string t, string fix, string rf) { return new CyFinding { Level = lvl, Host = h, Item = item.Length > 90 ? item.Substring(0, 90) + "…" : item, Title = t, Fix = fix, Ref = rf }; }
    }

    // ===================================================================== analyse d'un e-mail (source .eml ou en-têtes collés)
    static class Phish
    {
        public static Dictionary<string, List<string>> Headers(string raw, out string body)
        {
            var h = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var t = (raw ?? "").Replace("\r\n", "\n"); var cut = t.IndexOf("\n\n"); var head = cut >= 0 ? t.Substring(0, cut) : t; body = cut >= 0 ? t.Substring(cut + 2) : "";
            string last = null;
            foreach (var line in head.Split('\n'))
            {
                if ((line.StartsWith(" ") || line.StartsWith("\t")) && last != null) { var lst = h[last]; lst[lst.Count - 1] += " " + line.Trim(); continue; }
                var i = line.IndexOf(':'); if (i <= 0) continue; last = line.Substring(0, i).Trim();
                List<string> v; if (!h.TryGetValue(last, out v)) h[last] = v = new List<string>(); v.Add(line.Substring(i + 1).Trim());
            }
            return h;
        }
        static string H(Dictionary<string, List<string>> h, string k) { List<string> v; return h.TryGetValue(k, out v) && v.Count > 0 ? v[0] : ""; }
        public static string Addr(string s) { var m = Regex.Match(s ?? "", @"<([^>]+)>"); var a = m.Success ? m.Groups[1].Value : (s ?? "").Trim(); return a.Trim().Trim('"').ToLowerInvariant(); }
        public static string Dom(string addr) { var i = addr.LastIndexOf('@'); return i >= 0 ? addr.Substring(i + 1).Trim('>', ' ') : ""; }
        static string Decode(string s)   // =?utf-8?B?...?= / =?utf-8?Q?...?=
        {
            return Regex.Replace(s ?? "", @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", m =>
            {
                try
                {
                    var enc = Encoding.GetEncoding(m.Groups[1].Value);
                    if (m.Groups[2].Value.ToUpperInvariant() == "B") return enc.GetString(Convert.FromBase64String(m.Groups[3].Value));
                    var q = m.Groups[3].Value.Replace('_', ' '); var bytes = new List<byte>();
                    for (int i = 0; i < q.Length; i++) { if (q[i] == '=' && i + 2 < q.Length) { bytes.Add(Convert.ToByte(q.Substring(i + 1, 2), 16)); i += 2; } else bytes.Add((byte)q[i]); }
                    return enc.GetString(bytes.ToArray());
                }
                catch { return m.Value; }
            });
        }

        public static List<CyFinding> Analyze(string raw)
        {
            string body; var h = Headers(raw, out body); var l = new List<CyFinding>();
            var from = Decode(H(h, "From")); var fa = Addr(from); var fd = Dom(fa); var who = fa.Length > 0 ? fa : "(expéditeur absent)";
            Action<string, string, string, string, string> add = (lvl, item, t, fix, rf) => l.Add(new CyFinding { Level = lvl, Host = who, Item = item, Title = t, Fix = fix, Ref = rf });
            if (h.Count == 0 || fa.Length == 0) { add("warn", "en-têtes", "En-têtes introuvables : colle la SOURCE complète (Outlook : Fichier > Propriétés > En-têtes Internet, ou enregistrer en .eml)", "", ""); }

            var auth = string.Join(" ", h.Where(x => x.Key.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("ARC-Authentication-Results", StringComparison.OrdinalIgnoreCase)).SelectMany(x => x.Value)).ToLowerInvariant();
            Func<string, string> res = k => { var m = Regex.Match(auth, k + @"=(\w+)"); return m.Success ? m.Groups[1].Value : ""; };
            string spf = res("spf"), dkim = res("dkim"), dmarc = res("dmarc");
            if (auth.Length == 0) add("info", "authentification", "Aucun résultat SPF/DKIM/DMARC dans les en-têtes (message interne ou en-têtes incomplets)", "", "");
            else
            {
                add(spf == "pass" ? "ok" : spf == "fail" ? "bad" : "warn", "SPF", "SPF : " + (spf.Length > 0 ? spf : "absent") + (spf == "pass" ? " (serveur d'envoi autorisé)" : " : le serveur d'envoi n'est PAS autorisé par le domaine"), spf == "pass" ? "" : "Indice fort d'usurpation si le domaine est connu.", "RFC 7208");
                add(dkim == "pass" ? "ok" : dkim == "fail" ? "bad" : "warn", "DKIM", "DKIM : " + (dkim.Length > 0 ? dkim : "absent") + (dkim == "pass" ? " (message non modifié, signé)" : ""), "", "RFC 6376");
                add(dmarc == "pass" ? "ok" : dmarc == "fail" ? "bad" : "warn", "DMARC", "DMARC : " + (dmarc.Length > 0 ? dmarc : "absent") + (dmarc == "fail" ? " : le domaine affiché dans « De » est USURPÉ" : ""), dmarc == "fail" ? "Ne pas répondre, ne rien ouvrir." : "", "RFC 7489");
            }
            var disp = Regex.Match(from, @"^\s*""?([^""<]*)""?\s*<").Groups[1].Value.Trim();
            var dispMail = Regex.Match(disp, @"[\w.+\-]+@([\w\-]+\.)+[a-z]{2,}", RegexOptions.IgnoreCase);
            if (dispMail.Success && !dispMail.Value.Equals(fa, StringComparison.OrdinalIgnoreCase)) add("bad", "De", "Nom affiché trompeur : « " + disp + " » alors que l'adresse réelle est " + fa, "Usurpation classique : regarder l'adresse, pas le nom.", "MITRE T1566");
            var look = fd.Length > 0 ? LinkCheck.Lookalike(fd) : null; if (look != null) add("bad", "De", "Domaine de l'expéditeur trompeur : " + fd + " — " + look, "Ne pas répondre ; signaler.", "MITRE T1583.001");
            var rt = Addr(Decode(H(h, "Reply-To"))); if (rt.Length > 0 && Dom(rt) != fd) add("warn", "Répondre à", "Les réponses partent vers un AUTRE domaine : " + rt, "Technique d'arnaque au virement / au président.", "MITRE T1566");
            var rp = Addr(H(h, "Return-Path")); if (rp.Length > 0 && fd.Length > 0 && LinkCheck.Reg(Dom(rp)) != LinkCheck.Reg(fd)) add("info", "Return-Path", "Adresse de retour d'un autre domaine : " + rp + " (normal pour un routeur d'e-mails, suspect sinon)", "", "");
            var subj = Decode(H(h, "Subject"));
            if (Regex.IsMatch(subj + " " + body.Substring(0, Math.Min(body.Length, 4000)), @"urgent|imm[ée]diat|suspendu|bloqu[ée]|v[ée]rifi(ez|er) votre|mot de passe expir|facture impay|virement|nouveau RIB|changement de RIB|colis en attente|frais de livraison|remboursement|amende|gift ?card|password expir|verify your account", RegexOptions.IgnoreCase))
                add("warn", "contenu", "Formules de pression typiques de l'hameçonnage (urgence, compte bloqué, RIB, colis, remboursement)", "Vérifier par un autre canal (téléphone connu).", "MITRE T1566");

            // liens : texte affiché ≠ destination, IP loggers, raccourcisseurs, sosies
            var html = Regex.Replace(body, @"=\r?\n", "").Replace("=3D", "=");
            foreach (Match m in Regex.Matches(html, @"<a\b[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var href = WebUtility.HtmlDecode(m.Groups[1].Value); var txt = Regex.Replace(WebUtility.HtmlDecode(m.Groups[2].Value), "<[^>]+>", "").Trim();
                var tm = Regex.Match(txt, @"(https?://)?([\w\-]+\.)+[a-z]{2,}", RegexOptions.IgnoreCase); Uri hu;
                if (tm.Success && Uri.TryCreate(href, UriKind.Absolute, out hu) && LinkCheck.Reg(hu.Host) != LinkCheck.Reg(Regex.Replace(tm.Value, "^https?://", "").Split('/')[0]))
                    add("bad", "lien", "Lien déguisé : affiche « " + tm.Value + " » mais mène à " + hu.Host, "Ne pas cliquer.", "MITRE T1566.002");
                l.AddRange(LinkCheck.Analyze(href, "lien").Select(x => { x.Host = who; return x; }));
            }
            foreach (Match m in Regex.Matches(Regex.Replace(html, @"<[^>]+>", " "), @"https?://[^\s""'<>)]+", RegexOptions.IgnoreCase))
                if (!l.Any(x => x.Item.StartsWith(m.Value.Length > 90 ? m.Value.Substring(0, 90) : m.Value))) l.AddRange(LinkCheck.Analyze(m.Value, "lien").Select(x => { x.Host = who; return x; }));

            // pièces jointes dangereuses
            foreach (Match m in Regex.Matches(body, @"(?:file)?name\*?=""?([^"";\r\n]+)""?", RegexOptions.IgnoreCase))
            {
                var n = Decode(m.Groups[1].Value.Trim()); var ext = Path.GetExtension(n).ToLowerInvariant();
                if (Regex.IsMatch(n, @"\.(pdf|docx?|xlsx?|jpg|png|txt)\.(exe|scr|js|vbs|hta|bat|cmd|lnk)$", RegexOptions.IgnoreCase)) add("bad", "pièce jointe", "Double extension piégée : " + n, "Ne pas ouvrir.", "MITRE T1036.007");
                else if (Regex.IsMatch(ext, @"^\.(exe|scr|js|jse|vbs|vbe|wsf|hta|bat|cmd|ps1|msi|lnk|iso|img|vhdx?|one|xll|cpl)$")) add("bad", "pièce jointe", "Pièce jointe exécutable : " + n, "Ne pas ouvrir ; supprimer le message.", "MITRE T1204.002");
                else if (Regex.IsMatch(ext, @"^\.(docm|xlsm|pptm|zip|rar|7z|html?|svg)$")) add("warn", "pièce jointe", "Pièce jointe à risque (macros, archive ou page HTML) : " + n, "N'ouvrir qu'après vérification auprès de l'expéditeur.", "MITRE T1566.001");
            }
            var dedup = l.GroupBy(x => x.Item + "|" + x.Title).Select(g => g.First()).ToList();
            return dedup.OrderBy(x => x.Level == "bad" ? 0 : x.Level == "warn" ? 1 : x.Level == "info" ? 2 : 3).ToList();
        }
        public static string Verdict(List<CyFinding> l) { var b = l.Count(x => x.Level == "bad"); var w = l.Count(x => x.Level == "warn"); return b >= 1 ? "HAMEÇONNAGE TRÈS PROBABLE" : w >= 2 ? "SUSPECT" : w == 1 ? "à vérifier" : "aucun signe d'hameçonnage"; }
    }

    // ===================================================================== détection des scans de ports visant ce PC (journal du pare-feu Windows)
    static class ScanWatch
    {
        public static string LogFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\LogFiles\Firewall\pfirewall.log"); } }
        public const string Enable = "netsh advfirewall set allprofiles logging droppedconnections enable & netsh advfirewall set allprofiles logging maxfilesize 32767 & netsh advfirewall set allprofiles logging filename %systemroot%\\system32\\LogFiles\\Firewall\\pfirewall.log";

        public class Hit { public string Src; public DateTime First, Last; public HashSet<int> Ports = new HashSet<int>(); public int Packets, Icmp; public string Proto = ""; }

        // format W3C : date time action protocol src-ip dst-ip src-port dst-port size tcpflags …
        public static List<Hit> Parse(IEnumerable<string> lines, int window = 60)
        {
            var bySrc = new Dictionary<string, List<Hit>>();
            foreach (var line in lines)
            {
                if (line.StartsWith("#")) continue; var p = line.Split(' '); if (p.Length < 8 || p[2] != "DROP" || p[7] == "-" && p[3] != "ICMP") continue;
                DateTime t; if (!DateTime.TryParseExact(p[0] + " " + p[1], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                List<Hit> l; if (!bySrc.TryGetValue(p[4], out l)) bySrc[p[4]] = l = new List<Hit>();
                var h = l.LastOrDefault(); if (h == null || (t - h.Last).TotalSeconds > window) { h = new Hit { Src = p[4], First = t }; l.Add(h); }
                h.Last = t; h.Packets++; if (p[3] == "ICMP") h.Icmp++; else { int port; if (int.TryParse(p[7], out port)) h.Ports.Add(port); if (!h.Proto.Contains(p[3])) h.Proto += (h.Proto.Length > 0 ? "+" : "") + p[3]; }
            }
            return bySrc.Values.SelectMany(x => x).Where(h => h.Ports.Count >= 5 || h.Icmp >= 10).OrderByDescending(h => h.Ports.Count).ToList();
        }

        public static List<CyFinding> Findings(List<Hit> hits)
        {
            return hits.Select(h => new CyFinding
            {
                Level = h.Ports.Count >= 15 ? "bad" : "warn", Host = h.Src, Item = h.First.ToString("dd/MM HH:mm:ss") + " → " + h.Last.ToString("HH:mm:ss"),
                Title = (h.Ports.Count >= 15 ? "SCAN DE PORTS" : h.Ports.Count >= 5 ? "Sondage de ports" : "Balayage ICMP (ping)") + " : " + h.Ports.Count + " port(s) " + h.Proto + " en " + Math.Max(1, (int)(h.Last - h.First).TotalSeconds) + " s, " + h.Packets + " paquet(s) bloqué(s)" + (h.Ports.Count > 0 ? " (" + string.Join(",", h.Ports.OrderBy(x => x).Take(12)) + (h.Ports.Count > 12 ? "…" : "") + ")" : ""),
                Fix = "Identifier la machine source ; si inconnue : isoler, bloquer l'IP au pare-feu, vérifier les comptes.", Ref = "MITRE T1046 · détection"
            }).ToList();
        }

        public static List<string> Tail(long fromPos, out long newPos)
        {
            var l = new List<string>(); newPos = fromPos;
            if (!File.Exists(LogFile)) return l;
            using (var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fromPos > fs.Length) fromPos = 0; fs.Seek(fromPos, SeekOrigin.Begin);
                using (var sr = new StreamReader(fs, Encoding.ASCII)) { string s; while ((s = sr.ReadLine()) != null) l.Add(s); newPos = fs.Position; }
            }
            return l;
        }
    }

    // ===================================================================== durcissement anti-scan (réversible)
    static class Harden
    {
        // état précédent enregistré dans Prefs pour le retour arrière
        public const string Apply =
            "$ErrorActionPreference='Continue';$k='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\DNSClient';" +
            "$old=(Get-ItemProperty $k -Name EnableMulticast -EA SilentlyContinue).EnableMulticast;'PREV|LLMNR|'+$(if($null -eq $old){'absent'}else{$old});" +
            "New-Item $k -Force|Out-Null;Set-ItemProperty $k -Name EnableMulticast -Value 0 -Type DWord;'OK|LLMNR désactivé (empoisonnement Responder)';" +
            "Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True'|%{'PREV|NBT|'+$_.Index+'|'+$_.TcpipNetbiosOptions;$null=Invoke-CimMethod -InputObject $_ -MethodName SetTcpipNetbios -Arguments @{TcpipNetbiosOptions=[uint32]2}};'OK|NetBIOS sur TCP/IP désactivé';" +
            "Get-NetFirewallProfile|%{'PREV|FW|'+$_.Name+'|'+$_.Enabled+'|'+$_.DefaultInboundAction};Set-NetFirewallProfile -All -Enabled True -DefaultInboundAction Block;'OK|Pare-feu actif, entrées bloquées par défaut (3 profils)';" +
            "Set-NetFirewallProfile -Profile Public -AllowInboundRules False -EA SilentlyContinue;'OK|Profil Public : aucune règle entrante (furtif sur réseau public)';" +
            "netsh advfirewall set allprofiles logging droppedconnections enable|Out-Null;'OK|Journal des paquets bloqués activé (détection des scans)'";

        public static string Restore(string prev)
        {
            var sb = new StringBuilder("$ErrorActionPreference='Continue';");
            foreach (var l in prev.Split('\n').Select(x => x.Trim()).Where(x => x.StartsWith("PREV|")))
            {
                // seules des valeurs numériques ou des mots simples sont réinjectées : une ligne altérée est ignorée
                var p = l.Split('|'); int a, b;
                if (p.Length > 2 && p[1] == "LLMNR" && (p[2] == "absent" || int.TryParse(p[2], out a))) sb.Append(p[2] == "absent" ? "Remove-ItemProperty 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\DNSClient' -Name EnableMulticast -EA SilentlyContinue;" : "Set-ItemProperty 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\DNSClient' -Name EnableMulticast -Value " + int.Parse(p[2]) + " -Type DWord;");
                else if (p.Length > 3 && p[1] == "NBT" && int.TryParse(p[2], out a) && int.TryParse(p[3], out b)) sb.Append("$a=Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'Index=" + a + "';if($a){$null=Invoke-CimMethod -InputObject $a -MethodName SetTcpipNetbios -Arguments @{TcpipNetbiosOptions=[uint32]" + b + "}};");
                else if (p[1] == "FW" && p.Length > 4 && Regex.IsMatch(p[2], @"^\w+$") && Regex.IsMatch(p[4], @"^\w+$")) sb.Append("Set-NetFirewallProfile -Profile " + p[2] + " -Enabled " + (p[3] == "True" ? "True" : "False") + " -DefaultInboundAction " + p[4] + ";" + (p[2] == "Public" ? "Set-NetFirewallProfile -Profile Public -AllowInboundRules NotConfigured -EA SilentlyContinue;" : ""));
            }
            return sb.Append("'OK|restauré'").ToString();
        }
    }

    // ===================================================================== test de sécurité d'un programme
    static class ProgTest
    {
        public class Pe { public string Arch = ""; public bool Dotnet, Aslr, HighEntropy, Dep, Cfg, NoSeh, Is64; public int Sections; public string Error; }

        public static Pe Read(string file)
        {
            var r = new Pe();
            try
            {
                using (var br = new BinaryReader(File.OpenRead(file)))
                {
                    if (br.ReadUInt16() != 0x5A4D) { r.Error = "pas un exécutable Windows (signature MZ absente)"; return r; }
                    br.BaseStream.Seek(0x3C, SeekOrigin.Begin); var pe = br.ReadInt32(); br.BaseStream.Seek(pe, SeekOrigin.Begin);
                    if (br.ReadUInt32() != 0x00004550) { r.Error = "en-tête PE absent"; return r; }
                    var machine = br.ReadUInt16(); r.Sections = br.ReadUInt16(); br.BaseStream.Seek(12, SeekOrigin.Current); var optSize = br.ReadUInt16(); br.ReadUInt16();
                    if (optSize < 96) { r.Error = "en-tête optionnel PE invalide"; return r; }
                    var optStart = br.BaseStream.Position; var magic = br.ReadUInt16(); r.Is64 = magic == 0x20B;
                    r.Arch = machine == 0x8664 ? "x64" : machine == 0x14C ? "x86" : machine == 0xAA64 ? "ARM64" : "0x" + machine.ToString("X");
                    br.BaseStream.Seek(optStart + 70, SeekOrigin.Begin); var dll = br.ReadUInt16();
                    r.HighEntropy = (dll & 0x20) != 0; r.Aslr = (dll & 0x40) != 0; r.Dep = (dll & 0x100) != 0; r.NoSeh = (dll & 0x400) != 0; r.Cfg = (dll & 0x4000) != 0;
                    var ddStart = optStart + (r.Is64 ? 112 : 96); br.BaseStream.Seek(ddStart + 14 * 8, SeekOrigin.Begin);   // répertoire 14 = en-tête CLR (.NET)
                    r.Dotnet = br.ReadUInt32() != 0;
                }
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        public static List<CyFinding> Evaluate(string file, Pe p, string sig)
        {
            var n = Path.GetFileName(file); var l = new List<CyFinding>();
            Action<string, string, string, string> add = (lvl, t, fix, rf) => l.Add(new CyFinding { Level = lvl, Host = n, Item = p.Arch + (p.Dotnet ? " .NET" : ""), Title = t, Fix = fix, Ref = rf });
            if (p.Error != null) { add("warn", "Lecture impossible : " + p.Error, "", ""); return l; }
            var st = (sig ?? "").Split('|')[0];
            add(st == "Valid" ? "ok" : st == "NotSigned" ? "warn" : "bad", st == "Valid" ? "Signé : " + Regex.Replace((sig ?? "").Split('|').Skip(1).FirstOrDefault() ?? "", @"^CN=""?([^,""]+).*", "$1") : st == "NotSigned" ? "Non signé : éditeur invérifiable" : "Signature INVALIDE (" + st + ") : fichier modifié ou certificat non approuvé", st == "Valid" ? "" : "Vérifier la provenance ; VirusTotal.", "");
            add(p.Aslr ? "ok" : "warn", "ASLR (adresses aléatoires) : " + (p.Aslr ? "oui" : "NON"), p.Aslr ? "" : "Compiler avec /DYNAMICBASE.", "CWE-1188");
            if (p.Is64) add(p.HighEntropy ? "ok" : "info", "ASLR haute entropie (64 bits) : " + (p.HighEntropy ? "oui" : "non"), "", "");
            add(p.Dep ? "ok" : "warn", "DEP / NX (pile non exécutable) : " + (p.Dep ? "oui" : "NON"), p.Dep ? "" : "Compiler avec /NXCOMPAT.", "");
            if (!p.Dotnet) add(p.Cfg ? "ok" : "info", "Control Flow Guard : " + (p.Cfg ? "oui" : "non"), p.Cfg ? "" : "Compiler avec /guard:cf (code natif).", "");
            else add("info", "Programme .NET : code managé (protections mémoire assurées par le CLR)", "", "");
            return l;
        }

        // comportement : photo avant / après exécution (à faire dans une VM)
        public static HashSet<string> Snapshot(string persistOut, string connOut)
        {
            var s = new HashSet<string>();
            foreach (var l in persistOut.Split('\n').Select(x => x.Trim()).Where(x => x.StartsWith("P|") && !x.StartsWith("P|END"))) { var p = l.Split('|'); s.Add("Démarrage · " + p[1] + " · " + p[2] + " · " + (p.Length > 3 ? p[3] : "")); }
            foreach (var l in connOut.Split('\n').Select(x => x.Trim()).Where(x => x.StartsWith("C|Listen|"))) { var p = l.Split('|'); s.Add("Port en écoute · " + p[2] + ":" + p[3] + " · " + (p.Length > 8 ? p[8] : "")); }
            return s;
        }
        public static List<CyFinding> Diff(HashSet<string> before, HashSet<string> after)
        {
            var l = after.Except(before).OrderBy(x => x).Select(x => new CyFinding { Level = x.StartsWith("Démarrage") ? "bad" : "warn", Host = Environment.MachineName, Item = "AJOUTÉ", Title = x, Fix = x.StartsWith("Démarrage") ? "Le programme s'installe au démarrage : persistance à justifier." : "Le programme ouvre un port : vérifier son utilité et le pare-feu.", Ref = x.StartsWith("Démarrage") ? "MITRE T1547" : "MITRE T1571" }).ToList();
            l.AddRange(before.Except(after).OrderBy(x => x).Select(x => new CyFinding { Level = "info", Host = Environment.MachineName, Item = "RETIRÉ", Title = x, Fix = "", Ref = "" }));
            return l;
        }
    }
}
