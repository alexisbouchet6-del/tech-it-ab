// AMI·OPS - Diagnostic réseau : Wi-Fi, surveillance des coupures, serveur DHCP pirate, débit entre 2 PC, test SMTP
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlexB;

namespace AmiOps
{
    class Ap { public string Ssid = "", Bssid = "", Radio = "", Auth = "", Band = ""; public int Signal, Channel; }

    static class Wifi
    {
        static readonly Regex Line = new Regex(@"^\s*(SSID \d+|BSSID \d+|SSID|BSSID|Signal|Canal|Channel|Bande|Band|Type de radio|Radio type|Authentification|Authentication|Vitesse de réception \(Mbits/s\)|Receive rate \(Mbps\)|État|State)\s*:\s*(.*)$", RegexOptions.IgnoreCase);

        public static List<Ap> Scan(out Ap current, out string err)
        {
            return Parse(Sh.CmdOut(Proc.Sys("netsh.exe"), "wlan show networks mode=bssid", 30000), Sh.CmdOut(Proc.Sys("netsh.exe"), "wlan show interfaces", 30000), out current, out err);
        }

        public static List<Ap> Parse(string o, string i, out Ap current, out string err)
        {
            current = null; err = null; var l = new List<Ap>();
            if (Regex.IsMatch(o, "location|localisation", RegexOptions.IgnoreCase)) { err = "Windows 11 exige l'autorisation de localisation pour lister le Wi-Fi : Paramètres > Confidentialité et sécurité > Localisation > activer « Services de localisation » et « Autoriser les applications de bureau ». Puis relance."; }
            else if (o.Trim().Length == 0 || Regex.IsMatch(o, "n'est pas en cours|is not running|aucune interface|no wireless interface", RegexOptions.IgnoreCase)) { err = "Pas de carte Wi-Fi active (ou service WLAN arrêté)."; }
            string ssid = "", auth = ""; Ap cur = null;
            foreach (var raw in o.Split('\n'))
            {
                var m = Line.Match(raw); if (!m.Success) continue;
                var k = m.Groups[1].Value.ToLowerInvariant(); var v = m.Groups[2].Value.Trim();
                if (k.StartsWith("ssid ")) { ssid = v; auth = ""; }
                else if (k.StartsWith("auth")) auth = v;
                else if (k.StartsWith("bssid ")) { cur = new Ap { Ssid = ssid.Length > 0 ? ssid : "(masqué)", Bssid = v, Auth = auth }; l.Add(cur); }
                else if (cur == null) continue;
                else if (k == "signal") { int s; int.TryParse(v.TrimEnd('%', ' '), out s); cur.Signal = s; }
                else if (k == "canal" || k == "channel") { int c; int.TryParse(v, out c); cur.Channel = c; }
                else if (k == "bande" || k == "band") cur.Band = v;
                else if (k.StartsWith("type de radio") || k.StartsWith("radio type")) cur.Radio = v;
            }
            foreach (var a in l) if (a.Band.Length == 0) a.Band = a.Channel > 14 ? "5 GHz" : "2,4 GHz";
            var c2 = new Ap(); bool any = false;
            foreach (var raw in i.Split('\n'))
            {
                var m = Line.Match(raw); if (!m.Success) continue;
                var k = m.Groups[1].Value.ToLowerInvariant(); var v = m.Groups[2].Value.Trim();
                if (k == "ssid") { c2.Ssid = v; any = true; } else if (k == "bssid") c2.Bssid = v;
                else if (k == "signal") { int s; int.TryParse(v.TrimEnd('%', ' '), out s); c2.Signal = s; }
                else if (k == "canal" || k == "channel") { int c; int.TryParse(v, out c); c2.Channel = c; }
                else if (k.StartsWith("type de radio") || k.StartsWith("radio type")) c2.Radio = v;
                else if (k.StartsWith("vitesse") || k.StartsWith("receive")) c2.Auth = v + " Mbit/s";
            }
            if (any) current = c2;
            return l;
        }

