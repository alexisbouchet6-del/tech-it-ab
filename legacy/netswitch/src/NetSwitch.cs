// NetSwitch - utilitaire de zone de notification Windows
// Bascule rapide DHCP / IP statique (192.168.X.Y) sur les cartes Wi-Fi et Ethernet.
// Cible : .NET Framework 4.8 (inclus dans Windows 10 / 11).
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("NetSwitch")]
[assembly: AssemblyDescription("Bascule rapide DHCP / IP statique")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("NetSwitch")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.1.2.0")]
[assembly: AssemblyFileVersion("1.1.2.0")]

namespace NetSwitch
{
    // ===================================================================== Programme
    static class Program
    {
        public const string AppName = "NetSwitch";
        public const string Version = "1.1.2";

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            AlexBHarden.Harden.Apply(Logger.Dir);
            // Commandes appelées par l'installateur (déjà élevé)
            if (args.Length > 0)
            {
                string a = args[0].ToLowerInvariant();
                if (a == "--autostart-on") return AutoStart.Enable() ? 0 : 1;
                if (a == "--autostart-off") { AutoStart.Disable(); return 0; }
            }

            // Modifier la config IP exige les droits administrateur
            if (!IsAdmin())
            {
                try
                {
                    var psi = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" };
                    Process.Start(psi);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    MessageBox.Show("NetSwitch a besoin des droits administrateur pour modifier la configuration réseau.",
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return 0;
            }

            bool created;
            using (var mutex = new Mutex(true, "Global\\NetSwitchTray_AlexisB", out created))
            {
                if (!created)
                {
                    MessageBox.Show("NetSwitch est déjà lancé : icône en bas à droite, près de l'horloge\n(cliquer sur ^ si elle est masquée).",
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Logger.Write("ERREUR UI : " + e.Exception);
                Application.Run(new TrayContext());
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        public static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    // ===================================================================== Journal
    static class Logger
    {
        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetSwitch"); }
        }
        public static string FilePath { get { return Path.Combine(Dir, "netswitch.log"); } }

        static readonly object Lock = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();   // rotation simple à 1 Mo
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ===================================================================== Configuration
    class Config
    {
        public string Base = "192.168";          // deux premiers octets
        public int HostOctet = 100;              // dernier octet de l'IP statique
        public int GatewayOctet = 1;             // passerelle = Base.X.GatewayOctet
        public int Prefix = 24;                  // /24 = 255.255.255.0
        public string Dns2 = "1.1.1.1";          // DNS 1 = passerelle, DNS 2 = celui-ci (vide = aucun)
        public List<int> Presets = new List<int> { 1, 2 };
        public bool DarkTheme = true;
        public bool TipShown = false;

        static string FilePath { get { return Path.Combine(Logger.Dir, "config.ini"); } }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(FilePath)) { c.Save(); return c; }
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    int n;
                    switch (k)
                    {
                        case "base": if (Validate.IsBase(v)) c.Base = v; break;
                        case "hostoctet": if (int.TryParse(v, out n) && n >= 1 && n <= 254) c.HostOctet = n; break;
                        case "gatewayoctet": if (int.TryParse(v, out n) && n >= 1 && n <= 254) c.GatewayOctet = n; break;
                        case "prefix": if (int.TryParse(v, out n) && n >= 8 && n <= 30) c.Prefix = n; break;
                        case "dns2": if (v.Length == 0 || Validate.IsIPv4(v)) c.Dns2 = v; break;
                        case "theme": c.DarkTheme = v.ToLowerInvariant() != "light"; break;
                        case "tipshown": c.TipShown = v == "1"; break;
                        case "presets":
                            List<int> p;
                            if (Validate.TryParsePresets(v, out p)) c.Presets = p;
                            break;
                    }
                }
            }
            catch (Exception ex) { Logger.Write("Lecture config impossible : " + ex.Message); }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Logger.Dir);
                var sb = new StringBuilder();
                sb.AppendLine("# NetSwitch - configuration (by alex.b)");
                sb.AppendLine("# IP statique = Base.<plage>.HostOctet   passerelle = Base.<plage>.GatewayOctet");
                sb.AppendLine("Base=" + Base);
                sb.AppendLine("HostOctet=" + HostOctet);
                sb.AppendLine("GatewayOctet=" + GatewayOctet);
                sb.AppendLine("Prefix=" + Prefix);
                sb.AppendLine("Dns2=" + Dns2);
                sb.AppendLine("Presets=" + string.Join(",", Presets));
                sb.AppendLine("Theme=" + (DarkTheme ? "dark" : "light"));
                sb.AppendLine("TipShown=" + (TipShown ? "1" : "0"));
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Logger.Write("Écriture config impossible : " + ex.Message); }
        }
    }

    // ===================================================================== Validation IP
    static class Validate
    {
        static readonly Regex Octet = new Regex(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)$");

        public static bool IsIPv4(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Trim().Split('.');
            return parts.Length == 4 && parts.All(p => Octet.IsMatch(p));
        }

        public static bool IsBase(string s)
        {
            var parts = (s ?? "").Trim().Split('.');
            return parts.Length == 2 && parts.All(p => Octet.IsMatch(p));
        }

        public static bool TryParsePresets(string s, out List<int> list)
        {
            list = new List<int>();
            foreach (var part in (s ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int n;
                if (!int.TryParse(part, out n) || n < 0 || n > 255) return false;
                if (!list.Contains(n)) list.Add(n);
            }
            return list.Count >= 1 && list.Count <= 12;
        }

        public static uint ToUInt(string ip)
        {
            var b = IPAddress.Parse(ip).GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        public static uint MaskOf(int prefix)
        {
            return prefix <= 0 ? 0u : uint.MaxValue << (32 - prefix);
        }

        public static string PrefixToMask(int prefix)
        {
            uint m = MaskOf(prefix);
            return string.Format("{0}.{1}.{2}.{3}", m >> 24, (m >> 16) & 255, (m >> 8) & 255, m & 255);
        }

        public static int MaskToPrefix(IPAddress mask)
        {
            if (mask == null) return 0;
            int bits = 0;
            foreach (var b in mask.GetAddressBytes()) { byte x = b; while (x != 0) { bits += x & 1; x >>= 1; } }
            return bits;
        }

        // Accepte "24" ou "255.255.255.0"
        public static bool TryParsePrefix(string s, out int prefix)
        {
            prefix = 0;
            s = (s ?? "").Trim().TrimStart('/');
            if (int.TryParse(s, out prefix)) return prefix >= 1 && prefix <= 30;
            if (!IsIPv4(s)) return false;
            uint m = ToUInt(s);
            uint inv = ~m;
            if ((inv & (inv + 1)) != 0) return false;   // masque non contigu
            prefix = MaskToPrefix(IPAddress.Parse(s));
            return prefix >= 1 && prefix <= 30;
        }

        // Retourne null si OK, sinon le message d'erreur
        public static string CheckStatic(string ip, int prefix, string gw, IList<string> dns)
        {
            if (!IsIPv4(ip)) return "Adresse IP invalide : " + ip;
            if (prefix < 1 || prefix > 30) return "Préfixe invalide : /" + prefix + " (1 à 30)";
            uint mask = MaskOf(prefix), a = ToUInt(ip);
            if ((a & ~mask) == 0) return ip + " est l'adresse du réseau, pas une adresse d'hôte.";
            if ((a & ~mask) == ~mask) return ip + " est l'adresse de broadcast du réseau.";
            if (!string.IsNullOrEmpty(gw))
            {
                if (!IsIPv4(gw)) return "Passerelle invalide : " + gw;
                if ((ToUInt(gw) & mask) != (a & mask)) return "La passerelle " + gw + " n'est pas dans le réseau de " + ip + "/" + prefix + ".";
                if (gw == ip) return "L'IP et la passerelle sont identiques.";
            }
            foreach (var d in dns) if (!IsIPv4(d)) return "DNS invalide : " + d;
            return null;
        }
    }

    // ===================================================================== Carte réseau
    class AdapterInfo
    {
        public string Name = "", Description = "", Mac = "";
        public bool IsWifi, IsUp, Dhcp;
        public List<string> Ips = new List<string>();       // "ip/prefixe"
        public List<string> RawIps = new List<string>();    // "ip"
        public List<string> DuplicateIps = new List<string>();
        public string Gateway = "";
        public List<string> Dns = new List<string>();

        public string Kind { get { return IsWifi ? "Wi-Fi" : "Ethernet"; } }
        public string Mode { get { return Dhcp ? "DHCP" : "Statique"; } }

        public string StateText
        {
            get
            {
                string ip = RawIps.FirstOrDefault(i => !i.StartsWith("169.254.")) ?? RawIps.FirstOrDefault();
                if (!IsUp) return Mode + " · déconnectée";
                return Mode + " · " + (ip ?? "pas d'IP");
            }
        }
    }

    class OpResult
    {
        public bool Ok; public string Message;
        public static OpResult Success(string m) { return new OpResult { Ok = true, Message = m }; }
        public static OpResult Fail(string m) { return new OpResult { Ok = false, Message = m }; }
    }

    static class NetOps
    {
        static readonly string[] Excluded =
        {
            "virtual", "vmware", "hyper-v", "virtualbox", "vpn", "tap-", "tap ", "wan miniport", "bluetooth",
            "loopback", "pseudo", "tunnel", "wireguard", "openvpn", "zerotier", "tailscale", "npcap",
            "kernel debug", "fortinet", "cisco anyconnect", "teredo", "isatap"
        };

        public static List<AdapterInfo> GetAdapters()
        {
            var list = new List<AdapterInfo>();
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch { return list; }

            foreach (var ni in all)
            {
                var t = ni.NetworkInterfaceType;
                bool wifi = t == NetworkInterfaceType.Wireless80211;
                bool eth = t == NetworkInterfaceType.Ethernet || t == NetworkInterfaceType.GigabitEthernet ||
                           t == NetworkInterfaceType.FastEthernetT || t == NetworkInterfaceType.FastEthernetFx ||
                           t == NetworkInterfaceType.Ethernet3Megabit;
                if (!wifi && !eth) continue;

                string d = (ni.Description ?? "").ToLowerInvariant();
                string n = (ni.Name ?? "").ToLowerInvariant();
                if (Excluded.Any(k => d.Contains(k)) || n.StartsWith("vethernet")) continue;

                var a = new AdapterInfo
                {
                    Name = ni.Name,
                    Description = ni.Description,
                    IsWifi = wifi,
                    IsUp = ni.OperationalStatus == OperationalStatus.Up,
                };
                try { a.Mac = string.Join("-", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))); } catch { }

                try
                {
                    var p = ni.GetIPProperties();
                    try { var v4 = p.GetIPv4Properties(); a.Dhcp = v4 != null && v4.IsDhcpEnabled; } catch { }
                    foreach (var u in p.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = u.Address.ToString();
                        int pre = 0;
                        try { pre = Validate.MaskToPrefix(u.IPv4Mask); } catch { }
                        a.RawIps.Add(ip);
                        a.Ips.Add(pre > 0 ? ip + "/" + pre : ip);
                        try { if (u.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Duplicate) a.DuplicateIps.Add(ip); } catch { }
                    }
                    var gw = p.GatewayAddresses.Select(g => g.Address)
                              .FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any));
                    if (gw != null) a.Gateway = gw.ToString();
                    a.Dns = p.DnsAddresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork).Select(x => x.ToString()).ToList();
                }
                catch { }
                list.Add(a);
            }
            return list.OrderBy(x => x.IsWifi ? 1 : 0).ThenBy(x => x.Name).ToList();
        }

        public static AdapterInfo Find(string name)
        {
            return GetAdapters().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        static Encoding Oem
        {
            get
            {
                try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
                catch { return Encoding.Default; }
            }
        }

        public static int Run(string exe, string args, out string output, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Oem,
                StandardErrorEncoding = Oem
            };
            using (var p = Process.Start(psi))
            {
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    output = "Délai dépassé (" + (timeoutMs / 1000) + " s)";
                    return -1;
                }
                output = (so.Result + " " + se.Result).Trim();
                return p.ExitCode;
            }
        }

        static string Sys(string exe) { return Path.Combine(Environment.SystemDirectory, exe); }

        static int Netsh(string args, out string output)
        {
            int rc = Run(Sys("netsh.exe"), args, out output, 30000);
            Logger.Write("netsh " + args + "  => " + rc + (output.Length > 0 ? "  " + output.Replace("\r", " ").Replace("\n", " ") : ""));
            return rc;
        }

        static string Q(string name) { return "\"" + name.Replace("\"", "") + "\""; }

        public static OpResult SetDhcp(string name)
        {
            var a = Find(name);
            if (a == null) return OpResult.Fail("Carte « " + name + " » introuvable.");
            string o;

            if (!a.Dhcp)
            {
                // supprime la route par défaut statique pour ne pas la garder en plus de celle du DHCP
                if (a.Gateway.Length > 0)
                    Netsh("interface ipv4 delete route prefix=0.0.0.0/0 interface=" + Q(name) + " nexthop=" + a.Gateway + " store=persistent", out o);
                if (Netsh("interface ipv4 set address name=" + Q(name) + " source=dhcp", out o) != 0)
                    return OpResult.Fail("Échec du passage en DHCP : " + o);
            }
            Netsh("interface ipv4 set dnsservers name=" + Q(name) + " source=dhcp", out o);   // erreur ignorée si déjà en DHCP

            if (!a.IsUp) return OpResult.Success(name + " : DHCP configuré (carte déconnectée, appliqué à la connexion).");

            Run(Sys("ipconfig.exe"), "/renew " + Q(name), out o, 45000);
            for (int i = 0; i < 10; i++)
            {
                var now = Find(name);
                string ip = now == null ? null : now.RawIps.FirstOrDefault(x => !x.StartsWith("169.254."));
                if (ip != null) return OpResult.Success(name + " : DHCP → " + ip + (now.Gateway.Length > 0 ? "  (GW " + now.Gateway + ")" : ""));
                Thread.Sleep(1000);
            }
            return OpResult.Fail(name + " : DHCP activé mais aucun bail reçu (pas de serveur DHCP sur ce réseau ?).");
        }

        public static OpResult SetStatic(string name, string ip, int prefix, string gw, IList<string> dns)
        {
            string err = Validate.CheckStatic(ip, prefix, gw, dns);
            if (err != null) return OpResult.Fail(err);

            var a = Find(name);
            if (a == null) return OpResult.Fail("Carte « " + name + " » introuvable.");

            string o;
            string mask = Validate.PrefixToMask(prefix);
            string gwArg = string.IsNullOrEmpty(gw) ? "none" : gw;
            if (Netsh("interface ipv4 set address name=" + Q(name) + " source=static address=" + ip + " mask=" + mask + " gateway=" + gwArg, out o) != 0)
                return OpResult.Fail("Échec de l'application de l'IP : " + o);

            if (dns.Count == 0)
            {
                Netsh("interface ipv4 set dnsservers name=" + Q(name) + " source=static address=none", out o);
            }
            else
            {
                if (Netsh("interface ipv4 set dnsservers name=" + Q(name) + " source=static address=" + dns[0] + " register=primary validate=no", out o) != 0)
                    return OpResult.Fail("IP appliquée mais échec du DNS : " + o);
                for (int i = 1; i < dns.Count; i++)
                    Netsh("interface ipv4 add dnsservers name=" + Q(name) + " address=" + dns[i] + " index=" + (i + 1) + " validate=no", out o);
            }

            string msg = name + " : " + ip + "/" + prefix + (string.IsNullOrEmpty(gw) ? "" : "  GW " + gw);
            if (!a.IsUp) return OpResult.Success(msg + "\nCarte déconnectée : test impossible.");

            Thread.Sleep(3000);   // laisse Windows faire la détection de doublon (DAD)
            var now = Find(name);
            if (now != null && now.DuplicateIps.Contains(ip))
                return OpResult.Fail("CONFLIT : " + ip + " est déjà utilisée sur ce réseau. Choisis un autre dernier octet.");

            if (!string.IsNullOrEmpty(gw))
            {
                bool reply = false;
                try
                {
                    using (var ping = new Ping())
                        for (int i = 0; i < 3 && !reply; i++)
                            reply = ping.Send(gw, 1000).Status == IPStatus.Success;
                }
                catch { }
                if (!reply) return OpResult.Fail(msg + "\nPasserelle injoignable : mauvaise plage pour ce réseau ?");
                return OpResult.Success(msg + "\nPasserelle joignable.");
            }
            return OpResult.Success(msg);
        }
    }

    // ===================================================================== Démarrage auto (tâche planifiée, sans UAC)
    static class AutoStart
    {
        const string TaskName = "NetSwitch";

        static string Schtasks { get { return Path.Combine(Environment.SystemDirectory, "schtasks.exe"); } }

        public static bool IsEnabled()
        {
            string o;
            try { return NetOps.Run(Schtasks, "/query /tn \"" + TaskName + "\"", out o, 10000) == 0; } catch { return false; }
        }

        public static bool Enable()
        {
            string exe = Application.ExecutablePath;
            // tâche élevée sans invite : uniquement pour l'exe installé (un exe dans un dossier modifiable pourrait être remplacé)
            if (!AlexBHarden.Harden.InProtectedDir(exe)) { Logger.Write("AutoStart refusé : exe hors de Program Files (" + exe + ")"); return false; }
            // Groupe Utilisateurs + niveau le plus élevé : se lance à l'ouverture de session de chaque utilisateur,
            // élevé sans invite UAC pour les administrateurs. Pas de limite de durée, fonctionne sur batterie.
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Author>NetSwitch</Author><Description>Lance NetSwitch à l'ouverture de session.</Description></RegistrationInfo>\r\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled><Delay>PT5S</Delay></LogonTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><GroupId>S-1-5-32-545</GroupId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
"    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
"    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"    <Hidden>false</Hidden>\r\n" +
"    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
"    <WakeToRun>false</WakeToRun>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Priority>7</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>\"" + SecurityElement.Escape(exe) + "\"</Command></Exec></Actions>\r\n" +
"</Task>\r\n";

            string tdir = AlexBHarden.Harden.SecureTempDir("netswitch"); string tmp = Path.Combine(tdir, "netswitch-task.xml");
            try
            {
                File.WriteAllText(tmp, xml, Encoding.Unicode);   // schtasks attend de l'UTF-16
                string o;
                int rc = NetOps.Run(Schtasks, "/create /tn \"" + TaskName + "\" /xml \"" + tmp + "\" /f", out o, 15000);
                Logger.Write("AutoStart ON => " + rc + " " + o);
                return rc == 0;
            }
            catch (Exception ex) { Logger.Write("AutoStart ON erreur : " + ex.Message); return false; }
            finally { try { Directory.Delete(tdir, true); } catch { } }
        }

        public static void Disable()
        {
            string o;
            try
            {
                int rc = NetOps.Run(Schtasks, "/delete /tn \"" + TaskName + "\" /f", out o, 10000);
                Logger.Write("AutoStart OFF => " + rc + " " + o);
            }
            catch { }
        }
    }

}
