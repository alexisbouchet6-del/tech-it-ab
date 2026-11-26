// TestReseau - diagnostic réseau rapide avec verdict clair
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlexB;

[assembly: AssemblyTitle("TestReseau")]
[assembly: AssemblyDescription("Diagnostic réseau rapide")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("TestReseau")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace TestReseau
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            AppInfo.Name = "TestReseau"; AppInfo.Id = "TestReseau"; AppInfo.Version = "1.1.0"; AppInfo.MachineWide = false;
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; } catch { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            Boot.Run(() => new MainForm(), false, null);
        }
    }

    class Target { public string Name, Host; public int Port; }

    // ===================================================================== Tests
    class Tester
    {
        readonly LogBox log;
        readonly Action<string> step;
        public string Verdict = "", VerdictDetail = "", VerdictColor = "ok";

        public Tester(LogBox l, Action<string> s) { log = l; step = s; }

        void Head(string s) { log.Line("// " + s, "accent", true); step(s.ToLowerInvariant()); }
        void Row(string k, string v, string color) { log.Line("  " + (k + " ").PadRight(30, '·') + " " + v, color); }

        public static string TargetsFile { get { return Path.Combine(AppInfo.DataDir, "cibles.txt"); } }

        public static List<Target> LoadTargets()
        {
            if (!File.Exists(TargetsFile))
                File.WriteAllText(TargetsFile,
                    "# TestReseau (by alex.b) - cibles testées en TCP : Nom | hôte | port\r\n" +
                    "# Ajoute tes serveurs (mail du client, opérateur SIP en TCP 5060/5061, NAS, RDP...).\r\n" +
                    "Web HTTPS | www.google.com | 443\r\n" +
                    "Microsoft 365 (Outlook) | outlook.office365.com | 443\r\n" +
                    "SMTP Microsoft 365 | smtp.office365.com | 587\r\n" +
                    "SMTP OVH (SSL) | ssl0.ovh.net | 465\r\n" +
                    "IMAP OVH (SSL) | ssl0.ovh.net | 993\r\n" +
                    "POP OVH (SSL) | ssl0.ovh.net | 995\r\n" +
                    "SMTP port 25 sortant | aspmx.l.google.com | 25\r\n" +
                    "Synology QuickConnect | global.quickconnect.to | 443\r\n" +
                    "TeamViewer | router1.teamviewer.com | 5938\r\n", new UTF8Encoding(false));
            var list = new List<Target>();
            foreach (var raw in File.ReadAllLines(TargetsFile, Encoding.UTF8))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                var p = l.Split('|').Select(x => x.Trim()).ToArray();
                int port;
                if (p.Length == 3 && p[1].Length > 0 && int.TryParse(p[2], out port) && port > 0 && port < 65536)
                    list.Add(new Target { Name = p[0], Host = p[1], Port = port });
            }
            return list;
        }

        static NetworkInterface ActiveAdapter()
        {
            var all = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel).ToList();
            return all.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                ?? all.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        }

        static Tuple<int, double, double> PingStats(string host, int count, int timeout)
        {
            var times = new List<long>(); int lost = 0;
            using (var p = new Ping())
                for (int i = 0; i < count; i++)
                {
                    try { var r = p.Send(host, timeout); if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime); else lost++; }
                    catch { lost++; }
                }
            double avg = times.Count > 0 ? times.Average() : -1;
            double jitter = times.Count > 1 ? times.Zip(times.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
            return Tuple.Create(lost * 100 / count, avg, jitter);
        }

        // requête DNS (type A) envoyée directement à un serveur précis
        static bool DnsQuery(IPAddress server, string host, out long ms)
        {
            ms = -1;
            try
            {
                var rnd = new Random(); var id = (ushort)rnd.Next(1, 65535);
                var pkt = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
                foreach (var label in host.Split('.')) { pkt.Add((byte)label.Length); pkt.AddRange(Encoding.ASCII.GetBytes(label)); }
                pkt.AddRange(new byte[] { 0, 0, 1, 0, 1 });
                using (var u = new UdpClient(server.AddressFamily))
                {
                    u.Client.ReceiveTimeout = 2000;
                    var sw = Stopwatch.StartNew();
                    u.Send(pkt.ToArray(), pkt.Count, new IPEndPoint(server, 53));
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    var resp = u.Receive(ref ep);
                    ms = sw.ElapsedMilliseconds;
                    return resp.Length > 12 && resp[0] == (byte)(id >> 8) && resp[1] == (byte)id && (resp[3] & 0x0F) == 0 && ((resp[6] << 8) | resp[7]) > 0;
                }
            }
            catch { return false; }
        }

        static bool Tcp(string host, int port, int timeout, out long ms, out string err)
        {
            ms = -1; err = null;
            try
            {
                using (var c = new TcpClient())
                {
                    var sw = Stopwatch.StartNew();
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeout)) { err = "délai dépassé (filtré ?)"; return false; }
                    c.EndConnect(ar);
                    ms = sw.ElapsedMilliseconds;
                    return true;
                }
            }
            catch (SocketException ex) { err = ex.SocketErrorCode == SocketError.ConnectionRefused ? "refusé" : ex.SocketErrorCode == SocketError.HostNotFound ? "nom introuvable" : ex.SocketErrorCode.ToString(); return false; }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        static string Http(string url, int timeout, out int code)
        {
            code = 0;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeout; req.ReadWriteTimeout = timeout; req.AllowAutoRedirect = false;
                req.UserAgent = "TestReseau/" + AppInfo.Version + " (by alex.b)";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream()))
                { code = (int)resp.StatusCode; var body = r.ReadToEnd(); return body.Length > 200 ? body.Substring(0, 200) : body; }
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr != null) code = (int)hr.StatusCode;
                return null;
            }
            catch { return null; }
        }

        public void Run()
        {
            bool linkOk = false, ipOk = false, gwOk = false, netOk = false, dnsOk = false, httpOk = false, captive = false;
            string dnsBadServer = null; int loss = 0; double avg = -1;

            // ---------------------------------------------------------- carte
            Head("CARTE RÉSEAU");
            var ni = ActiveAdapter();
            IPAddress gw = null;
            List<IPAddress> dns = new List<IPAddress>();
            if (ni == null) Row("Carte active", "AUCUNE (câble débranché / Wi-Fi coupé)", "bad");
            else
            {
                linkOk = true;
                var p = ni.GetIPProperties();
                var u = p.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                bool dhcp = false; try { dhcp = p.GetIPv4Properties().IsDhcpEnabled; } catch { }
                gw = p.GatewayAddresses.Select(g => g.Address).FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any));
                dns = p.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).ToList();
                Row("Carte", ni.Name + "  (" + ni.Description + ")", "text");
                Row("Vitesse du lien", ni.Speed > 0 ? (ni.Speed >= 1000000000 ? (ni.Speed / 1000000000.0).ToString("0.#") + " Gb/s" : (ni.Speed / 1000000) + " Mb/s") : "?",
                    ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 && ni.Speed > 0 && ni.Speed < 1000000000 ? "warn" : "text");
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    string o; Proc.Run(Proc.Sys("netsh.exe"), "wlan show interfaces", 10000, out o);
                    var ssid = Regex.Match(o, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
                    var sig = Regex.Match(o, @"^\s*Signal\s*:\s*(\d+)\s*%", RegexOptions.Multiline);
                    if (ssid.Success) Row("Wi-Fi", ssid.Groups[1].Value.Trim() + (sig.Success ? "  —  signal " + sig.Groups[1].Value + " %" : ""),
                        sig.Success && int.Parse(sig.Groups[1].Value) < 50 ? "warn" : "text");
                }
                string ip = u != null ? u.Address.ToString() : null;
                ipOk = ip != null && !ip.StartsWith("169.254.");
                Row("Adresse IP", (ip ?? "aucune") + "/" + (u != null && u.IPv4Mask != null ? u.IPv4Mask.ToString() : "?") + (dhcp ? "  (DHCP)" : "  (fixe)"),
                    ipOk ? "ok" : "bad");
                if (ip != null && ip.StartsWith("169.254.")) Row("Diagnostic", "adresse APIPA : aucun serveur DHCP n'a répondu", "bad");
                Row("Passerelle", gw != null ? gw.ToString() : "AUCUNE", gw != null ? "text" : "bad");
                Row("DNS configurés", dns.Count > 0 ? string.Join(", ", dns) : "AUCUN", dns.Count > 0 ? "text" : "bad");
            }

            // ---------------------------------------------------------- passerelle
            if (gw != null)
            {
                Head("PASSERELLE");
                var st = PingStats(gw.ToString(), 4, 1000);
                gwOk = st.Item2 >= 0;
                Row("Ping " + gw, gwOk ? st.Item2.ToString("0") + " ms, perte " + st.Item1 + " %" : "AUCUNE RÉPONSE", gwOk ? (st.Item1 > 0 ? "warn" : "ok") : "bad");
            }

            // ---------------------------------------------------------- internet
            Head("INTERNET (IP)");
            var s1 = PingStats("1.1.1.1", 10, 1500);
            loss = s1.Item1; avg = s1.Item2;
            netOk = avg >= 0;
            Row("Ping 1.1.1.1 (x10)", netOk ? avg.ToString("0") + " ms moy., gigue " + s1.Item3.ToString("0") + " ms, perte " + loss + " %" : "AUCUNE RÉPONSE",
                !netOk ? "bad" : loss > 5 || avg > 100 ? "warn" : "ok");
            var s2 = PingStats("8.8.8.8", 3, 1500);
            Row("Ping 8.8.8.8", s2.Item2 >= 0 ? s2.Item2.ToString("0") + " ms" : "AUCUNE RÉPONSE", s2.Item2 >= 0 ? "ok" : "bad");
            netOk = netOk || s2.Item2 >= 0;

            // ---------------------------------------------------------- dns
            Head("DNS");
            long ms;
            foreach (var d in dns)
            {
                bool ok = DnsQuery(d, "www.google.com", out ms);
                Row("Serveur " + d, ok ? "répond (" + ms + " ms)" : "NE RÉPOND PAS", ok ? "ok" : "bad");
                if (!ok && dnsBadServer == null) dnsBadServer = d.ToString();
                if (ok) dnsOk = true;
            }
            bool cf = DnsQuery(IPAddress.Parse("1.1.1.1"), "www.google.com", out ms);
            Row("Référence 1.1.1.1", cf ? "répond (" + ms + " ms)" : "ne répond pas (UDP 53 filtré ?)", cf ? "ok" : "warn");
            try
            {
                var sw = Stopwatch.StartNew();
                var addr = Dns.GetHostAddresses("www.microsoft.com");
                Row("Résolution système", "www.microsoft.com → " + addr.First() + "  (" + sw.ElapsedMilliseconds + " ms)", "ok");
                dnsOk = true;
            }
            catch { Row("Résolution système", "ÉCHEC", "bad"); }

            // ---------------------------------------------------------- web
            Head("WEB");
            int code;
            var body = Http("http://www.msftconnecttest.com/connecttest.txt", 5000, out code);
            if (body != null && body.Contains("Microsoft Connect Test")) Row("Test HTTP Microsoft", "OK", "ok");
            else if (code >= 300 && code < 400 || (body != null && !body.Contains("Microsoft"))) { captive = true; Row("Test HTTP Microsoft", "REDIRIGÉ : portail captif (hôtel, invité…) ?", "warn"); }
            else Row("Test HTTP Microsoft", "ÉCHEC" + (code > 0 ? " (HTTP " + code + ")" : ""), "bad");
            Http("https://www.google.com/generate_204", 6000, out code);
            httpOk = code == 204 || code == 200;
            Row("HTTPS google.com", httpOk ? "OK" : "ÉCHEC" + (code > 0 ? " (HTTP " + code + ")" : " (proxy / filtrage / TLS ?)"), httpOk ? "ok" : "bad");
            if (Prefs.Ini.GetBool("PublicIp", false))
            {
                var pub = Http("https://api.ipify.org", 6000, out code);
                Row("IP publique (api.ipify.org)", pub != null ? pub.Trim() : "inconnue", pub != null ? "text" : "warn");
            }
            else Row("IP publique", "non demandée (option « IP publique » : interroge le service tiers api.ipify.org)", "muted");

            // ---------------------------------------------------------- ports
            Head("SERVICES (TCP)");
            var targets = LoadTargets();
            Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = 6 }, t =>
            {
                long tms; string err;
                bool ok = Tcp(t.Host, t.Port, 3000, out tms, out err);
                Row(t.Name, t.Host + ":" + t.Port + "  →  " + (ok ? "ouvert (" + tms + " ms)" : "FERMÉ : " + err),
                    ok ? "ok" : t.Port == 25 ? "warn" : "bad");
            });
            log.Line("  (port 25 souvent bloqué par les FAI : normal sur une box grand public)", "muted");

            // ---------------------------------------------------------- verdict
            if (!linkOk) Set("PAS DE CONNEXION RÉSEAU", "Câble débranché, Wi-Fi coupé ou carte désactivée.", "bad");
            else if (!ipOk) Set("PAS D'ADRESSE IP", "Aucun serveur DHCP n'a répondu : vérifier la box / le serveur DHCP, ou passer en IP fixe (NetSwitch).", "bad");
            else if (gw != null && !gwOk && !netOk) Set("PASSERELLE INJOIGNABLE", "La box / le routeur ne répond pas : câble, switch, VLAN ou box éteinte.", "bad");
            else if (!netOk) Set("INTERNET COUPÉ APRÈS LA BOX", "Le réseau local fonctionne mais pas la sortie Internet : box, ligne ou FAI.", "bad");
            else if (!dnsOk) Set("PROBLÈME DNS", "Internet répond par IP mais les noms ne se résolvent pas" + (dnsBadServer != null ? " (DNS " + dnsBadServer + " muet)" : "") + " : mettre 1.1.1.1 / 8.8.8.8 ou corriger la box.", "bad");
            else if (captive) Set("PORTAIL CAPTIF", "Ouvrir un navigateur pour accepter les conditions du réseau invité.", "warn");
            else if (!httpOk) Set("WEB BLOQUÉ", "Le DNS et le ping fonctionnent mais pas le HTTPS : proxy, pare-feu ou filtrage.", "warn");
            else if (loss > 5 || avg > 100) Set("CONNEXION INSTABLE", "Perte " + loss + " %, latence " + avg.ToString("0") + " ms : Wi-Fi faible, ligne saturée ou défaut de câble.", "warn");
            else if (dnsBadServer != null) Set("RÉSEAU OK — UN DNS MUET", "Le DNS " + dnsBadServer + " ne répond pas : le poste bascule sur le suivant (lenteurs possibles).", "warn");
            else Set("RÉSEAU OK", "Lien, passerelle, Internet, DNS et web fonctionnent. Latence " + avg.ToString("0") + " ms, perte " + loss + " %.", "ok");
            log.Line("", "text");
            log.Line("VERDICT : " + Verdict + " — " + VerdictDetail, VerdictColor, true);
        }

        void Set(string v, string d, string c) { Verdict = v; VerdictDetail = d; VerdictColor = c; }
    }

    // ===================================================================== Fenêtre
    class MainForm : CyberForm
    {
        readonly Chip bRun, bTrace, bCopy, bTargets;
        readonly Card verdict;
        readonly Label vTitle, vSub;
        readonly LogBox box;
        bool busy;

        public MainForm() : base("TEST_RESEAU", 880, 760)
        {
            SetSubtitle("Diagnostic réseau en 20 secondes  //  " + Environment.MachineName + "  //  by alex.b");
            Func<float, int> k = Theme.S;
            bRun = new Chip("▶  LANCER LE TEST") { Primary = true, AccentOf = t => t.Ok, Location = new Point(k(16), k(12)) };
            bTrace = new Chip("Traceroute 1.1.1.1") { AccentOf = t => t.Accent };
            bCopy = new Chip("Copier le rapport") { AccentOf = t => t.Accent2 };
            bTargets = new Chip("Modifier les cibles") { AccentOf = t => t.Muted };
            bTrace.Location = new Point(bRun.Right + k(8), k(12));
            bCopy.Location = new Point(bTrace.Right + k(8), k(12));
            bTargets.Location = new Point(bCopy.Right + k(8), k(12));
            var bPub = new Chip(Prefs.Ini.GetBool("PublicIp", false) ? "✓ IP publique" : "IP publique : non") { AccentOf = t => t.Muted };
            bPub.Location = new Point(bTargets.Right + k(8), k(12));
            bPub.Click += (s, e) =>
            {
                bool on = !Prefs.Ini.GetBool("PublicIp", false);
                Prefs.Ini.Set("PublicIp", on); Prefs.Save();
                bPub.SetText(on ? "✓ IP publique" : "IP publique : non");
            };
            Body.Controls.Add(bPub);
            bRun.Click += (s, e) => RunTest();
            bTrace.Click += (s, e) => Trace();
            bCopy.Click += (s, e) =>
            {
                if (box.TextLength == 0) return;
                Clipboard.SetText("TEST RÉSEAU — " + Environment.MachineName + " — " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " (TestReseau, by alex.b)\r\n\r\n" + box.Text.Replace("\n", "\r\n"));
                bCopy.SetText("✓ Copié");
            };
            bTargets.Click += (s, e) => { Tester.LoadTargets(); Process.Start("notepad.exe", "\"" + Tester.TargetsFile + "\""); };
            Body.Controls.AddRange(new Control[] { bRun, bTrace, bCopy, bTargets });

            verdict = new Card { Location = new Point(k(16), k(54)), Size = new Size(Body.Width - k(32), k(70)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, StatusOf = t => t.Muted };
            vTitle = new Label { Text = "PRÊT", AutoSize = true, Font = new Font("Consolas", 14f, FontStyle.Bold), Location = new Point(k(18), k(10)), Tag = "muted" };
            vSub = new Label { Text = "Lance le test : carte, passerelle, Internet, DNS, web et services.", AutoSize = true, MaximumSize = new Size(Body.Width - k(80), 0), Location = new Point(k(20), k(40)), Tag = "muted" };
            verdict.Controls.Add(vTitle); verdict.Controls.Add(vSub);
            Body.Controls.Add(verdict);
            box = new LogBox { Location = new Point(k(16), k(136)), Size = new Size(Body.Width - k(32), Body.Height - k(148)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Body.Controls.Add(box);
            Shown += (s, e) => RunTest();
        }

        void SetBusy(bool on)
        {
            busy = on; UseWaitCursor = on;
            foreach (var c in new[] { bRun, bTrace, bCopy, bTargets }) c.Enabled = !on;
            bCopy.SetText("Copier le rapport");
        }

        void RunTest()
        {
            if (busy) return;
            SetBusy(true); box.Clear();
            vTitle.Text = "TEST EN COURS…"; vTitle.Tag = "accent"; verdict.StatusOf = t => t.Accent; Recolor(verdict); verdict.Invalidate();
            var tester = new Tester(box, s => BeginInvoke(new Action(() => vSub.Text = "test : " + s)));
            Task.Run(() =>
            {
                try { tester.Run(); } catch (Exception ex) { box.Line("ERREUR : " + ex.Message, "bad", true); Log.Write(ex.ToString()); }
                BeginInvoke(new Action(() =>
                {
                    SetBusy(false);
                    vTitle.Text = tester.Verdict.Length > 0 ? tester.Verdict : "TEST INTERROMPU";
                    vTitle.Tag = tester.VerdictColor; vSub.Text = tester.VerdictDetail;
                    verdict.StatusOf = t => t.ByName(tester.VerdictColor);
                    Recolor(verdict); verdict.Invalidate();
                    Log.Write("Verdict : " + tester.Verdict);
                }));
            });
        }

        void Trace()
        {
            if (busy) return;
            SetBusy(true);
            box.Line("", "text");
            box.Line("// TRACEROUTE 1.1.1.1 (15 sauts max)", "accent", true);
            Task.Run(() =>
            {
                string o;
                Proc.Run(Proc.Sys("tracert.exe"), "-d -h 15 -w 1000 1.1.1.1", 120000, null, line => box.Line("  " + line, line.Contains("*") ? "warn" : "text"), out o);
                BeginInvoke(new Action(() => SetBusy(false)));
            });
        }
    }
}