        public static void Analyze(Action<string, string> log)
        {
            Ap cur; string err; var aps = Scan(out cur, out err);
            if (err != null) { log(err, "bad"); if (err.Contains("localisation")) try { Process.Start("ms-settings:privacy-location"); } catch { } return; }
            if (cur != null)
            {
                log("CONNEXION ACTUELLE : « " + cur.Ssid + " » · signal " + cur.Signal + " % · canal " + cur.Channel + " · " + cur.Radio + (cur.Auth.Length > 0 ? " · " + cur.Auth : ""), cur.Signal >= 70 ? "ok" : cur.Signal >= 50 ? "warn" : "bad");
                log(cur.Signal >= 70 ? "  signal bon" : cur.Signal >= 50 ? "  signal moyen : coupures possibles plus loin de la borne" : "  SIGNAL FAIBLE : rapprocher le poste, ajouter un point d'accès / système mesh, ou passer en câble", cur.Signal >= 70 ? "ok" : cur.Signal >= 50 ? "warn" : "bad");
                if (cur.Channel > 0 && cur.Channel <= 14 && aps.Any(a => a.Ssid == cur.Ssid && a.Channel > 14)) log("  le même réseau existe en 5 GHz : préférer le 5 GHz (plus rapide, moins encombré)", "warn");
            }
            log("", "text"); log("// RÉSEAUX VISIBLES (" + aps.Count + " points d'accès)", "accent");
            foreach (var a in aps.OrderByDescending(a => a.Signal).Take(40))
                log("  " + a.Signal.ToString().PadLeft(3) + " %  canal " + a.Channel.ToString().PadRight(4) + a.Band.PadRight(8) + (a.Ssid.Length > 28 ? a.Ssid.Substring(0, 28) : a.Ssid).PadRight(30) + a.Radio.PadRight(11) + a.Auth, a.Auth.IndexOf("Ouvert", StringComparison.OrdinalIgnoreCase) >= 0 || a.Auth.IndexOf("Open", StringComparison.OrdinalIgnoreCase) >= 0 ? "warn" : "text");
            // 2,4 GHz : seuls 1, 6 et 11 ne se chevauchent pas ; on pondère par la puissance des voisins
            var g24 = aps.Where(a => a.Channel >= 1 && a.Channel <= 14 && (cur == null || a.Bssid != cur.Bssid)).ToList();
            if (g24.Count > 0)
            {
                var load = new[] { 1, 6, 11 }.Select(ch => Tuple.Create(ch, g24.Where(a => Math.Abs(a.Channel - ch) < 5).Sum(a => a.Signal * (5 - Math.Abs(a.Channel - ch)) / 5.0))).OrderBy(t => t.Item2).ToList();
                log("", "text"); log("// 2,4 GHz : charge des canaux 1 / 6 / 11", "accent");
                foreach (var t in load.OrderBy(t => t.Item1)) log("  canal " + t.Item1.ToString().PadRight(3) + new string('█', Math.Min(40, (int)(t.Item2 / 15))) + " " + t.Item2.ToString("0"), t.Item1 == load[0].Item1 ? "ok" : "text");
                log("  → canal conseillé en 2,4 GHz : " + load[0].Item1 + (cur != null && cur.Channel <= 14 && cur.Channel != load[0].Item1 ? " (actuel : " + cur.Channel + ")" : ""), "ok");
                var bad = g24.Where(a => a.Channel != 1 && a.Channel != 6 && a.Channel != 11).Count();
                if (bad > 0) log("  " + bad + " borne(s) sur des canaux intermédiaires : elles brouillent deux canaux à la fois", "muted");
            }
            var g5 = aps.Where(a => a.Channel > 14).ToList();
            if (g5.Count > 0)
            {
                var used = g5.GroupBy(a => a.Channel).ToDictionary(x => x.Key, x => x.Count());
                var free = new[] { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140 }.OrderBy(c => used.ContainsKey(c) ? used[c] : 0).ThenBy(c => c).First();
                log("", "text"); log("// 5 GHz : " + string.Join(" · ", used.OrderBy(x => x.Key).Select(x => "canal " + x.Key + " ×" + x.Value)), "accent");
                log("  → canal conseillé en 5 GHz : " + free + (free >= 52 ? " (canal DFS : la borne peut changer de canal en présence de radar)" : ""), "ok");
            }
            var open = aps.Where(a => Regex.IsMatch(a.Auth, "ouvert|open", RegexOptions.IgnoreCase)).Select(a => a.Ssid).Distinct().ToList();
            if (open.Count > 0) log("Réseaux ouverts (sans mot de passe) à proximité : " + string.Join(", ", open), "warn");
        }
    }

