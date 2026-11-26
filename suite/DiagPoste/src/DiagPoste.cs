// DiagPoste - diagnostic complet d'un poste client, rapport pour le ticket
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

[assembly: AssemblyTitle("DiagPoste")]
[assembly: AssemblyDescription("Diagnostic de poste client")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("DiagPoste")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

namespace DiagPoste
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            AppInfo.Name = "DiagPoste"; AppInfo.Id = "DiagPoste"; AppInfo.Version = "1.0.1"; AppInfo.MachineWide = true;
            Boot.Run(() => new MainForm(), true, "lecture SMART des disques, BitLocker, TPM et journaux système.");
        }
    }

    enum Lvl { Info, Ok, Warn, Bad }

    class Check
    {
        public string Section, Label, Value;
        public Lvl Level;
    }

    // ===================================================================== Collecte
    class Collector
    {
        public readonly List<Check> Checks = new List<Check>();
        string section = "";
        readonly Action<string> progress;

        public Collector(Action<string> p) { progress = p; }

        void Sec(string s) { section = s; progress(s); }
        void Add(string label, string value, Lvl lvl = Lvl.Info) { Checks.Add(new Check { Section = section, Label = label, Value = value ?? "—", Level = lvl }); }

        static IEnumerable<ManagementObject> Q(string scope, string query)
        {
            var list = new List<ManagementObject>();
            try { using (var s = new ManagementObjectSearcher(scope, query)) foreach (ManagementObject o in s.Get()) list.Add(o); }
            catch (Exception ex) { Log.Write("WMI " + scope + " " + query + " : " + ex.Message); }
            return list;
        }
        static ManagementObject Q1(string scope, string query) { return Q(scope, query).FirstOrDefault(); }
        static string S(ManagementObject o, string p) { try { return o == null ? null : Convert.ToString(o[p]).Trim(); } catch { return null; } }
        static DateTime? D(ManagementObject o, string p) { try { var s = S(o, p); return string.IsNullOrEmpty(s) ? (DateTime?)null : ManagementDateTimeConverter.ToDateTime(s); } catch { return null; } }

        static object Reg(string path, string name)
        {
            try { using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) using (var k = b.OpenSubKey(path)) return k == null ? null : k.GetValue(name); }
            catch { return null; }
        }
        static bool RegKeyExists(string path)
        {
            try { using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) using (var k = b.OpenSubKey(path)) return k != null; }
            catch { return false; }
        }

        void Safe(Action a, string what)
        {
            try { a(); } catch (Exception ex) { Add(what, "lecture impossible : " + ex.Message, Lvl.Warn); Log.Write(what + " : " + ex); }
        }

        public void Run()
        {
            Safe(Systeme, "Système");
            Safe(Windows, "Windows");
            Safe(Materiel, "Matériel");
            Safe(Disques, "Disques");
            Safe(Batterie, "Batterie");
            Safe(Securite, "Sécurité");
            Safe(MisesAJour, "Mises à jour");
            Safe(Stabilite, "Stabilité");
            Safe(Reseau, "Réseau");
        }

        // ------------------------------------------------------------------
        void Systeme()
        {
            Sec("SYSTÈME");
            var cs = Q1(@"root\CIMV2", "SELECT Manufacturer, Model, TotalPhysicalMemory, Domain, PartOfDomain FROM Win32_ComputerSystem");
            var bios = Q1(@"root\CIMV2", "SELECT SerialNumber, SMBIOSBIOSVersion, ReleaseDate, Manufacturer FROM Win32_BIOS");
            Add("Nom du PC", Environment.MachineName);
            Add("Utilisateur", Environment.UserDomainName + "\\" + Environment.UserName);
            Add("Fabricant / modèle", S(cs, "Manufacturer") + " " + S(cs, "Model"));
            Add("N° de série", S(bios, "SerialNumber"));
            bool dom = false; try { dom = Convert.ToBoolean(cs["PartOfDomain"]); } catch { }
            Add(dom ? "Domaine" : "Groupe de travail", S(cs, "Domain"));
            var rel = D(bios, "ReleaseDate");
            var age = rel.HasValue ? (DateTime.Now - rel.Value).TotalDays / 365.25 : 0;
            Add("BIOS", S(bios, "SMBIOSBIOSVersion") + (rel.HasValue ? "  (" + rel.Value.ToString("dd/MM/yyyy") + ")" : ""),
                age > 4 ? Lvl.Warn : Lvl.Info);
        }

        void Windows()
        {
            Sec("WINDOWS");
            var os = Q1(@"root\CIMV2", "SELECT Caption, Version, BuildNumber, InstallDate, LastBootUpTime, OSArchitecture FROM Win32_OperatingSystem");
            string caption = S(os, "Caption") ?? "?";
            string disp = Convert.ToString(Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion") ?? "");
            string ubr = Convert.ToString(Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR") ?? "");
            int build; int.TryParse(S(os, "BuildNumber"), out build);
            bool win11 = build >= 22000;
            if (win11) caption = caption.Replace("Windows 10", "Windows 11");
            Add("Version", caption + "  " + disp + "  (build " + build + (ubr.Length > 0 ? "." + ubr : "") + ", " + S(os, "OSArchitecture") + ")");

            bool ent = caption.IndexOf("Enterprise", StringComparison.OrdinalIgnoreCase) >= 0 || caption.IndexOf("Entreprise", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       caption.IndexOf("Education", StringComparison.OrdinalIgnoreCase) >= 0 || caption.IndexOf("Éducation", StringComparison.OrdinalIgnoreCase) >= 0;
            DateTime? eos = null;
            if (!win11) eos = new DateTime(2025, 10, 14);   // Windows 10 (hors ESU / LTSC)
            else
            {
                // dates Microsoft (Modern Lifecycle) - Home/Pro et Enterprise/Education
                var home = new Dictionary<string, DateTime> { { "21H2", new DateTime(2023, 10, 10) }, { "22H2", new DateTime(2024, 10, 8) }, { "23H2", new DateTime(2025, 11, 11) }, { "24H2", new DateTime(2026, 10, 13) }, { "25H2", new DateTime(2027, 10, 12) }, { "26H1", new DateTime(2028, 3, 14) }, { "26H2", new DateTime(2028, 10, 10) } };
                var entd = new Dictionary<string, DateTime> { { "21H2", new DateTime(2024, 10, 8) }, { "22H2", new DateTime(2025, 10, 14) }, { "23H2", new DateTime(2026, 11, 10) }, { "24H2", new DateTime(2027, 10, 12) }, { "25H2", new DateTime(2028, 10, 10) } };
                DateTime d;
                if ((ent ? entd : home).TryGetValue(disp.ToUpperInvariant(), out d)) eos = d;
            }
            if (caption.IndexOf("LTSC", StringComparison.OrdinalIgnoreCase) >= 0) eos = null;
            if (eos.HasValue)
            {
                var days = (eos.Value - DateTime.Today).TotalDays;
                if (days < 0) Add("Support Microsoft", "TERMINÉ le " + eos.Value.ToString("dd/MM/yyyy") + " : plus de mises à jour de sécurité" + (win11 ? " → passer à une version plus récente" : " → passer à Windows 11 (ou ESU)"), Lvl.Bad);
                else Add("Support Microsoft", "jusqu'au " + eos.Value.ToString("dd/MM/yyyy") + (days < 120 ? "  (moins de 4 mois)" : ""), days < 120 ? Lvl.Warn : Lvl.Ok);
            }
            else Add("Support Microsoft", "date non déterminée pour cette édition/version — à vérifier", Lvl.Info);

            var inst = D(os, "InstallDate");
            if (inst.HasValue) Add("Installé le", inst.Value.ToString("dd/MM/yyyy"));
            var boot = D(os, "LastBootUpTime");
            if (boot.HasValue)
            {
                var up = DateTime.Now - boot.Value;
                Add("Allumé depuis", (up.TotalDays >= 1 ? (int)up.TotalDays + " j " : "") + up.Hours + " h  (démarrage " + boot.Value.ToString("dd/MM HH:mm") + ")",
                    up.TotalDays > 14 ? Lvl.Warn : Lvl.Info);
            }

            var lic = Q1(@"root\CIMV2", "SELECT LicenseStatus, Name FROM SoftwareLicensingProduct WHERE ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL AND LicenseStatus=1");
            if (lic != null) Add("Activation", "Windows activé", Lvl.Ok);
            else Add("Activation", "NON ACTIVÉ (ou en période de grâce)", Lvl.Bad);
        }

        void Materiel()
        {
            Sec("MATÉRIEL");
            var cpu = Q1(@"root\CIMV2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            Add("Processeur", S(cpu, "Name") + "  (" + S(cpu, "NumberOfCores") + " cœurs / " + S(cpu, "NumberOfLogicalProcessors") + " threads)");
            var cs = Q1(@"root\CIMV2", "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            double ram = 0; try { ram = Convert.ToDouble(cs["TotalPhysicalMemory"]); } catch { }
            var mods = Q(@"root\CIMV2", "SELECT Capacity, Speed, Manufacturer FROM Win32_PhysicalMemory").ToList();
            string detail = string.Join(" + ", mods.Select(m => Fmt.Size(Convert.ToDouble(m["Capacity"])).Replace(",0", "") + (S(m, "Speed").Length > 0 ? " " + S(m, "Speed") + " MHz" : "")));
            double gb = ram / 1024 / 1024 / 1024;
            Add("Mémoire vive", Fmt.Size(ram) + "  (" + mods.Count + " barrette(s) : " + detail + ")", gb < 7.5 ? Lvl.Warn : Lvl.Ok);
            var gpu = Q(@"root\CIMV2", "SELECT Name FROM Win32_VideoController").Select(g => S(g, "Name"));
            Add("Carte graphique", string.Join(" / ", gpu));
        }

        void Disques()
        {
            Sec("DISQUES");
            var pd = Q(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, MediaType, BusType, HealthStatus, Size FROM MSFT_PhysicalDisk").ToList();
            foreach (var d in pd)
            {
                int media = 0, bus = 0, health = 0;
                try { media = Convert.ToInt32(d["MediaType"]); bus = Convert.ToInt32(d["BusType"]); health = Convert.ToInt32(d["HealthStatus"]); } catch { }
                string type = bus == 17 ? "SSD NVMe" : media == 4 ? "SSD" : media == 3 ? "HDD (mécanique)" : "type inconnu";
                string hs = health == 0 ? "sain" : health == 1 ? "AVERTISSEMENT" : health == 2 ? "DÉFAILLANT" : "inconnu";
                Add(S(d, "FriendlyName"), type + "  " + Fmt.Size(Convert.ToDouble(d["Size"])) + "  —  état : " + hs,
                    health == 2 ? Lvl.Bad : health == 1 ? Lvl.Warn : media == 3 ? Lvl.Warn : Lvl.Ok);
            }
            if (pd.Any(d => { try { return Convert.ToInt32(d["MediaType"]) == 3; } catch { return false; } }))
                Add("Conseil", "disque mécanique détecté : un SSD rendrait le poste nettement plus rapide", Lvl.Warn);

            var smart = Q(@"root\WMI", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus").ToList();
            if (smart.Any(s => { try { return Convert.ToBoolean(s["PredictFailure"]); } catch { return false; } }))
                Add("SMART", "PANNE PRÉDITE sur au moins un disque : sauvegarder immédiatement", Lvl.Bad);
            else if (smart.Count > 0) Add("SMART", "aucune panne prédite", Lvl.Ok);

            foreach (var v in Q(@"root\CIMV2", "SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3"))
            {
                double size = 0, free = 0;
                try { size = Convert.ToDouble(v["Size"]); free = Convert.ToDouble(v["FreeSpace"]); } catch { }
                if (size <= 0) continue;
                double pct = free / size * 100;
                Add("Volume " + S(v, "DeviceID") + " " + S(v, "VolumeName"), Fmt.Size(free) + " libres sur " + Fmt.Size(size) + "  (" + pct.ToString("0") + " %)",
                    pct < 10 ? Lvl.Bad : pct < 20 ? Lvl.Warn : Lvl.Ok);
            }
        }

        void Batterie()
        {
            var bat = Q1(@"root\CIMV2", "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            if (bat == null) return;
            Sec("BATTERIE");
            Add("Charge", S(bat, "EstimatedChargeRemaining") + " %");
            var design = Q1(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData");
            var full = Q1(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            try
            {
                double d = Convert.ToDouble(design["DesignedCapacity"]), f = Convert.ToDouble(full["FullChargedCapacity"]);
                if (d > 0)
                {
                    double h = Math.Min(100, f / d * 100);
                    Add("Santé (capacité restante)", h.ToString("0") + " %  (" + f.ToString("0") + " / " + d.ToString("0") + " mWh)", h < 60 ? Lvl.Bad : h < 80 ? Lvl.Warn : Lvl.Ok);
                }
            }
            catch { Add("Santé", "non disponible sur ce modèle"); }
        }

        void Securite()
        {
            Sec("SÉCURITÉ");
            var avs = Q(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct").ToList();
            if (avs.Count == 0) Add("Antivirus", "AUCUN détecté", Lvl.Bad);
            foreach (var av in avs)
            {
                int st = 0; try { st = Convert.ToInt32(av["productState"]); } catch { }
                int run = (st >> 8) & 0xFF, sig = st & 0xFF;
                bool on = run == 0x10 || run == 0x11, upToDate = sig == 0x00;
                Add("Antivirus", S(av, "displayName") + "  —  " + (on ? "actif" : "inactif") + ", " + (upToDate ? "à jour" : "SIGNATURES PÉRIMÉES"),
                    !on && avs.Count == 1 ? Lvl.Bad : !on ? Lvl.Info : upToDate ? Lvl.Ok : Lvl.Warn);
            }

            var off = new List<string>();
            foreach (var p in new[] { new[] { "DomainProfile", "Domaine" }, new[] { "StandardProfile", "Privé" }, new[] { "PublicProfile", "Public" } })
            {
                var v = Reg(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\" + p[0], "EnableFirewall");
                if (v != null && Convert.ToInt32(v) == 0) off.Add(p[1]);
            }
            Add("Pare-feu Windows", off.Count == 0 ? "actif sur tous les profils" : "DÉSACTIVÉ : " + string.Join(", ", off) + " (normal si un pare-feu tiers le remplace)", off.Count == 0 ? Lvl.Ok : Lvl.Warn);

            var bl = Q1(@"root\CIMV2\Security\MicrosoftVolumeEncryption", "SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='" + Proc.SysDrive.TrimEnd('\\') + "'");
            if (bl != null) { int ps = 0; try { ps = Convert.ToInt32(bl["ProtectionStatus"]); } catch { } Add("BitLocker (système)", ps == 1 ? "chiffré et protégé — noter la clé de récupération" : "non protégé", ps == 1 ? Lvl.Ok : Lvl.Info); }

            var sb = Reg(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
            Add("Secure Boot", sb == null ? "non pris en charge (BIOS hérité ?)" : Convert.ToInt32(sb) == 1 ? "activé" : "désactivé", sb != null && Convert.ToInt32(sb) == 1 ? Lvl.Ok : Lvl.Warn);

            var tpm = Q1(@"root\CIMV2\Security\MicrosoftTpm", "SELECT IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm");
            if (tpm == null) Add("TPM", "absent ou désactivé dans le BIOS", Lvl.Warn);
            else Add("TPM", "présent, version " + (S(tpm, "SpecVersion") ?? "").Split(',')[0].Trim(), Lvl.Ok);
        }

        void MisesAJour()
        {
            Sec("MISES À JOUR");
            var hf = Q(@"root\CIMV2", "SELECT HotFixID, InstalledOn, Description FROM Win32_QuickFixEngineering").ToList();
            var dated = new List<Tuple<DateTime, string>>();
            foreach (var h in hf)
            {
                DateTime d;
                var s = S(h, "InstalledOn");
                if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out d) || DateTime.TryParse(s, out d))
                    dated.Add(Tuple.Create(d, S(h, "HotFixID")));
            }
            if (dated.Count > 0)
            {
                var last = dated.Max(x => x.Item1);
                var days = (DateTime.Today - last).TotalDays;
                Add("Dernière mise à jour", last.ToString("dd/MM/yyyy") + "  (il y a " + (int)days + " jours)", days > 90 ? Lvl.Bad : days > 45 ? Lvl.Warn : Lvl.Ok);
                Add("5 dernières", string.Join(", ", dated.OrderByDescending(x => x.Item1).Take(5).Select(x => x.Item2 + " (" + x.Item1.ToString("dd/MM") + ")")));
            }
            else Add("Dernière mise à jour", "date inconnue", Lvl.Warn);
            bool pending = RegKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") ||
                           RegKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            Add("Redémarrage en attente", pending ? "OUI (mises à jour à finaliser)" : "non", pending ? Lvl.Warn : Lvl.Ok);
        }

        int CountEvents(string xpath)
        {
            try
            {
                var q = new EventLogQuery("System", PathType.LogName, xpath);
                int n = 0;
                using (var r = new EventLogReader(q)) { EventRecord e; while ((e = r.ReadEvent()) != null) { n++; e.Dispose(); if (n > 999) break; } }
                return n;
            }
            catch (Exception ex) { Log.Write("EventLog : " + ex.Message); return -1; }
        }

        void Stabilite()
        {
            Sec("STABILITÉ (7 DERNIERS JOURS)");
            const string T = "TimeCreated[timediff(@SystemTime) <= 604800000]";
            int kp = CountEvents("*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and " + T + "]]");
            int bsod = CountEvents("*[System[(EventID=1001) and Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and " + T + "]]");
            int disk = CountEvents("*[System[Provider[@Name='disk' or @Name='Disk' or @Name='Ntfs' or @Name='stornvme' or @Name='storahci'] and (Level=1 or Level=2 or Level=3) and " + T + "]]");
            int errs = CountEvents("*[System[(Level=1 or Level=2) and " + T + "]]");
            Add("Arrêts brutaux (Kernel-Power 41)", kp < 0 ? "?" : kp.ToString(), kp > 0 ? Lvl.Warn : Lvl.Ok);
            Add("Écrans bleus (BugCheck)", bsod < 0 ? "?" : bsod.ToString(), bsod > 0 ? Lvl.Bad : Lvl.Ok);
            Add("Erreurs disque / NTFS", disk < 0 ? "?" : disk.ToString(), disk > 0 ? Lvl.Bad : Lvl.Ok);
            Add("Erreurs système (total)", errs < 0 ? "?" : (errs > 999 ? "999+" : errs.ToString()), errs > 50 ? Lvl.Warn : Lvl.Info);
            int startup = Q(@"root\CIMV2", "SELECT Name FROM Win32_StartupCommand").Count();
            Add("Programmes au démarrage", startup.ToString(), startup > 15 ? Lvl.Warn : Lvl.Info);
        }

        void Reseau()
        {
            Sec("RÉSEAU");
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                     (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)))
            {
                if ((ni.Description ?? "").ToLowerInvariant().Contains("virtual") || (ni.Description ?? "").ToLowerInvariant().Contains("vmware")) continue;
                var p = ni.GetIPProperties();
                var ip = p.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => u.Address.ToString()).FirstOrDefault();
                var gw = p.GatewayAddresses.Select(g => g.Address).FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork);
                bool dhcp = false; try { dhcp = p.GetIPv4Properties().IsDhcpEnabled; } catch { }
                Add(ni.Name, (ip ?? "pas d'IP") + (dhcp ? " (DHCP)" : " (fixe)") + "  GW " + (gw != null ? gw.ToString() : "—") +
                    "  DNS " + string.Join(", ", p.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString())),
                    ip == null || ip.StartsWith("169.254.") ? Lvl.Bad : Lvl.Info);
            }
            bool net = false;
            try { using (var ping = new Ping()) net = ping.Send("1.1.1.1", 1500).Status == IPStatus.Success; } catch { }
            Add("Accès Internet (ping 1.1.1.1)", net ? "OK" : "ÉCHEC", net ? Lvl.Ok : Lvl.Bad);
        }
    }

    // ===================================================================== Rapports
    static class Report
    {
        static string Mark(Lvl l) { return l == Lvl.Bad ? "[!!]" : l == Lvl.Warn ? "[! ]" : l == Lvl.Ok ? "[OK]" : "[  ]"; }

        public static string Text(List<Check> checks)
        {
            var sb = new StringBuilder();
            sb.AppendLine("DIAGNOSTIC POSTE — " + Environment.MachineName + " — " + DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
            sb.AppendLine("Réalisé avec DiagPoste " + AppInfo.Version + " (Développé by alex.b)");
            int bad = checks.Count(c => c.Level == Lvl.Bad), warn = checks.Count(c => c.Level == Lvl.Warn);
            sb.AppendLine("Bilan : " + bad + " problème(s), " + warn + " point(s) d'attention");
            foreach (var g in checks.GroupBy(c => c.Section))
            {
                sb.AppendLine(); sb.AppendLine("== " + g.Key + " ==");
                foreach (var c in g) sb.AppendLine(Mark(c.Level) + " " + c.Label + " : " + c.Value);
            }
            return sb.ToString();
        }

        public static string Html(List<Check> checks)
        {
            int bad = checks.Count(c => c.Level == Lvl.Bad), warn = checks.Count(c => c.Level == Lvl.Warn);
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang='fr'><head><meta charset='utf-8'><title>Diagnostic " + Fmt.Html(Environment.MachineName) + "</title><style>");
            sb.Append("body{font-family:Segoe UI,Arial,sans-serif;background:#070a11;color:#dcecf6;margin:0;padding:32px}");
            sb.Append("h1{font-family:Consolas,monospace;color:#00e5ff;margin:0}.sub{color:#6980a0;font-family:Consolas,monospace;margin:6px 0 24px}");
            sb.Append(".line{height:2px;background:linear-gradient(90deg,#00e5ff,#ff2bd6);margin-bottom:24px}");
            sb.Append(".sum{display:flex;gap:12px;margin-bottom:24px}.pill{padding:10px 16px;border-radius:10px;background:#121b2a;border:1px solid #203046}");
            sb.Append("h2{font-family:Consolas,monospace;color:#00e5ff;font-size:15px;margin:26px 0 8px}");
            sb.Append("table{width:100%;border-collapse:collapse;background:#0d131e;border:1px solid #203046;border-radius:10px;overflow:hidden}");
            sb.Append("td{padding:7px 12px;border-bottom:1px solid #182436;vertical-align:top}td.k{color:#8aa0b8;width:32%}");
            sb.Append(".ok{color:#39ff8c}.warn{color:#ffb000}.bad{color:#ff3860;font-weight:600}.dot{display:inline-block;width:9px;height:9px;border-radius:50%;margin-right:8px}");
            sb.Append(".foot{margin-top:30px;color:#6980a0;font-family:Consolas,monospace;font-size:12px}");
            sb.Append("@media print{body{background:#fff;color:#000}table{background:#fff}td.k{color:#444}}</style></head><body>");
            sb.Append("<h1>&gt; DIAGNOSTIC_POSTE_</h1><div class='sub'>" + Fmt.Html(Environment.MachineName) + " · " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " · by alex.b</div><div class='line'></div>");
            sb.Append("<div class='sum'><div class='pill bad'>" + bad + " problème(s)</div><div class='pill warn'>" + warn + " point(s) d'attention</div><div class='pill ok'>" + checks.Count(c => c.Level == Lvl.Ok) + " OK</div></div>");
            foreach (var g in checks.GroupBy(c => c.Section))
            {
                sb.Append("<h2>// " + Fmt.Html(g.Key) + "</h2><table>");
                foreach (var c in g)
                {
                    string cls = c.Level == Lvl.Bad ? "bad" : c.Level == Lvl.Warn ? "warn" : c.Level == Lvl.Ok ? "ok" : "";
                    string col = c.Level == Lvl.Bad ? "#ff3860" : c.Level == Lvl.Warn ? "#ffb000" : c.Level == Lvl.Ok ? "#39ff8c" : "#6980a0";
                    sb.Append("<tr><td class='k'><span class='dot' style='background:" + col + "'></span>" + Fmt.Html(c.Label) + "</td><td class='" + cls + "'>" + Fmt.Html(c.Value) + "</td></tr>");
                }
                sb.Append("</table>");
            }
            sb.Append("<div class='foot'>Rapport généré par DiagPoste " + AppInfo.Version + " — Développé by alex.b</div></body></html>");
            return sb.ToString();
        }
    }

    // ===================================================================== Fenêtre
    class MainForm : CyberForm
    {
        readonly Chip bRun, bCopy, bHtml, bTxt;
        readonly Card verdict;
        readonly Label vTitle, vSub;
        readonly LogBox box;
        List<Check> checks;
        bool busy;

        public MainForm() : base("DIAG_POSTE", 900, 780)
        {
            SetSubtitle("Diagnostic complet du poste  //  " + Environment.MachineName + "  //  by alex.b");
            Func<float, int> k = Theme.S;

            bRun = new Chip("↻ Relancer l'analyse") { AccentOf = t => t.Accent, Location = new Point(k(16), k(12)) };
            bCopy = new Chip("Copier pour le ticket") { Primary = true, AccentOf = t => t.Ok };
            bHtml = new Chip("Exporter HTML") { AccentOf = t => t.Accent2 };
            bTxt = new Chip("Exporter TXT") { AccentOf = t => t.Muted };
            bCopy.Location = new Point(bRun.Right + k(8), k(12));
            bHtml.Location = new Point(bCopy.Right + k(8), k(12));
            bTxt.Location = new Point(bHtml.Right + k(8), k(12));
            bRun.Click += (s, e) => Analyse();
            bCopy.Click += (s, e) => { if (checks == null) return; Clipboard.SetText(Report.Text(checks)); bCopy.SetText("✓ Copié"); };
            bHtml.Click += (s, e) => Export(true);
            bTxt.Click += (s, e) => Export(false);
            Body.Controls.AddRange(new Control[] { bRun, bCopy, bHtml, bTxt });

            verdict = new Card { Location = new Point(k(16), k(54)), Size = new Size(Body.Width - k(32), k(70)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, StatusOf = t => t.Muted };
            vTitle = new Label { Text = "ANALYSE EN COURS…", AutoSize = true, Font = new Font("Consolas", 14f, FontStyle.Bold), Location = new Point(k(18), k(10)), Tag = "muted" };
            vSub = new Label { Text = "", AutoSize = true, Location = new Point(k(20), k(40)), Tag = "muted" };
            verdict.Controls.Add(vTitle); verdict.Controls.Add(vSub);
            Body.Controls.Add(verdict);

            box = new LogBox { Location = new Point(k(16), k(136)), Size = new Size(Body.Width - k(32), Body.Height - k(148)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Body.Controls.Add(box);
            Shown += (s, e) => Analyse();
        }

        void Analyse()
        {
            if (busy) return;
            busy = true;
            foreach (var c in new[] { bRun, bCopy, bHtml, bTxt }) c.Enabled = false;
            bCopy.SetText("Copier pour le ticket");
            box.Clear();
            box.Line("Analyse de " + Environment.MachineName + "… (10 à 30 secondes)", "muted");
            vTitle.Text = "ANALYSE EN COURS…"; vTitle.Tag = "muted"; verdict.StatusOf = t => t.Muted; Recolor(verdict); verdict.Invalidate();
            Task.Run(() =>
            {
                var col = new Collector(step => BeginInvoke(new Action(() => vSub.Text = "lecture : " + step.ToLowerInvariant())));
                col.Run();
                BeginInvoke(new Action(() => Display(col.Checks)));
            });
        }

        void Display(List<Check> list)
        {
            checks = list; busy = false;
            foreach (var c in new[] { bRun, bCopy, bHtml, bTxt }) c.Enabled = true;
            box.Clear();
            var t = Theme.Current;
            foreach (var g in list.GroupBy(c => c.Section))
            {
                box.Line("// " + g.Key, t.Accent, true);
                foreach (var c in g)
                {
                    Color col = c.Level == Lvl.Bad ? t.Bad : c.Level == Lvl.Warn ? t.Warn : c.Level == Lvl.Ok ? t.Ok : t.Text;
                    string mark = c.Level == Lvl.Bad ? "✗" : c.Level == Lvl.Warn ? "!" : c.Level == Lvl.Ok ? "✓" : "·";
                    box.Line("  " + mark + " " + (c.Label + " ").PadRight(34, '·') + " " + c.Value, col);
                }
                box.Line("", t.Text);
            }
            box.SelectionStart = 0; box.ScrollToCaret();
            int bad = list.Count(c => c.Level == Lvl.Bad), warn = list.Count(c => c.Level == Lvl.Warn);
            if (bad > 0) { vTitle.Text = bad + " PROBLÈME(S) À TRAITER"; vTitle.Tag = "bad"; verdict.StatusOf = x => x.Bad; }
            else if (warn > 0) { vTitle.Text = "POSTE CORRECT — " + warn + " POINT(S) D'ATTENTION"; vTitle.Tag = "warn"; verdict.StatusOf = x => x.Warn; }
            else { vTitle.Text = "POSTE EN BON ÉTAT"; vTitle.Tag = "ok"; verdict.StatusOf = x => x.Ok; }
            vSub.Text = bad + " problème(s) · " + warn + " attention · " + list.Count(c => c.Level == Lvl.Ok) + " OK  —  « Copier pour le ticket » colle le rapport dans Note Station";
            Recolor(verdict); verdict.Invalidate();
            Log.Write("Diagnostic : " + bad + " bad, " + warn + " warn");
        }

        void Export(bool html)
        {
            if (checks == null) return;
            using (var d = new SaveFileDialog
            {
                FileName = "Diag_" + Environment.MachineName + "_" + DateTime.Now.ToString("yyyyMMdd-HHmm") + (html ? ".html" : ".txt"),
                Filter = html ? "Page web (*.html)|*.html" : "Texte (*.txt)|*.txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(d.FileName, html ? Report.Html(checks) : Report.Text(checks), new UTF8Encoding(true));
                try { Process.Start(d.FileName); } catch { }
            }
        }
    }
}
