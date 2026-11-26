// MailCheck - contrôle de la messagerie d'un domaine (MX, SPF, DKIM, DMARC, serveurs)
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlexB;

[assembly: AssemblyTitle("MailCheck")]
[assembly: AssemblyDescription("Contrôle de la messagerie d'un domaine")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("MailCheck")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace MailCheck
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            AppInfo.Name = "MailCheck"; AppInfo.Id = "MailCheck"; AppInfo.Version = "1.0.0"; AppInfo.MachineWide = false;
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; } catch { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            Boot.Run(() => new MainForm(), false, null);
        }
    }

    // ===================================================================== Client DNS minimal (UDP + repli TCP)
    class Rr { public string Name; public int Type; public string Data; public int Pref; }

    static class Dns2
    {
        public const int A = 1, CNAME = 5, MX = 15, TXT = 16;
        static List<IPAddress> servers;

        public static List<IPAddress> Servers
        {
            get
            {
                if (servers != null) return servers;
                var l = new List<IPAddress>();
                try
                {
                    foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                        if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                            foreach (var d in n.GetIPProperties().DnsAddresses)
                                if (d.AddressFamily == AddressFamily.InterNetwork && !l.Contains(d)) l.Add(d);
                }
                catch { }
                l.Add(IPAddress.Parse("1.1.1.1")); l.Add(IPAddress.Parse("8.8.8.8"));   // secours
                return servers = l;
            }
        }

        static byte[] Build(string name, int type, ushort id)
        {
            var p = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var b = Encoding.ASCII.GetBytes(label);
                if (b.Length == 0 || b.Length > 63) throw new ArgumentException("nom de domaine invalide");
                p.Add((byte)b.Length); p.AddRange(b);
            }
            p.Add(0); p.Add((byte)(type >> 8)); p.Add((byte)type); p.Add(0); p.Add(1);
            return p.ToArray();
        }

        static string ReadName(byte[] m, ref int pos)
        {
            var parts = new List<string>(); int jumps = 0; int p = pos; bool jumped = false;
            while (true)
            {
                if (p >= m.Length) throw new FormatException();
                int len = m[p];
                if (len == 0) { p++; break; }
                if ((len & 0xC0) == 0xC0)
                {
                    int ptr = ((len & 0x3F) << 8) | m[p + 1];
                    if (!jumped) pos = p + 2;
                    jumped = true; p = ptr;
                    if (++jumps > 20) throw new FormatException();
                    continue;
                }
                parts.Add(Encoding.ASCII.GetString(m, p + 1, len)); p += len + 1;
            }
            if (!jumped) pos = p;
            return string.Join(".", parts).ToLowerInvariant();
        }

        // rcode : 0 = OK, 3 = NXDOMAIN, -1 = pas de réponse
        public static List<Rr> Query(string name, int type, out int rcode)
        {
            rcode = -1;
            foreach (var srv in Servers)
            {
                try
                {
                    var id = (ushort)new Random().Next(1, 65535);
                    var q = Build(name, type, id);
                    byte[] resp;
                    using (var u = new UdpClient(AddressFamily.InterNetwork))
                    {
                        u.Client.ReceiveTimeout = 2500;
                        u.Send(q, q.Length, new IPEndPoint(srv, 53));
                        var ep = new IPEndPoint(IPAddress.Any, 0);
                        resp = u.Receive(ref ep);
                    }
                    if (resp.Length < 12 || resp[0] != (byte)(id >> 8) || resp[1] != (byte)id) continue;
                    if ((resp[2] & 0x02) != 0) resp = Tcp(srv, q) ?? resp;   // réponse tronquée → TCP
                    rcode = resp[3] & 0x0F;
                    if (rcode != 0 && rcode != 3) continue;                    // SERVFAIL… → serveur suivant
                    return Parse(resp);
                }
                catch { }
            }
            return new List<Rr>();
        }

        static byte[] Tcp(IPAddress srv, byte[] q)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    if (!c.ConnectAsync(srv, 53).Wait(2500)) return null;
                    var s = c.GetStream(); s.ReadTimeout = 3000;
                    s.Write(new[] { (byte)(q.Length >> 8), (byte)q.Length }, 0, 2); s.Write(q, 0, q.Length);
                    var h = new byte[2]; if (s.Read(h, 0, 2) != 2) return null;
                    int len = (h[0] << 8) | h[1]; var buf = new byte[len]; int got = 0, n;
                    while (got < len && (n = s.Read(buf, got, len - got)) > 0) got += n;
                    return got == len ? buf : null;
                }
            }
            catch { return null; }
        }

        static List<Rr> Parse(byte[] m)
        {
            var list = new List<Rr>();
            int qd = (m[4] << 8) | m[5], an = (m[6] << 8) | m[7], pos = 12;
            for (int i = 0; i < qd; i++) { ReadName(m, ref pos); pos += 4; }
            for (int i = 0; i < an; i++)
            {
                var r = new Rr { Name = ReadName(m, ref pos) };
                r.Type = (m[pos] << 8) | m[pos + 1]; int rdlen = (m[pos + 8] << 8) | m[pos + 9]; pos += 10;
                int start = pos;
                if (r.Type == A && rdlen == 4) r.Data = m[pos] + "." + m[pos + 1] + "." + m[pos + 2] + "." + m[pos + 3];
                else if (r.Type == CNAME) { int p = pos; r.Data = ReadName(m, ref p); }
                else if (r.Type == MX) { r.Pref = (m[pos] << 8) | m[pos + 1]; int p = pos + 2; r.Data = ReadName(m, ref p); }
                else if (r.Type == TXT)
                {
                    var sb = new StringBuilder(); int p = pos;
                    while (p < start + rdlen) { int l = m[p]; sb.Append(Encoding.UTF8.GetString(m, p + 1, l)); p += l + 1; }   // chaînes concaténées
                    r.Data = sb.ToString();
                }
                pos = start + rdlen;
                if (r.Data != null) list.Add(r);
            }
            return list;
        }

        public static List<string> Txt(string name) { int rc; return Query(name, TXT, out rc).Where(r => r.Type == TXT).Select(r => r.Data).ToList(); }
    }

    // ===================================================================== Contrôles
    class Checker
    {
        readonly LogBox log;
        readonly Action<string> step;
        public string Verdict = "", Detail = "", VColor = "ok";
        int bad, warn;
        readonly List<string> issues = new List<string>();

        public Checker(LogBox l, Action<string> s) { log = l; step = s; }

        void Head(string s) { log.Line("", "text"); log.Line("// " + s, "accent", true); step(s.ToLowerInvariant()); }
        void Row(string k, string v, string c)
        {
            log.Line("  " + (k + " ").PadRight(26, '·') + " " + v, c);
            if (c == "bad") { bad++; issues.Add(k + " : " + v); } else if (c == "warn") { warn++; issues.Add(k + " : " + v); }
        }

        static string Provider(IEnumerable<string> mx)
        {
            var all = string.Join(" ", mx);
            if (all.Contains("mail.protection.outlook.com")) return "Microsoft 365";
            if (all.Contains("ovh.net") || all.Contains("ovh.com")) return "OVHcloud";
            if (all.Contains("google.com") || all.Contains("googlemail.com")) return "Google Workspace";
            if (all.Contains("o2switch")) return "o2switch";
            if (all.Contains("ionos") || all.Contains("kundenserver")) return "IONOS";
            if (all.Contains("gandi.net")) return "Gandi";
            if (all.Contains("orange.fr") || all.Contains("wanadoo")) return "Orange";
            if (all.Contains("mailinblack") || all.Contains("vadesecure") || all.Contains("altospam") || all.Contains("mimecast") || all.Contains("pphosted")) return "Filtre antispam devant la messagerie";
            return "";
        }

        // nombre de requêtes DNS consommées par un SPF (limite RFC 7208 : 10)
        static int SpfLookups(string domain, int depth, HashSet<string> seen)
        {
            if (depth > 10 || !seen.Add(domain)) return 0;
            var spf = Dns2.Txt(domain).FirstOrDefault(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase));
            if (spf == null) return 0;
            int n = 0;
            foreach (var term in spf.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = term.TrimStart('+', '-', '~', '?').ToLowerInvariant();
                if (t.StartsWith("include:")) { n++; n += SpfLookups(t.Substring(8), depth + 1, seen); }
                else if (t.StartsWith("redirect=")) { n++; n += SpfLookups(t.Substring(9), depth + 1, seen); }
                else if (t == "a" || t.StartsWith("a:") || t.StartsWith("a/") || t == "mx" || t.StartsWith("mx:") || t.StartsWith("mx/") || t.StartsWith("ptr") || t.StartsWith("exists:")) n++;
            }
            return n;
        }

        // Port + TLS : implicite (993/995/465) ou STARTTLS (587/25) ; renvoie le certificat
        static string TlsTest(string host, int port, bool starttls, out string color)
        {
            color = "bad";
            try
            {
                using (var c = new TcpClient())
                {
                    var sw = Stopwatch.StartNew();
                    if (!c.ConnectAsync(host, port).Wait(4000)) return "pas de réponse (port filtré ?)";
                    long ms = sw.ElapsedMilliseconds;
                    Stream s = c.GetStream(); s.ReadTimeout = 5000; s.WriteTimeout = 5000;
                    if (starttls)
                    {
                        var r = new StreamReader(s, Encoding.ASCII); var w = new StreamWriter(s, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                        string l = r.ReadLine(); if (l == null || !l.StartsWith("220")) return "bannière inattendue : " + (l ?? "rien");
                        w.WriteLine("EHLO mailcheck.local");
                        bool tls = false; while ((l = r.ReadLine()) != null) { if (l.ToUpperInvariant().Contains("STARTTLS")) tls = true; if (l.Length > 3 && l[3] == ' ') break; }
                        if (!tls) { color = "warn"; return "joignable (" + ms + " ms) mais STARTTLS non proposé"; }
                        w.WriteLine("STARTTLS"); l = r.ReadLine();
                        if (l == null || !l.StartsWith("220")) return "STARTTLS refusé : " + l;
                    }
                    X509Certificate2 cert = null; SslPolicyErrors errs = SslPolicyErrors.None;
                    using (var ssl = new SslStream(s, false, (o, ce, ch, e) => { cert = new X509Certificate2(ce); errs = e; return true; }))
                    {
                        ssl.AuthenticateAsClient(host);
                        if (starttls) { }   // pas de commande envoyée après la négociation : aucun identifiant n'est transmis
                        if (cert == null) return "TLS sans certificat ?";
                        int days = (int)(cert.NotAfter - DateTime.Now).TotalDays;
                        string who = cert.GetNameInfo(X509NameType.SimpleName, false);
                        string txt = "OK " + ms + " ms · " + ssl.SslProtocol.ToString().Replace("Tls", "TLS ").Replace("TLS 12", "TLS 1.2").Replace("TLS 13", "TLS 1.3") + " · certificat " + who + " jusqu'au " + cert.NotAfter.ToString("dd/MM/yyyy");
                        if (errs != SslPolicyErrors.None) { color = "bad"; return txt + " — CERTIFICAT INVALIDE (" + errs + ")"; }
                        color = days < 15 ? "warn" : "ok";
                        return txt + (days < 15 ? " — expire dans " + days + " j" : "");
                    }
                }
            }
            catch (Exception ex) { return "échec : " + (ex.InnerException ?? ex).Message; }
        }

        public void Run(string domain, string customHost)
        {
            domain = domain.Trim().ToLowerInvariant().TrimEnd('.');
            if (domain.Contains("@")) domain = domain.Substring(domain.IndexOf('@') + 1);
            log.Line("CONTRÔLE MESSAGERIE — " + domain + " — " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " (MailCheck, by alex.b)", "text", true);
            log.Line("Serveurs DNS utilisés : " + string.Join(", ", Dns2.Servers.Take(4)), "muted");

            // ---------------------------------------------------------- MX
            Head("MX (réception)");
            int rc; var mx = Dns2.Query(domain, Dns2.MX, out rc).Where(r => r.Type == Dns2.MX).OrderBy(r => r.Pref).ToList();
            if (rc == 3) { Row("Domaine", "N'EXISTE PAS (NXDOMAIN)", "bad"); Finish(); return; }
            if (rc == -1) { Row("DNS", "aucun serveur DNS ne répond", "bad"); Finish(); return; }
            if (mx.Count == 0) Row("Enregistrements MX", "AUCUN : le domaine ne peut pas recevoir de mail", "bad");
            foreach (var r in mx)
            {
                int rca; var a = Dns2.Query(r.Data, Dns2.A, out rca).Where(x => x.Type == Dns2.A).Select(x => x.Data).ToList();
                Row("MX " + r.Pref, r.Data + (a.Count > 0 ? "  →  " + string.Join(", ", a.Take(3)) : "  →  NE RÉSOUT PAS"), a.Count > 0 ? "text" : "bad");
            }
            string prov = Provider(mx.Select(r => r.Data));
            if (mx.Count > 0) Row("Hébergeur détecté", prov.Length > 0 ? prov : "non reconnu (serveur dédié / autre)", "text");

            // ---------------------------------------------------------- SPF
            Head("SPF (qui a le droit d'envoyer)");
            var txt = Dns2.Txt(domain);
            var spfs = txt.Where(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();
            if (spfs.Count == 0) Row("SPF", "ABSENT : les mails partent souvent en spam", "bad");
            else if (spfs.Count > 1) Row("SPF", spfs.Count + " enregistrements : INVALIDE (un seul autorisé, à fusionner)", "bad");
            else
            {
                var spf = spfs[0]; log.Line("    " + spf, "muted");
                var all = Regex.Match(spf, @"([-~?+]?)all\b");
                if (!all.Success && !spf.Contains("redirect=")) Row("Fin du SPF", "pas de « all » : SPF sans effet", "warn");
                else if (all.Success && (all.Groups[1].Value == "+" || all.Groups[1].Value == "")) Row("Fin du SPF", "+all : TOUT LE MONDE peut envoyer en ton nom", "bad");
                else if (all.Success && all.Groups[1].Value == "?") Row("Fin du SPF", "?all : neutre, aucune protection", "warn");
                else if (all.Success) Row("Fin du SPF", all.Groups[1].Value + "all " + (all.Groups[1].Value == "-" ? "(strict)" : "(souple, accepté)"), "ok");
                int n = SpfLookups(domain, 0, new HashSet<string>());
                Row("Requêtes DNS du SPF", n + " / 10" + (n > 10 ? " : DÉPASSÉ, le SPF est rejeté (permerror)" : ""), n > 10 ? "bad" : n > 8 ? "warn" : "ok");
                var need = prov == "Microsoft 365" ? "spf.protection.outlook.com" : prov == "OVHcloud" ? "mx.ovh.com" : prov == "Google Workspace" ? "_spf.google.com" : null;
                if (need != null) Row("Include " + prov, spf.Contains(need) ? "présent" : "ABSENT (include:" + need + ")", spf.Contains(need) ? "ok" : "bad");
            }

            // ---------------------------------------------------------- DKIM
            Head("DKIM (signature des mails)");
            var selectors = new[] { "selector1", "selector2", "google", "default", "dkim", "mail", "k1", "s1", "s2", "ovhmo-selector-1", "ovhmo-selector-2", "x" };
            var foundSel = new List<string>();
            foreach (var sel in selectors)
            {
                int rcs; var res = Dns2.Query(sel + "._domainkey." + domain, Dns2.TXT, out rcs);
                var cn = res.FirstOrDefault(r => r.Type == Dns2.CNAME);
                var key = res.FirstOrDefault(r => r.Type == Dns2.TXT && (r.Data.Contains("p=") || r.Data.Contains("v=DKIM1")));
                if (key != null)
                {
                    foundSel.Add(sel);
                    bool revoked = Regex.IsMatch(key.Data, @"p=\s*(;|$)");
                    Row("Sélecteur " + sel, (cn != null ? "CNAME → " + cn.Data + " · " : "") + (revoked ? "clé vide (révoquée)" : "clé publiée"), revoked ? "warn" : "ok");
                }
                else if (cn != null) Row("Sélecteur " + sel, "CNAME → " + cn.Data + " mais aucune clé derrière (DKIM non activé chez l'hébergeur ?)", "warn");
            }
            if (foundSel.Count == 0)
                Row("DKIM", "aucun sélecteur courant trouvé (" + (prov == "Microsoft 365" ? "M365 : activer DKIM dans Defender > Stratégies > DKIM" : "sélecteur peut-être personnalisé : vérifier l'en-tête DKIM-Signature d'un mail reçu") + ")", "warn");

            // ---------------------------------------------------------- DMARC
            Head("DMARC (politique anti-usurpation)");
            var dmarc = Dns2.Txt("_dmarc." + domain).Where(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToList();
            if (dmarc.Count == 0) Row("DMARC", "ABSENT : Gmail / Yahoo l'exigent pour les gros envoyeurs, usurpation facile", "bad");
            else if (dmarc.Count > 1) Row("DMARC", "plusieurs enregistrements : INVALIDE", "bad");
            else
            {
                log.Line("    " + dmarc[0], "muted");
                var p = Regex.Match(dmarc[0], @"\bp\s*=\s*(\w+)", RegexOptions.IgnoreCase);
                var pol = p.Success ? p.Groups[1].Value.ToLowerInvariant() : "";
                Row("Politique", pol == "reject" ? "reject (maximal)" : pol == "quarantine" ? "quarantine (bon)" : pol == "none" ? "none : surveillance seule, aucune protection" : "ILLISIBLE",
                    pol == "reject" || pol == "quarantine" ? "ok" : pol == "none" ? "warn" : "bad");
                Row("Rapports (rua)", dmarc[0].Contains("rua=") ? "oui" : "non : aucun retour sur les usurpations", dmarc[0].Contains("rua=") ? "ok" : "text");
            }

            // ---------------------------------------------------------- Autodiscover
            Head("AUTODISCOVER (configuration d'Outlook)");
            int rcad; var ad = Dns2.Query("autodiscover." + domain, Dns2.CNAME, out rcad);
            var adc = ad.FirstOrDefault(r => r.Type == Dns2.CNAME);
            if (adc != null) Row("autodiscover." + domain, "CNAME → " + adc.Data, prov == "Microsoft 365" && !adc.Data.Contains("outlook.com") ? "warn" : "ok");
            else Row("autodiscover." + domain, prov == "Microsoft 365" ? "ABSENT : Outlook aura du mal à configurer le compte (CNAME autodiscover.outlook.com)" : "absent (normal hors Exchange / M365)", prov == "Microsoft 365" ? "warn" : "text");

            // ---------------------------------------------------------- Serveurs
            Head("SERVEURS (ports + certificats, sans identifiant)");
            var hosts = new List<Tuple<string, string, int, bool>>();
            if (prov == "Microsoft 365") { hosts.Add(Tuple.Create("IMAP M365", "outlook.office365.com", 993, false)); hosts.Add(Tuple.Create("SMTP M365", "smtp.office365.com", 587, true)); }
            if (prov == "OVHcloud") { hosts.Add(Tuple.Create("IMAP OVH", "ssl0.ovh.net", 993, false)); hosts.Add(Tuple.Create("SMTP OVH", "ssl0.ovh.net", 465, false)); }
            if (prov == "Google Workspace") { hosts.Add(Tuple.Create("IMAP Google", "imap.gmail.com", 993, false)); hosts.Add(Tuple.Create("SMTP Google", "smtp.gmail.com", 465, false)); }
            if (!string.IsNullOrWhiteSpace(customHost))
            {
                var h = customHost.Trim();
                hosts.Add(Tuple.Create("IMAP " + h, h, 993, false)); hosts.Add(Tuple.Create("POP " + h, h, 995, false));
                hosts.Add(Tuple.Create("SMTP SSL " + h, h, 465, false)); hosts.Add(Tuple.Create("SMTP 587 " + h, h, 587, true));
            }
            if (hosts.Count == 0) log.Line("  Hébergeur non reconnu : saisis le serveur IMAP/SMTP du client (ex. mail." + domain + ") pour tester les ports.", "muted");
            foreach (var h in hosts) { string c; var res = TlsTest(h.Item2, h.Item3, h.Item4, out c); Row(h.Item1 + ":" + h.Item3, res, c); }
            if (mx.Count > 0)
            {
                string c; var res = TlsTest(mx[0].Data, 25, true, out c);
                // le port 25 sortant est souvent bloqué par les FAI grand public : information, pas une erreur
                log.Line("  " + ("MX port 25 ").PadRight(26, '·') + " " + res + (c == "bad" ? "  (souvent bloqué en sortie par le FAI : normal)" : ""), c == "ok" ? "ok" : "muted");
            }
            log.Line("", "text");
            log.Line("  Listes noires (blacklists) : vérifier sur https://mxtoolbox.com/blacklists.aspx (les listes publiques bloquent les DNS de FAI).", "muted");
            Finish();
        }

        void Finish()
        {
            if (bad > 0) { Verdict = bad + " PROBLÈME(S) BLOQUANT(S)"; VColor = "bad"; }
            else if (warn > 0) { Verdict = "FONCTIONNEL — " + warn + " POINT(S) À AMÉLIORER"; VColor = "warn"; }
            else { Verdict = "MESSAGERIE CORRECTEMENT CONFIGURÉE"; VColor = "ok"; }
            Detail = issues.Count > 0 ? string.Join("  ·  ", issues.Take(3)) : "MX, SPF, DKIM, DMARC et serveurs OK.";
            log.Line("", "text"); log.Line("VERDICT : " + Verdict, VColor, true);
            foreach (var i in issues) log.Line("  - " + i, "text");
        }
    }

    // ===================================================================== Fenêtre
    class MainForm : CyberForm
    {
        readonly TextBox tDomain, tHost;
        readonly Chip bRun, bCopy;
        readonly Card verdict;
        readonly Label vTitle, vSub;
        readonly LogBox box;
        bool busy;

        public MainForm() : base("MAIL_CHECK", 940, 780)
        {
            SetSubtitle("Contrôle de la messagerie d'un domaine  //  lecture seule, aucun identifiant  //  by alex.b");
            Func<float, int> k = Theme.S;
            var l1 = new Label { Text = "Domaine ou adresse :", AutoSize = true, Location = new Point(k(16), k(16)), Tag = "muted" };
            tDomain = new TextBox { Text = Prefs.Ini.Get("Last", ""), Location = new Point(k(150), k(12)), Width = k(230), Font = new Font("Consolas", 10f) };
            var l2 = new Label { Text = "Serveur IMAP/SMTP (option) :", AutoSize = true, Location = new Point(k(396), k(16)), Tag = "muted" };
            tHost = new TextBox { Location = new Point(k(580), k(12)), Width = k(170), Font = new Font("Consolas", 10f) };
            bRun = new Chip("▶  VÉRIFIER") { Primary = true, AccentOf = t => t.Ok, Location = new Point(k(762), k(8)) };
            bCopy = new Chip("Copier le rapport") { AccentOf = t => t.Accent2, Location = new Point(k(16), k(46)) };
            var bMx = new Chip("MXToolbox") { AccentOf = t => t.Muted, Location = new Point(bCopy.Right + k(8), k(46)) };
            bRun.Click += (s, e) => Run();
            tDomain.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Run(); } };
            bCopy.Click += (s, e) => { if (box.TextLength > 0) { Clipboard.SetText(box.Text.Replace("\n", "\r\n")); bCopy.SetText("✓ Copié"); } };
            bMx.Click += (s, e) => { var d = Clean(tDomain.Text); if (d.Length > 0) Process.Start("https://mxtoolbox.com/SuperTool.aspx?action=mx%3a" + Uri.EscapeDataString(d)); };
            verdict = new Card { Location = new Point(k(16), k(86)), Size = new Size(Body.Width - k(32), k(70)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, StatusOf = t => t.Muted };
            vTitle = new Label { Text = "PRÊT", AutoSize = true, Font = new Font("Consolas", 14f, FontStyle.Bold), Location = new Point(k(18), k(10)), Tag = "muted" };
            vSub = new Label { Text = "Saisis le domaine du client (ex. lba-securite.com) ou une adresse mail.", AutoSize = true, MaximumSize = new Size(Body.Width - k(80), 0), Location = new Point(k(20), k(40)), Tag = "muted" };
            verdict.Controls.Add(vTitle); verdict.Controls.Add(vSub);
            box = new LogBox { Location = new Point(k(16), k(168)), Size = new Size(Body.Width - k(32), Body.Height - k(180)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Body.Controls.AddRange(new Control[] { l1, tDomain, l2, tHost, bRun, bCopy, bMx, verdict, box });
        }

        static string Clean(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.Contains("@")) s = s.Substring(s.IndexOf('@') + 1);
            s = Regex.Replace(s, @"^https?://", ""); s = s.Split('/')[0].TrimEnd('.');
            return Regex.IsMatch(s, @"^(?=.{4,253}$)([a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$") ? s : "";
        }

        void Run()
        {
            if (busy) return;
            var dom = Clean(tDomain.Text);
            if (dom.Length == 0) { Warn(this, "Domaine invalide. Exemple : lba-securite.com"); return; }
            var host = tHost.Text.Trim();
            if (host.Length > 0 && !Regex.IsMatch(host, @"^[A-Za-z0-9.\-]{3,253}$")) { Warn(this, "Nom de serveur invalide."); return; }
            Prefs.Ini.Set("Last", dom); Prefs.Save();
            busy = true; UseWaitCursor = true; bRun.Enabled = false; bCopy.SetText("Copier le rapport"); box.Clear();
            vTitle.Text = "CONTRÔLE EN COURS…"; vTitle.Tag = "accent"; verdict.StatusOf = t => t.Accent; Recolor(verdict); verdict.Invalidate();
            var ch = new Checker(box, s => BeginInvoke(new Action(() => vSub.Text = "contrôle : " + s)));
            Task.Run(() =>
            {
                try { ch.Run(dom, host); } catch (Exception ex) { box.Line("ERREUR : " + ex.Message, "bad", true); Log.Write(ex.ToString()); }
                BeginInvoke(new Action(() =>
                {
                    busy = false; UseWaitCursor = false; bRun.Enabled = true;
                    vTitle.Text = ch.Verdict.Length > 0 ? ch.Verdict : "CONTRÔLE INTERROMPU"; vTitle.Tag = ch.VColor; vSub.Text = ch.Detail;
                    verdict.StatusOf = t => t.ByName(ch.VColor); Recolor(verdict); verdict.Invalidate();
                    Log.Write(dom + " : " + ch.Verdict);
                }));
            });
        }
    }
}