    // ===================================================================== Surveillance des coupures (longue durée)
    class MonitorForm : Form
    {
        class Target { public string Name, Host; public bool Dns; public List<double> S = new List<double>(); public List<DateTime> T = new List<DateTime>(); public int Lost, Count, Streak; public DateTime OutStart; public double Sum, Max; public List<string> Outages = new List<string>(); }
        readonly List<Target> targets = new List<Target>(); readonly Panel chart; readonly Label stat; readonly Chip bStop; volatile bool run = true; DateTime start = DateTime.Now;

        public MonitorForm()
        {
            Func<float, int> k = Theme.S;
            Text = "Surveillance des coupures · by alex.b"; Size = new Size(k(1100), k(640)); StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Current.Bg; Font = new Font("Segoe UI", 9f);
            var gw = ScanParc.Scanner.Gateway();
            if (gw != null) targets.Add(new Target { Name = "Box / passerelle (" + gw + ")", Host = gw.ToString() });
            targets.Add(new Target { Name = "Internet (1.1.1.1)", Host = "1.1.1.1" });
            targets.Add(new Target { Name = "Internet (8.8.8.8)", Host = "8.8.8.8" });
            targets.Add(new Target { Name = "DNS (résolution www.google.fr)", Host = "www.google.fr", Dns = true });
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = k(44), Padding = new Padding(k(6)) };
            bStop = new Chip("■ Arrêter") { AccentOf = t => t.Bad };
            bStop.Click += (s, e) => { run = false; bStop.SetText("Arrêté"); };
            var bCopy = new Chip("Copier le rapport") { AccentOf = t => t.Accent2 }; bCopy.Click += (s, e) => Clip.Text(Report());
            bar.Controls.Add(bStop); bar.Controls.Add(bCopy);
            bar.Controls.Add(new Label { AutoSize = true, Tag = "muted", Text = "  Laisse tourner pendant que le problème se produit (des heures si besoin). Une mesure par seconde, 4 cibles.", Margin = new Padding(0, k(8), 0, 0) });
            stat = new Label { Dock = DockStyle.Bottom, Height = k(130), Tag = "text", Font = new Font("Consolas", 9f), Padding = new Padding(k(8)) };
            chart = new Panel { Dock = DockStyle.Fill };
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(chart, true, null);
            chart.Paint += Draw;
            Controls.Add(chart); Controls.Add(bar); Controls.Add(stat);
            FormClosing += (s, e) => run = false;
            CyberForm.Recolor(this);
            var th = new Thread(Loop) { IsBackground = true }; th.Start();
            var ui = new System.Windows.Forms.Timer { Interval = 1000 }; ui.Tick += (s, e) => { chart.Invalidate(); stat.Text = Summary(); }; ui.Start();
        }

