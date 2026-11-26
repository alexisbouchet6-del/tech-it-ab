// ScanParc - inventaire du réseau local (IP, MAC, fabricant, type, ports)
// Développé by alex.b

using System;
using System.Collections.Concurrent;
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
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlexB;

[assembly: AssemblyTitle("ScanParc")]
[assembly: AssemblyDescription("Inventaire du réseau local")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("ScanParc")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ScanParc
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            AppInfo.Name = "ScanParc"; AppInfo.Id = "ScanParc"; AppInfo.Version = "1.0.0"; AppInfo.MachineWide = false;
            Boot.Run(() => new MainForm(), false, null);
        }
    }

    class Device
    {
        public IPAddress Ip;
        public string Mac = "", Vendor = "", Name = "", Type = "", Info = "";
        public long Ms = -1;
        public List<int> Ports = new List<int>();
        public uint Sort { get { var b = Ip.GetAddressBytes(); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]; } }
    }

    // ===================================================================== Moteur de scan
    static class Scanner
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        static extern int SendARP(uint destIp, uint srcIp, byte[] mac, ref int len);

        public static readonly int[] Ports = { 22, 80, 135, 443, 445, 3389, 5000, 5001, 5060, 8080, 9100, 631 };

        public static string OuiFile { get { return Path.Combine(AppInfo.DataDir, "fabricants.txt"); } }

        // Préfixes MAC : fichier modifiable. Seuls des préfixes vérifiés sont fournis ; complète avec ceux de tes clients.
        public static Dictionary<string, string> LoadOui()
        {
            if (!File.Exists(OuiFile))
                File.WriteAllText(OuiFile,
                    "# ScanParc (by alex.b) - préfixes MAC (6 premiers caractères hexa) = fabricant\r\n" +
                    "# Ajoute les tiens : relève la MAC d'un appareil connu (étiquette) et note ses 6 premiers caractères.\r\n" +
                    "# Liste officielle IEEE : https://standards-oui.ieee.org/oui/oui.txt\r\n" +
                    "001132 = Synology\r\n" +
                    "001565 = Yealink\r\n" +
                    "805EC0 = Yealink\r\n" +
                    "7C2F80 = Gigaset\r\n" +
                    "000C29 = VMware (VM)\r\n" +
                    "005056 = VMware (VM)\r\n" +
                    "00155D = Hyper-V (VM)\r\n" +
                    "080027 = VirtualBox (VM)\r\n", new UTF8Encoding(false));
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(OuiFile, Encoding.UTF8))
            {
                var l = raw.Trim(); if (l.Length == 0 || l.StartsWith("#")) continue;
                int i = l.IndexOf('='); if (i < 0) continue;
                var k = Regex.Replace(l.Substring(0, i), "[^0-9A-Fa-f]", "");
                if (k.Length >= 6) d[k.Substring(0, 6)] = l.Substring(i + 1).Trim();
            }
            return d;
        }

        // Plage proposée : réseau de la carte active (limité à /22 = 1022 hôtes)
        public static string DefaultRange(out string adapter)
        {
            adapter = "";
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var p = n.GetIPProperties();
                if (!p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                foreach (var u in p.UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                    int bits = u.IPv4Mask.GetAddressBytes().Sum(b => Convert.ToString(b, 2).Count(c => c == '1'));
                    int real = bits;
                    if (bits < 22) bits = 24;   // grand réseau : on propose le /24 du poste
                    var net = Network(u.Address, bits);
                    adapter = n.Name + " — " + u.Address + "/" + real;
                    return net + "/" + bits;
                }
            }
            return "192.168.1.0/24";
        }

        static uint ToUint(IPAddress a) { var b = a.GetAddressBytes(); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]; }
        static IPAddress FromUint(uint v) { return new IPAddress(new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }); }
        static IPAddress Network(IPAddress a, int bits) { uint m = bits == 0 ? 0 : 0xFFFFFFFF << (32 - bits); return FromUint(ToUint(a) & m); }

        // Accepte "192.168.1.0/24" ou "192.168.1.10-192.168.1.80" ; 1024 adresses max
        public static List<IPAddress> Parse(string s, out string err)
        {
            err = null; var list = new List<IPAddress>(); s = (s ?? "").Trim();
            IPAddress a, b; uint from, to;
            var m = Regex.Match(s, @"^(\d+\.\d+\.\d+\.\d+)\s*/\s*(\d+)$");
            var r = Regex.Match(s, @"^(\d+\.\d+\.\d+\.\d+)\s*-\s*(\d+\.\d+\.\d+\.\d+)$");
            if (m.Success && IPAddress.TryParse(m.Groups[1].Value, out a) && a.AddressFamily == AddressFamily.InterNetwork)
            {
                int bits = int.Parse(m.Groups[2].Value);
                if (bits < 22 || bits > 30) { err = "Masque accepté : /22 à /30."; return list; }
                from = ToUint(Network(a, bits)) + 1; to = ToUint(Network(a, bits)) + (uint)(1 << (32 - bits)) - 2;
            }
            else if (r.Success && IPAddress.TryParse(r.Groups[1].Value, out a) && IPAddress.TryParse(r.Groups[2].Value, out b))
            { from = ToUint(a); to = ToUint(b); }
            else { err = "Format attendu : 192.168.1.0/24 ou 192.168.1.10-192.168.1.80"; return list; }
            if (to < from || to - from > 1023) { err = "Plage vide ou trop grande (1024 adresses max)."; return list; }
            for (uint v = from; v <= to; v++) list.Add(FromUint(v));
            return list;
        }

        static string Mac(IPAddress ip)
        {
            try
            {
                var mac = new byte[6]; int len = 6;
                if (SendARP(BitConverter.ToUInt32(ip.GetAddressBytes(), 0), 0, mac, ref len) == 0 && len == 6 && mac.Any(x => x != 0))
                    return string.Join(":", mac.Select(x => x.ToString("X2")));
            }
            catch { }
            return "";
        }

        static bool Open(IPAddress ip, int port, int timeout)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeout)) return false;
                    c.EndConnect(ar); return true;
                }
            }
            catch { return false; }
        }

        // Titre de la page web d'administration (Yealink, Gigaset, imprimante, box, DSM…)
        static string WebTitle(IPAddress ip, int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(800)) return "";
                    c.EndConnect(ar);
                    Stream s = c.GetStream(); s.ReadTimeout = 1500; s.WriteTimeout = 1500;
                    if (port == 443 || port == 5001)
                    {
                        // appareils locaux : certificat auto-signé attendu, on lit seulement le titre (aucune donnée envoyée)
                        var ssl = new SslStream(s, false, (o, cert, ch, e) => true);
                        ssl.AuthenticateAsClient(ip.ToString());
                        s = ssl;
                    }
                    var req = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nHost: " + ip + "\r\nUser-Agent: ScanParc\r\n\r\n");
                    s.Write(req, 0, req.Length);
                    var buf = new byte[16384]; int total = 0, n;
                    var sw = Stopwatch.StartNew();
                    while (total < buf.Length && sw.ElapsedMilliseconds < 2000 && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
                    var txt = Encoding.UTF8.GetString(buf, 0, total);
                    var t = Regex.Match(txt, @"<title[^>]*>\s*([^<]{1,80})", RegexOptions.IgnoreCase);
                    if (t.Success) return WebUtilityDecode(t.Groups[1].Value.Trim());
                    var sv = Regex.Match(txt, @"^Server:\s*(.{1,60})$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    return sv.Success ? sv.Groups[1].Value.Trim() : "";
                }
            }
            catch { return ""; }
        }

        static string WebUtilityDecode(string s) { try { return WebUtility.HtmlDecode(s); } catch { return s; } }

        static string Guess(Device d, IPAddress gateway)
        {
            string all = (d.Vendor + " " + d.Info + " " + d.Name).ToLowerInvariant();
            if (gateway != null && d.Ip.Equals(gateway)) return "Box / routeur";
            if (all.Contains("synology") || d.Ports.Contains(5000) || d.Ports.Contains(5001)) return "NAS Synology";
            if (all.Contains("yealink")) return "Téléphone IP Yealink";
            if (all.Contains("gigaset")) return "Téléphone / base Gigaset";
            if (d.Ports.Contains(5060)) return "Équipement VoIP";
            if (d.Ports.Contains(9100) || d.Ports.Contains(631) || Regex.IsMatch(all, "printer|imprim|laserjet|officejet|brother|epson|canon|kyocera|ricoh|xerox|lexmark")) return "Imprimante";
            if (all.Contains("(vm)")) return "Machine virtuelle";
            if (d.Ports.Contains(445) && d.Ports.Contains(3389)) return "PC / serveur Windows (RDP)";
            if (d.Ports.Contains(445) || d.Ports.Contains(135)) return "PC / serveur Windows";
            if (d.Ports.Contains(22) && !d.Ports.Contains(80)) return "Linux / équipement SSH";
            if (Regex.IsMatch(all, "unifi|ubiquiti|tp-link|netgear|cisco|aruba|zyxel|mikrotik|d-link")) return "Équipement réseau";
            if (d.Ports.Contains(80) || d.Ports.Contains(443) || d.Ports.Contains(8080)) return "Appareil avec page web";
            return d.Mac.Length > 0 ? "Inconnu" : "Inconnu (hors sous-réseau)";
        }

        public static IPAddress Gateway()
        {
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                    if (n.OperationalStatus == OperationalStatus.Up)
                        foreach (var g in n.GetIPProperties().GatewayAddresses)
                            if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) return g.Address;
            }
            catch { }
            return null;
        }

        // Scan : ping + ARP (les appareils qui bloquent le ping sont trouvés par ARP), puis détail des hôtes trouvés
        public static List<Device> Run(List<IPAddress> ips, bool deep, Action<int, int, Device> progress, CancellationToken ct)
        {
            var oui = LoadOui(); var gw = Gateway();
            var found = new ConcurrentBag<Device>(); int done = 0;
            var po = new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct };
            try
            {
                Parallel.ForEach(ips, po, ip =>
                {
                    long ms = -1;
                    try { using (var p = new Ping()) { var r = p.Send(ip, 700); if (r.Status == IPStatus.Success) ms = r.RoundtripTime; } } catch { }
                    string mac = Mac(ip);
                    if (ms >= 0 || mac.Length > 0)
                    {
                        var d = new Device { Ip = ip, Ms = ms, Mac = mac };
                        if (mac.Length > 0) { string v; if (oui.TryGetValue(mac.Replace(":", "").Substring(0, 6), out v)) d.Vendor = v; else if ((Convert.ToByte(mac.Substring(0, 2), 16) & 2) != 0) d.Vendor = "MAC aléatoire (smartphone ?)"; }
                        found.Add(d);
                        progress(Interlocked.Increment(ref done), ips.Count, d);
                    }
                    else progress(Interlocked.Increment(ref done), ips.Count, null);
                });
            }
            catch (OperationCanceledException) { }
            var list = found.OrderBy(d => d.Sort).ToList();
            if (ct.IsCancellationRequested) return list;
            Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = 24 }, d =>
            {
                try { var h = Dns.GetHostEntry(d.Ip); if (h.HostName != d.Ip.ToString()) d.Name = h.HostName; } catch { }
                if (deep)
                {
                    var open = new List<int>();
                    foreach (var port in Ports) if (Open(d.Ip, port, 350)) open.Add(port);
                    d.Ports = open;   // liste remplacée d'un bloc (lue par le thread UI)
                    foreach (var port in new[] { 80, 443, 5000, 8080 })
                        if (open.Contains(port)) { var t = WebTitle(d.Ip, port); if (t.Length > 0) { d.Info = t; break; } }
                }
                d.Type = Guess(d, gw);
                progress(-1, list.Count, d);
            });
            return list;
        }
    }

    // ===================================================================== Fenêtre
    class MainForm : CyberForm
    {
        readonly TextBox tRange;
        readonly CheckBox cDeep;
        readonly Chip bScan, bCsv, bCopy, bOui;
        readonly NeonBar bar;
        readonly Label lStat;
        readonly ListView lv;
        List<Device> last = new List<Device>();
        CancellationTokenSource cts;

        public MainForm() : base("SCAN_PARC", 1080, 720)
        {
            string adapter; string range = Scanner.DefaultRange(out adapter);
            SetSubtitle("Inventaire du réseau local  //  " + (adapter.Length > 0 ? adapter : "aucune carte active") + "  //  by alex.b");
            Func<float, int> k = Theme.S;
            var lr = new Label { Text = "Plage :", AutoSize = true, Location = new Point(k(16), k(18)), Tag = "muted" };
            tRange = new TextBox { Text = Prefs.Ini.Get("Range", range), Location = new Point(k(70), k(14)), Width = k(230), Font = new Font("Consolas", 10f) };
            cDeep = new CheckBox { Text = "Ports + page web (plus lent)", Checked = Prefs.Ini.GetBool("Deep", true), AutoSize = true, Location = new Point(k(312), k(16)) };
            bScan = new Chip("▶  SCANNER") { Primary = true, AccentOf = t => t.Ok, Location = new Point(k(530), k(10)) };
            bCsv = new Chip("Exporter CSV") { AccentOf = t => t.Accent, Location = new Point(bScan.Right + k(8), k(10)) };
            bCopy = new Chip("Copier pour le ticket") { AccentOf = t => t.Accent2, Location = new Point(bCsv.Right + k(8), k(10)) };
            bOui = new Chip("Fabricants…") { AccentOf = t => t.Muted, Location = new Point(bCopy.Right + k(8), k(10)) };
            bar = new NeonBar { Location = new Point(k(16), k(52)), Size = new Size(Body.Width - k(32), k(6)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            lStat = new Label { Text = "Prêt. Plage proposée = réseau de la carte active. Aucun droit admin nécessaire.", AutoSize = true, Location = new Point(k(16), k(64)), Tag = "muted" };
            lv = new ListView { View = View.Details, FullRowSelect = true, GridLines = false, HideSelection = false, BorderStyle = BorderStyle.None,
                Location = new Point(k(16), k(88)), Size = new Size(Body.Width - k(32), Body.Height - k(100)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, Font = new Font("Consolas", 9f) };
            foreach (var c in new[] { Tuple.Create("IP", 115), Tuple.Create("Nom", 170), Tuple.Create("MAC", 135), Tuple.Create("Fabricant", 120), Tuple.Create("Type", 190), Tuple.Create("Ports", 150), Tuple.Create("Page web / info", 200), Tuple.Create("Ping", 55) })
                lv.Columns.Add(c.Item1, k(c.Item2));
            var cm = new ContextMenuStrip();
            cm.Items.Add("Ouvrir la page web (http)", null, (s, e) => OpenSel("http"));
            cm.Items.Add("Ouvrir la page web (https)", null, (s, e) => OpenSel("https"));
            cm.Items.Add("Ouvrir DSM (port 5001)", null, (s, e) => OpenSel("dsm"));
            cm.Items.Add("Bureau à distance (mstsc)", null, (s, e) => { var d = Sel(); if (d != null) Process.Start("mstsc.exe", "/v:" + d.Ip); });
            cm.Items.Add("Ouvrir les partages (\\\\IP)", null, (s, e) => { var d = Sel(); if (d != null) Process.Start("explorer.exe", "\\\\" + d.Ip); });
            cm.Items.Add("Copier l'IP", null, (s, e) => { var d = Sel(); if (d != null) Clipboard.SetText(d.Ip.ToString()); });
            cm.Items.Add("Copier la MAC", null, (s, e) => { var d = Sel(); if (d != null && d.Mac.Length > 0) Clipboard.SetText(d.Mac); });
            lv.ContextMenuStrip = cm;
            lv.DoubleClick += (s, e) => OpenSel("auto");
            bScan.Click += (s, e) => Scan();
            bCsv.Click += (s, e) => ExportCsv();
            bCopy.Click += (s, e) => { if (last.Count == 0) return; Clipboard.SetText(Report()); bCopy.SetText("✓ Copié"); };
            bOui.Click += (s, e) => { Scanner.LoadOui(); Process.Start("notepad.exe", "\"" + Scanner.OuiFile + "\""); };
            Body.Controls.AddRange(new Control[] { lr, tRange, cDeep, bScan, bCsv, bCopy, bOui, bar, lStat, lv });
            AcceptButton = null;
            tRange.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Scan(); } };
        }

        Device Sel() { return lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Tag as Device : null; }

        void OpenSel(string mode)
        {
            var d = Sel(); if (d == null) return;
            string url;
            if (mode == "dsm" || (mode == "auto" && d.Ports.Contains(5001))) url = "https://" + d.Ip + ":5001";
            else if (mode == "https" || (mode == "auto" && d.Ports.Contains(443))) url = "https://" + d.Ip;
            else if (mode == "auto" && !d.Ports.Contains(80) && d.Ports.Contains(8080)) url = "http://" + d.Ip + ":8080";
            else url = "http://" + d.Ip;
            try { Process.Start(url); } catch (Exception ex) { Warn(this, ex.Message); }
        }

        ListViewItem Row(Device d)
        {
            var it = new ListViewItem(new[] { d.Ip.ToString(), d.Name, d.Mac, d.Vendor, d.Type, string.Join(",", d.Ports), d.Info, d.Ms >= 0 ? d.Ms + " ms" : "—" }) { Tag = d };
            var t = Theme.Current;
            it.ForeColor = d.Type.StartsWith("NAS") ? t.Accent : d.Type.Contains("phone") || d.Type.Contains("VoIP") ? t.Accent2 : d.Type.StartsWith("Inconnu") ? t.Muted : t.Text;
            return it;
        }

        void Scan()
        {
            if (cts != null) { cts.Cancel(); return; }
            string err; var ips = Scanner.Parse(tRange.Text, out err);
            if (err != null) { Warn(this, err); return; }
            Prefs.Ini.Set("Range", tRange.Text.Trim()); Prefs.Ini.Set("Deep", cDeep.Checked); Prefs.Save();
            cts = new CancellationTokenSource(); var ct = cts.Token;
            bScan.SetText("■  ARRÊTER"); bCopy.SetText("Copier pour le ticket");
            lv.Items.Clear(); bar.Value = 0; last.Clear();
            bool deep = cDeep.Checked; var sw = Stopwatch.StartNew();
            Log.Write("Scan " + tRange.Text + " (" + ips.Count + " adresses)");
            Task.Run(() =>
            {
                List<Device> res = null;
                try
                {
                    res = Scanner.Run(ips, deep, (done, total, d) => BeginInvoke(new Action(() =>
                    {
                        if (done > 0) { bar.Value = (double)done / total * (deep ? 0.6 : 0.9); lStat.Text = "Recherche… " + done + " / " + total; }
                        else { bar.Value = Math.Min(1, bar.Value + 0.4 / Math.Max(1, total)); lStat.Text = "Détail des appareils…"; }
                        if (d != null)
                        {
                            var ex = lv.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag == d);
                            if (ex != null) lv.Items[ex.Index] = Row(d); else lv.Items.Add(Row(d));
                        }
                    })), ct);
                }
                catch (Exception ex) { Log.Write(ex.ToString()); }
                BeginInvoke(new Action(() =>
                {
                    cts = null; bScan.SetText("▶  SCANNER"); bar.Value = 1;
                    last = res ?? new List<Device>();
                    lv.BeginUpdate(); lv.Items.Clear(); foreach (var d in last) lv.Items.Add(Row(d)); lv.EndUpdate();
                    var byType = last.GroupBy(d => d.Type).OrderByDescending(g => g.Count()).Select(g => g.Count() + " " + g.Key.ToLowerInvariant());
                    lStat.Text = (ct.IsCancellationRequested ? "Arrêté : " : "") + last.Count + " appareil(s) en " + sw.Elapsed.TotalSeconds.ToString("0") + " s  ·  " + string.Join("  ·  ", byType.Take(5));
                    Log.Write("Scan terminé : " + last.Count + " appareil(s)");
                }));
            });
        }

        string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("INVENTAIRE RÉSEAU — " + tRange.Text.Trim() + " — " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " — depuis " + Environment.MachineName + " (ScanParc, by alex.b)");
            sb.AppendLine(last.Count + " appareil(s)");
            sb.AppendLine();
            foreach (var d in last)
                sb.AppendLine(d.Ip.ToString().PadRight(16) + (d.Mac.Length > 0 ? d.Mac : "—").PadRight(19) + d.Type.PadRight(28) + (d.Name.Length > 0 ? d.Name : "") + (d.Info.Length > 0 ? "  [" + d.Info + "]" : ""));
            return sb.ToString();
        }

        static string Csv(string s) { s = s ?? ""; if (s.Length > 0 && "=+-@".IndexOf(s[0]) >= 0) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }

        void ExportCsv()
        {
            if (last.Count == 0) { Info(this, "Lance un scan d'abord."); return; }
            using (var d = new SaveFileDialog { Filter = "CSV (Excel)|*.csv", FileName = "ScanParc_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder("IP;Nom;MAC;Fabricant;Type;Ports;Info;Ping ms\r\n");
                foreach (var x in last)
                    sb.Append(string.Join(";", new[] { x.Ip.ToString(), x.Name, x.Mac, x.Vendor, x.Type, string.Join(" ", x.Ports), x.Info, x.Ms >= 0 ? x.Ms.ToString() : "" }.Select(Csv))).Append("\r\n");
                File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(true));
                Info(this, "Export : " + d.FileName);
            }
        }
    }
}