        double Probe(Target t)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (t.Dns) { int rc; var r = MailCheck.Dns2.Query(t.Host, MailCheck.Dns2.A, out rc); return r.Count > 0 ? sw.Elapsed.TotalMilliseconds : -1; }
                using (var p = new Ping()) { var r = p.Send(t.Host, 1000); return r.Status == IPStatus.Success ? r.RoundtripTime : -1; }
            }
            catch { return -1; }
        }

        void Loop()
        {
            while (run)
            {
                var t0 = DateTime.Now;
                var res = targets.AsParallel().Select(t => Tuple.Create(t, Probe(t))).ToList();
                lock (targets)
                    foreach (var r in res)
                    {
                        var t = r.Item1; var v = r.Item2; t.Count++; t.S.Add(v); t.T.Add(t0);
                        if (t.S.Count > 7200) { t.S.RemoveAt(0); t.T.RemoveAt(0); }
                        if (v < 0) { t.Lost++; if (++t.Streak == 3) t.OutStart = t0.AddSeconds(-2); }
                        else
                        {
                            if (t.Streak >= 3) t.Outages.Add(t.OutStart.ToString("dd/MM HH:mm:ss") + " → " + t0.ToString("HH:mm:ss") + " (" + (int)(t0 - t.OutStart).TotalSeconds + " s)");
                            t.Streak = 0; t.Sum += v; t.Max = Math.Max(t.Max, v);
                        }
                    }
                var wait = 1000 - (int)(DateTime.Now - t0).TotalMilliseconds; if (wait > 0) Thread.Sleep(wait);
            }
        }

        string Verdict()
        {
            if (targets.Count < 3 || targets[0].Count < 30) return "mesure en cours (au moins 30 s pour un premier avis)…";
            Func<Target, double> loss = t => t.Count == 0 ? 0 : 100.0 * t.Lost / t.Count;
            var gw = targets[0]; var net = targets.Where(t => t.Host == "1.1.1.1" || t.Host == "8.8.8.8").ToList(); var dns = targets.Last();
            bool hasGw = !gw.Dns && gw.Name.StartsWith("Box");
            double lg = hasGw ? loss(gw) : 0, ln = net.Min(t => loss(t)), ld = loss(dns);
            double avgGw = hasGw && gw.Count - gw.Lost > 0 ? gw.Sum / (gw.Count - gw.Lost) : 0;
            if (hasGw && lg > 1) return "PROBLÈME LOCAL : la box elle-même ne répond pas toujours (" + lg.ToString("0.0") + " % de pertes) → Wi-Fi (signal, interférences), câble, switch ou carte réseau du poste.";
            if (ln > 1) return "PROBLÈME BOX ↔ OPÉRATEUR : la box répond mais Internet coupe (" + ln.ToString("0.0") + " % de pertes) → ligne, box (redémarrer / remplacer) ou incident opérateur (ticket FAI avec ce rapport).";
            if (ld > 2) return "PROBLÈME DNS : Internet répond mais la résolution des noms échoue (" + ld.ToString("0.0") + " %) → DNS de la box ou du serveur : passer en 1.1.1.1 / 9.9.9.9.";
            if (hasGw && avgGw > 30) return "LATENCE LOCALE ÉLEVÉE (" + avgGw.ToString("0") + " ms vers la box) → Wi-Fi saturé ou signal faible ; tester en câble.";
            return "Aucune coupure significative pour l'instant.";
        }

        string Summary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Durée " + (DateTime.Now - start).ToString(@"hh\:mm\:ss") + "   VERDICT : " + Verdict());
            lock (targets)
                foreach (var t in targets)
                {
                    int ok = t.Count - t.Lost;
                    sb.AppendLine("  " + t.Name.PadRight(34) + "pertes " + (t.Count == 0 ? 0 : 100.0 * t.Lost / t.Count).ToString("0.0").PadLeft(5) + " %   moy " + (ok > 0 ? (t.Sum / ok).ToString("0") : "-").PadLeft(4) + " ms   max " + t.Max.ToString("0").PadLeft(5) + " ms   coupures ≥3 s : " + t.Outages.Count);
                }
            return sb.ToString();
        }

        string Report()
        {
            var sb = new StringBuilder("SURVEILLANCE RÉSEAU — " + Environment.MachineName + " — début " + start.ToString("dd/MM/yyyy HH:mm") + " (AMI·OPS, by alex.b)\r\n" + Summary() + "\r\nCOUPURES :\r\n");
            lock (targets) foreach (var t in targets) foreach (var o in t.Outages) sb.AppendLine("  " + t.Name + " : " + o);
            return sb.ToString();
        }

        void Draw(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; var th = Theme.Current; g.Clear(th.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            var cols = new[] { th.Ok, th.Accent, th.Accent2, th.Warn };
            int W = chart.Width - 60, H = chart.Height - 30; if (W < 50 || H < 50) return;
            int n = 600; double maxMs = 200;
            using (var f = new Font("Consolas", 8f)) using (var mb = new SolidBrush(th.Muted)) using (var gp = new Pen(th.Border))
            {
                foreach (var ms in new[] { 0, 50, 100, 150, 200 }) { float y = 10 + H - (float)(ms / maxMs * H); g.DrawLine(gp, 50, y, 50 + W, y); g.DrawString(ms + " ms", f, mb, 0, y - 6); }
                g.DrawString("10 dernières minutes — trait rouge = paquet perdu", f, mb, 52, H + 14);
            }
            lock (targets)
                for (int ti = 0; ti < targets.Count; ti++)
                {
                    var s = targets[ti].S; int from = Math.Max(0, s.Count - n); var pts = new List<PointF>();
                    for (int i = from; i < s.Count; i++)
                    {
                        float x = 50 + (float)(i - from) / n * W;
                        if (s[i] < 0) { using (var p = new Pen(Color.FromArgb(150, th.Bad), 2)) g.DrawLine(p, x, 10, x, 10 + H); if (pts.Count > 1) using (var p = new Pen(cols[ti % 4], 1.6f)) g.DrawLines(p, pts.ToArray()); pts.Clear(); continue; }
                        pts.Add(new PointF(x, 10 + H - (float)(Math.Min(maxMs, s[i]) / maxMs * H)));
                    }
                    if (pts.Count > 1) using (var p = new Pen(cols[ti % 4], 1.6f)) g.DrawLines(p, pts.ToArray());
                    using (var f = new Font("Segoe UI", 8.5f)) using (var b = new SolidBrush(cols[ti % 4])) g.DrawString("■ " + targets[ti].Name, f, b, 60 + ti * 230, 12);
                }
        }
    }

    static class NetTools
    {
        // ------------------------------------------------------------------ DHCP pirate : un DISCOVER, on liste toutes les offres reçues
        public static void Dhcp(Action<string, string> log)
        {
            var rnd = new Random(); var xid = new byte[4]; rnd.NextBytes(xid);
            var mac = new byte[6]; rnd.NextBytes(mac); mac[0] = (byte)((mac[0] & 0xFE) | 0x02);   // MAC aléatoire « locale » : aucune adresse du poste n'est touchée
            var p = new byte[300];
            p[0] = 1; p[1] = 1; p[2] = 6; Array.Copy(xid, 0, p, 4, 4); p[10] = 0x80;
            Array.Copy(mac, 0, p, 28, 6);
            int o = 236; foreach (var b in new byte[] { 99, 130, 83, 99, 53, 1, 1, 55, 4, 1, 3, 6, 15, 255 }) p[o++] = b;
            var offers = new Dictionary<string, string>();
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                try { s.Bind(new IPEndPoint(IPAddress.Any, 68)); }
                catch (Exception ex) { log("Port DHCP 68 indisponible (" + ex.Message + ") : test impossible sur ce poste.", "bad"); return; }
                s.ReceiveTimeout = 1500;
                for (int round = 0; round < 2; round++) s.SendTo(p, new IPEndPoint(IPAddress.Broadcast, 67));
                log("  demande DHCP diffusée, écoute des réponses pendant 6 s…", "muted");
                var until = DateTime.Now.AddSeconds(6); var buf = new byte[1500];
                while (DateTime.Now < until)
                {
                    try
                    {
                        EndPoint ep = new IPEndPoint(IPAddress.Any, 0); int n = s.ReceiveFrom(buf, ref ep);
                        if (n < 240 || buf[0] != 2 || buf[4] != xid[0] || buf[5] != xid[1] || buf[6] != xid[2] || buf[7] != xid[3]) continue;
                        var offered = new IPAddress(new[] { buf[16], buf[17], buf[18], buf[19] });
                        string server = ((IPEndPoint)ep).Address.ToString(), router = "", dns = ""; int i = 240;
                        while (i < n && buf[i] != 255) { if (buf[i] == 0) { i++; continue; } int code = buf[i], len = buf[i + 1]; var v = new byte[len]; Array.Copy(buf, i + 2, v, 0, Math.Min(len, n - i - 2));
                            if (code == 54 && len == 4) server = new IPAddress(v).ToString();
                            if (code == 3 && len >= 4) router = new IPAddress(v.Take(4).ToArray()).ToString();
                            if (code == 6 && len >= 4) dns = string.Join(", ", Enumerable.Range(0, len / 4).Select(j => new IPAddress(v.Skip(j * 4).Take(4).ToArray()).ToString()));
                            i += 2 + len; }
                        offers[server] = "propose " + offered + " · passerelle " + router + " · DNS " + dns;
                    }
                    catch (SocketException) { }
                }
            }
            if (offers.Count == 0) log("Aucune réponse DHCP (pare-feu du poste, réseau en IP fixe, ou serveur DHCP en panne).", "warn");
            foreach (var kv in offers) log("  Serveur DHCP " + kv.Key + " : " + kv.Value, offers.Count > 1 ? "bad" : "ok");
            if (offers.Count > 1) log("PLUSIEURS SERVEURS DHCP : conflit (souvent box + routeur / borne Wi-Fi en mode routeur). Désactiver le DHCP de l'équipement en trop.", "bad");
            else if (offers.Count == 1) log("Un seul serveur DHCP : normal.", "ok");
        }

        // ------------------------------------------------------------------ Débit entre deux PC
        const int Port = 45201;
        static TcpListener server; static volatile bool serving;

        public static bool ServerRunning { get { return serving; } }

        public static void ServerStart(Action<string, string> log)
        {
            int fwrc = Sh.Cmd(Proc.Sys("netsh.exe"), "advfirewall firewall add rule name=\"AMI-OPS debit\" dir=in action=allow protocol=TCP localport=" + Port, 30000);
            server = new TcpListener(IPAddress.Any, Port); server.Start(); serving = true;
            var ips = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => u.Address.ToString());
            log("Serveur de débit actif sur " + string.Join(" / ", ips) + " port " + Port + (fwrc == 0 ? " (règle pare-feu temporaire ajoutée)." : " — règle pare-feu NON ajoutée : autoriser le port " + Port + " manuellement."), fwrc == 0 ? "ok" : "warn");
            log("Sur l'autre PC : AMI·OPS > Diagnostic réseau > « Débit : client » avec une de ces adresses. Re-clique « Débit : serveur » pour arrêter.", "text");
            new Thread(() =>
            {
                while (serving)
                {
                    TcpClient c; try { c = server.AcceptTcpClient(); } catch { break; }
                    var cc = c;
                    new Thread(() =>
                    {
                        try
                        {
                            using (cc) using (var s = cc.GetStream())
                            {
                                int mode = s.ReadByte(); var buf = new byte[65536];
                                if (mode == 'U') { long tot = 0; int n; while ((n = s.Read(buf, 0, buf.Length)) > 0) tot += n; }
                                else if (mode == 'D') { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < 8000) s.Write(buf, 0, buf.Length); }
                            }
                        }
                        catch { }
                    }) { IsBackground = true }.Start();
                }
            }) { IsBackground = true }.Start();
        }

        public static void ServerStop(Action<string, string> log)
        {
            serving = false; try { server.Stop(); } catch { }
            Sh.Cmd(Proc.Sys("netsh.exe"), "advfirewall firewall delete rule name=\"AMI-OPS debit\"", 30000);
            log("Serveur de débit arrêté, règle pare-feu retirée.", "ok");
        }

        public static void Client(string host, Action<string, string> log)
        {
            using (var p = new Ping()) { var r = p.Send(host, 1500); log("  latence : " + (r.Status == IPStatus.Success ? r.RoundtripTime + " ms" : "pas de réponse au ping (ICMP filtré ?)"), "muted"); }
            var buf = new byte[65536]; new Random().NextBytes(buf);
            double up, down;
            using (var c = new TcpClient()) { if (!c.ConnectAsync(host, Port).Wait(4000)) throw new Exception("serveur injoignable sur le port " + Port + " (lancé ? pare-feu ?)"); var s = c.GetStream(); s.WriteByte((byte)'U'); var sw = Stopwatch.StartNew(); long tot = 0; while (sw.ElapsedMilliseconds < 8000) { s.Write(buf, 0, buf.Length); tot += buf.Length; } up = tot * 8 / sw.Elapsed.TotalSeconds / 1e6; }
            log("  envoi  (ce PC → serveur) : " + up.ToString("0") + " Mbit/s", "ok");
            using (var c = new TcpClient()) { if (!c.ConnectAsync(host, Port).Wait(4000)) throw new Exception("serveur injoignable"); var s = c.GetStream(); s.WriteByte((byte)'D'); var sw = Stopwatch.StartNew(); long tot = 0; int n; while ((n = s.Read(buf, 0, buf.Length)) > 0) tot += n; down = tot * 8 / sw.Elapsed.TotalSeconds / 1e6; }
            log("  réception (serveur → ce PC) : " + down.ToString("0") + " Mbit/s", "ok");
            var min = Math.Min(up, down);
            log(min > 800 ? "Liaison gigabit OK." : min > 300 ? "Bon débit (Wi-Fi 5/6 correct ou gigabit chargé)." : min > 80 ? "Débit moyen : lien 100 Mbit/s (câble / port / switch) ou Wi-Fi moyen. Vérifier la vitesse de liaison de la carte." : "DÉBIT FAIBLE : câble abîmé, port 100 Mbit/s, switch saturé ou Wi-Fi faible.", min > 300 ? "ok" : min > 80 ? "warn" : "bad");
        }

        // ------------------------------------------------------------------ Test SMTP authentifié
        public static void Smtp(string host, int port, string mode, string user, string pass, string from, string to, Action<string, string> log)
        {
            using (var c = new TcpClient())
            {
                if (!c.ConnectAsync(host, port).Wait(6000)) { log("Connexion impossible à " + host + ":" + port + " (port bloqué par le FAI / pare-feu, ou serveur arrêté).", "bad"); return; }
                log("  connecté à " + host + ":" + port, "ok");
                Stream s = c.GetStream(); s.ReadTimeout = 15000;
                Func<Stream, string> read = st => { var sb = new StringBuilder(); string l; while ((l = ReadLine(st)) != null) { sb.AppendLine(l); log("  S: " + l, "muted"); if (l.Length < 4 || l[3] == ' ') break; } return sb.ToString(); };
                Action<Stream, string, string> send = (st, cmd, shown) => { var b = Encoding.ASCII.GetBytes(cmd + "\r\n"); st.Write(b, 0, b.Length); log("  C: " + (shown ?? cmd), "text"); };
                Func<SslStream> tls = () =>
                {
                    var ssl = new SslStream(s, false, (o, cert, ch, err) => { if (err != SslPolicyErrors.None) log("  CERTIFICAT INVALIDE : " + err + " (le client mail refusera la connexion)", "bad"); else log("  certificat valide : " + new X509Certificate2(cert).GetNameInfo(X509NameType.SimpleName, false) + " jusqu'au " + new X509Certificate2(cert).NotAfter.ToString("dd/MM/yyyy"), "ok"); return true; });
                    ssl.AuthenticateAsClient(host); log("  chiffrement : " + ssl.SslProtocol, "ok"); return ssl;
                };
                if (mode == "SSL/TLS (465)") s = tls();
                var banner = read(s); if (!banner.StartsWith("220")) { log("Bannière inattendue.", "bad"); return; }
                send(s, "EHLO amiops.local", null); var ehlo = read(s);
                if (mode == "STARTTLS (587)")
                {
                    if (!ehlo.ToUpperInvariant().Contains("STARTTLS")) { log("Le serveur ne propose pas STARTTLS.", "bad"); return; }
                    send(s, "STARTTLS", null); if (!read(s).StartsWith("220")) { log("STARTTLS refusé.", "bad"); return; }
                    s = tls(); send(s, "EHLO amiops.local", null); ehlo = read(s);
                }
                if (user.Length > 0)
                {
                    if (!(s is SslStream)) { log("Authentification refusée par AMI·OPS sur une connexion NON chiffrée (le mot de passe circulerait en clair).", "bad"); return; }
                    send(s, "AUTH LOGIN", null); read(s);
                    send(s, Convert.ToBase64String(Encoding.UTF8.GetBytes(user)), "(identifiant)"); read(s);
                    send(s, Convert.ToBase64String(Encoding.UTF8.GetBytes(pass)), "(mot de passe masqué)"); var a = read(s);
                    if (a.StartsWith("235")) log("AUTHENTIFICATION OK : identifiant et mot de passe acceptés.", "ok");
                    else
                    {
                        log("AUTHENTIFICATION REFUSÉE : " + a.Trim(), "bad");
                        if (Regex.IsMatch(a, "5.7.139|SmtpClientAuthentication|basic auth", RegexOptions.IgnoreCase)) log("  Microsoft 365 : SMTP AUTH désactivé (activer pour la boîte dans le centre d'admin Exchange, ou utiliser OAuth / relais).", "warn");
                        else if (a.StartsWith("535")) log("  Mot de passe / identifiant faux, compte bloqué, ou mot de passe d'application requis (2FA).", "warn");
                        return;
                    }
                }
                if (to.Length > 0)
                {
                    send(s, "MAIL FROM:<" + from + ">", null); if (!read(s).StartsWith("250")) { log("Expéditeur refusé (le compte n'a pas le droit d'envoyer avec cette adresse).", "bad"); return; }
                    send(s, "RCPT TO:<" + to + ">", null); if (!read(s).StartsWith("25")) { log("Destinataire refusé (relais interdit ?).", "bad"); return; }
                    send(s, "DATA", null); read(s);
                    var msg = "From: <" + from + ">\r\nTo: <" + to + ">\r\nSubject: Test AMI-OPS " + DateTime.Now.ToString("dd/MM HH:mm") + "\r\nDate: " + DateTime.Now.ToString("r") + "\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nMessage de test envoye par AMI-OPS (by alex.b) depuis " + Environment.MachineName + ".\r\n.";
                    send(s, msg, "(message de test)"); var r2 = read(s);
                    log(r2.StartsWith("250") ? "MESSAGE ACCEPTÉ par le serveur : vérifier la réception (et le dossier spam)." : "Message refusé : " + r2.Trim(), r2.StartsWith("250") ? "ok" : "bad");
                }
                send(s, "QUIT", null); try { read(s); } catch { }
            }
        }

        static string ReadLine(Stream s)
        {
            var sb = new StringBuilder(); int b;
            while ((b = s.ReadByte()) >= 0) { if (b == '\n') break; if (b != '\r') sb.Append((char)b); }
            return b < 0 && sb.Length == 0 ? null : sb.ToString();
        }
    }

    class NetDiagPage : HubPage
    {
        public override string Title { get { return "Diagnostic réseau"; } }

        public NetDiagPage() : base("DIAGNOSTIC RÉSEAU", "Wi-Fi, coupures, DHCP, débit, envoi de mail.", 120)
        {
            T(Theme.Icon("", "≋"), "Analyse Wi-Fi", "signal, canaux, conseil", t => t.Accent, () => Wifi.Analyze(Out));
            T(Theme.Icon("", "∿"), "Surveillance coupures", "box / Internet / DNS en continu", t => t.Warn, () => new MonitorForm().Show(this), false);
            T(Theme.Icon("", "⚑"), "DHCP pirate", "2 serveurs DHCP ?", t => t.Bad, () => NetTools.Dhcp(Out));
            T(Theme.Icon("", "⇅"), "Débit : serveur", "sur le 1er PC", t => t.Accent2, () => { if (NetTools.ServerRunning) NetTools.ServerStop(Out); else NetTools.ServerStart(Out); }, false);
            T(Theme.Icon("", "⇅"), "Débit : client", "sur le 2e PC", t => t.Accent2, () =>
            {
                var h = Dlg.Ask(this, "Test de débit", "Adresse IP du PC où « Débit : serveur » est lancé", Prefs.Ini.Get("IperfHost", ""), false);
                if (h == null) return; Prefs.Ini.Set("IperfHost", h); Prefs.Save();
                Start("Débit vers " + h, () => NetTools.Client(h, Out));
            }, false);
            T(Theme.Icon("", "✉"), "Test d'envoi SMTP", "identifiants, port, TLS", t => t.Ok, SmtpDialog, false);
        }

        void SmtpDialog()
        {
            var fh = new F("Serveur SMTP (ex. smtp.office365.com, ssl0.ovh.net, mail.domaine.fr)", Prefs.Ini.Get("SmtpHost", "")) { Required = true };
            var fm = new F("Mode", Prefs.Ini.Get("SmtpMode", "STARTTLS (587)")) { Options = new[] { "STARTTLS (587)", "SSL/TLS (465)", "Sans chiffrement (25)" }, Editable = false };
            var fu = new F("Identifiant (vide = tester seulement la connexion)", ""); var fp = new F("Mot de passe (jamais enregistré)", "") { Password = true };
            var ff = new F("Expéditeur (pour un envoi réel)", ""); var ft = new F("Destinataire (vide = pas d'envoi)", "");
            if (!Dlg.Edit(this, "Test SMTP", fh, fm, fu, fp, ff, ft)) return;
            Prefs.Ini.Set("SmtpHost", fh.Value); Prefs.Ini.Set("SmtpMode", fm.Value); Prefs.Save();
            int port = fm.Value.StartsWith("SSL") ? 465 : fm.Value.StartsWith("Sans") ? 25 : 587;
            var from = ff.Value.Length > 0 ? ff.Value : fu.Value;
            Start("Test SMTP " + fh.Value + ":" + port, () => NetTools.Smtp(fh.Value, port, fm.Value, fu.Value, fp.Value, from, ft.Value, Out));
        }
    }
}
