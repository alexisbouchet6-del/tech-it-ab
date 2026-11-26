// AMI·OPS - PanneScan : historique des pannes et verdict (BSOD, arrêts brutaux, disque, RAM, pilotes, chauffe, batterie)
// Développé by alex.b

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

namespace AmiOps
{
    class Finding { public string Cat = "", Level = "info", Title = "", Detail = "", Action = ""; }
    class Ev { public DateTime At; public string Level = "", Source = "", Text = ""; }

    class PanneReport
    {
        public List<Finding> F = new List<Finding>();
        public List<Ev> T = new List<Ev>();
        public string Verdict = "", Color = "ok", Advice = "";
        public int Days;
        public string Text()
        {
            var sb = new StringBuilder();
            sb.AppendLine("PanneScan " + Environment.MachineName + " (" + Days + " derniers jours) : " + Verdict);
            if (Advice.Length > 0) sb.AppendLine("→ " + Advice);
            foreach (var f in F.Where(x => x.Level == "bad" || x.Level == "warn"))
                sb.AppendLine("- [" + (f.Level == "bad" ? "CRITIQUE" : "à surveiller") + "] " + f.Cat + " : " + f.Title + (f.Detail.Length > 0 ? " (" + f.Detail + ")" : ""));
            return sb.ToString().TrimEnd();
        }
    }

    static class PanneScan
    {
        // code d'arrêt → nom, catégorie, explication
        static readonly Dictionary<uint, string[]> Bug = new Dictionary<uint, string[]>
        {
            { 0x0A, new[] { "IRQL_NOT_LESS_OR_EQUAL", "Pilote", "un pilote a accédé à une mémoire interdite" } },
            { 0xD1, new[] { "DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Pilote", "pilote défaillant (souvent réseau / Wi-Fi)" } },
            { 0x1E, new[] { "KMODE_EXCEPTION_NOT_HANDLED", "Pilote", "exception non gérée dans un pilote" } },
            { 0x3B, new[] { "SYSTEM_SERVICE_EXCEPTION", "Pilote", "pilote ou antivirus en cause" } },
            { 0x7E, new[] { "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "Pilote", "pilote récent ou incompatible" } },
            { 0x9F, new[] { "DRIVER_POWER_STATE_FAILURE", "Pilote", "pilote bloquant la veille / reprise" } },
            { 0x133, new[] { "DPC_WATCHDOG_VIOLATION", "Pilote", "pilote de stockage ou firmware SSD" } },
            { 0x139, new[] { "KERNEL_SECURITY_CHECK_FAILURE", "Pilote", "corruption détectée (pilote ou RAM)" } },
            { 0xC2, new[] { "BAD_POOL_CALLER", "Pilote", "pilote qui corrompt la mémoire" } },
            { 0x19, new[] { "BAD_POOL_HEADER", "Pilote", "pilote qui corrompt la mémoire" } },
            { 0x50, new[] { "PAGE_FAULT_IN_NONPAGED_AREA", "Mémoire / processeur", "RAM défaillante ou pilote" } },
            { 0x1A, new[] { "MEMORY_MANAGEMENT", "Mémoire / processeur", "RAM défaillante le plus souvent" } },
            { 0x124, new[] { "WHEA_UNCORRECTABLE_ERROR", "Mémoire / processeur", "erreur matérielle (processeur, RAM, chauffe, overclock)" } },
            { 0x101, new[] { "CLOCK_WATCHDOG_TIMEOUT", "Mémoire / processeur", "processeur qui ne répond plus" } },
            { 0x9C, new[] { "MACHINE_CHECK_EXCEPTION", "Mémoire / processeur", "erreur matérielle processeur" } },
            { 0x7A, new[] { "KERNEL_DATA_INPAGE_ERROR", "Disque", "lecture disque impossible (disque ou câble)" } },
            { 0x77, new[] { "KERNEL_STACK_INPAGE_ERROR", "Disque", "lecture disque impossible" } },
            { 0xF4, new[] { "CRITICAL_OBJECT_TERMINATION", "Disque", "souvent disque / SSD défaillant" } },
            { 0x154, new[] { "UNEXPECTED_STORE_EXCEPTION", "Disque", "SSD / disque défaillant" } },
            { 0x7B, new[] { "INACCESSIBLE_BOOT_DEVICE", "Disque", "disque de démarrage inaccessible (disque, mode SATA, pilote)" } },
            { 0x24, new[] { "NTFS_FILE_SYSTEM", "Disque", "système de fichiers corrompu" } },
            { 0x116, new[] { "VIDEO_TDR_FAILURE", "Carte graphique", "le pilote graphique ne répond plus" } },
            { 0x119, new[] { "VIDEO_SCHEDULER_INTERNAL_ERROR", "Carte graphique", "pilote / carte graphique" } },
            { 0x10E, new[] { "VIDEO_MEMORY_MANAGEMENT_INTERNAL", "Carte graphique", "pilote / mémoire vidéo" } },
            { 0xEF, new[] { "CRITICAL_PROCESS_DIED", "Windows", "processus système arrêté (fichiers système, disque)" } },
            { 0xA0, new[] { "INTERNAL_POWER_ERROR", "Alimentation", "gestion de l'alimentation / veille prolongée" } },
        };

        public static readonly Dictionary<string, string> Fix = new Dictionary<string, string>
        {
            { "Disque", "Sauvegarder les données tout de suite, puis remplacer le disque (cloner vers un SSD neuf)." },
            { "Mémoire / processeur", "Test mémoire (mdsched ou MemTest86 depuis la clé), retirer l'overclock / XMP, dépoussiérer, vérifier la ventilation." },
            { "Pilote", "Mettre à jour ou revenir à la version précédente du pilote récent (réseau, graphique, stockage) ; vérifier l'antivirus ; MaintenancePC > pilotes." },
            { "Carte graphique", "Réinstaller proprement le pilote graphique (DDU en mode sans échec), vérifier la chauffe et les câbles écran." },
            { "Alimentation", "Contrôler secteur / multiprise / onduleur, bloc d'alimentation, batterie, bouton marche ; désactiver le démarrage rapide." },
            { "Surchauffe", "Dépoussiérer, changer la pâte thermique, vérifier les ventilateurs ; sur portable, surface dure." },
            { "Windows", "DISM /RestoreHealth puis SFC /scannow ; réparer ou réinstaller Windows en conservant les données si ça persiste." },
            { "Batterie", "Remplacer la batterie (capacité réduite)." },
        };

        public static string BugName(uint code) { string[] b; return Bug.TryGetValue(code, out b) ? b[0] : "0x" + code.ToString("X"); }

        static string Desc(EventRecord r)
        {
            try { var d = r.FormatDescription(); if (!string.IsNullOrEmpty(d)) return Regex.Replace(d, @"\s+", " ").Trim(); } catch { }
            try { return string.Join(" | ", r.Properties.Select(p => Convert.ToString(p.Value)).Where(s => s.Length > 0).Take(4)); } catch { return ""; }
        }

        static uint ParseCode(string s)
        {
            var m = Regex.Match(s ?? "", @"0x([0-9a-fA-F]{1,8})\b");
            uint v; return m.Success && uint.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out v) ? v : 0;
        }

        static List<EventRecord> Read(string log, string ids, int days)
        {
            var l = new List<EventRecord>();
            try
            {
                var q = new EventLogQuery(log, PathType.LogName, "*[System[(" + ids + ") and TimeCreated[timediff(@SystemTime) <= " + ((long)days * 86400000L) + "]]]") { ReverseDirection = true };
                using (var rd = new EventLogReader(q)) { EventRecord r; while ((r = rd.ReadEvent()) != null && l.Count < 3000) l.Add(r); }
            }
            catch (Exception ex) { Log.Write("Journal " + log + " : " + ex.Message); }
            return l;
        }

        static string Ids(params int[] ids) { return string.Join(" or ", ids.Select(i => "EventID=" + i)); }

        // en-tête de minidump : signature PAGEDU64 + code d'arrêt à 0x38 (64 bits) ou PAGEDUMP + code à 0x20 (32 bits)
        public static bool ReadDump(string path, out uint code, out ulong p1)
        {
            code = 0; p1 = 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var h = new byte[0x60]; if (fs.Read(h, 0, h.Length) < h.Length) return false;
                    var sig = Encoding.ASCII.GetString(h, 0, 8);
                    if (sig == "PAGEDU64") { code = BitConverter.ToUInt32(h, 0x38); p1 = BitConverter.ToUInt64(h, 0x40); return true; }
                    if (sig == "PAGEDUMP") { code = BitConverter.ToUInt32(h, 0x20); p1 = BitConverter.ToUInt32(h, 0x24); return true; }
                }
            }
            catch { }
            return false;
        }

        static object Wmi(string ns, string q, Func<ManagementObject, object> pick)
        {
            try { using (var s = new ManagementObjectSearcher(ns, q)) foreach (ManagementObject o in s.Get()) return pick(o); } catch { }
            return null;
        }

        public static PanneReport Run(int days, Action<string> step)
        {
            var R = new PanneReport { Days = days };
            Action<string, string, string, string> add = (cat, lvl, title, detail) => R.F.Add(new Finding { Cat = cat, Level = lvl, Title = title, Detail = detail ?? "" });
            Action<DateTime, string, string, string> ev = (at, lvl, src, txt) => R.T.Add(new Ev { At = at, Level = lvl, Source = src, Text = txt });
            var score = new Dictionary<string, int>();
            Action<string, int> hit = (cat, n) => { int v; score.TryGetValue(cat, out v); score[cat] = v + n; };

            // ---------------------------------------------------------- écrans bleus (journal + minidumps)
            step("écrans bleus");
            var bsod = new List<Tuple<DateTime, uint>>();
            foreach (var r in Read("System", Ids(1001, 41, 6008), days))
            {
                var p = (r.ProviderName ?? "");
                var at = r.TimeCreated ?? DateTime.MinValue;
                if (r.Id == 1001 && p.Contains("WER-SystemErrorReporting"))
                {
                    var d = Desc(r); var code = ParseCode(d);
                    bsod.Add(Tuple.Create(at, code)); ev(at, "bad", "Écran bleu", BugName(code) + " — " + d);
                }
                else if (r.Id == 41 && p.Contains("Kernel-Power"))
                {
                    uint bc = 0; try { bc = Convert.ToUInt32(r.Properties[0].Value); } catch { }
                    ev(at, "bad", "Arrêt brutal", bc != 0 ? "plantage (écran bleu " + BugName(bc) + ")" : "coupure sans arrêt propre : courant, bouton maintenu, gel, alimentation");
                    if (bc == 0) hit("Alimentation", 1);
                }
                else if (r.Id == 6008) ev(at, "warn", "Arrêt inattendu", Desc(r));
            }
            var dumpDir = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "Minidump");
            try
            {
                if (Directory.Exists(dumpDir))
                    foreach (var f in new DirectoryInfo(dumpDir).GetFiles("*.dmp").Where(f => (DateTime.Now - f.LastWriteTime).TotalDays <= days))
                    {
                        uint code; ulong p1;
                        if (ReadDump(f.FullName, out code, out p1) && !bsod.Any(b => Math.Abs((b.Item1 - f.LastWriteTime).TotalMinutes) < 10))
                        { bsod.Add(Tuple.Create(f.LastWriteTime, code)); ev(f.LastWriteTime, "bad", "Écran bleu (minidump)", BugName(code) + " — " + f.Name); }
                    }
            }
            catch { }
            if (bsod.Count == 0) add("Écrans bleus", "ok", "Aucun écran bleu sur la période", "");
            foreach (var g in bsod.GroupBy(b => b.Item2).OrderByDescending(g => g.Count()))
            {
                string[] info; Bug.TryGetValue(g.Key, out info);
                var cat = info != null ? info[1] : "Pilote";
                hit(cat, 2 * g.Count());
                add(cat, "bad", g.Count() + " écran(s) bleu(s) " + BugName(g.Key), (info != null ? info[2] + " · " : "") + "dernier le " + g.Max(x => x.Item1).ToString("dd/MM HH:mm"));
            }
            var crashes = R.T.Count(x => x.Source == "Arrêt brutal");
            if (crashes > 0) add("Alimentation", crashes >= 3 ? "bad" : "warn", crashes + " arrêt(s) brutal(aux)", "Kernel-Power 41");

            // ---------------------------------------------------------- disque
            step("disques");
            var diskEv = Read("System", Ids(7, 11, 51, 153, 157, 129, 55, 98), days);
            int bad7 = 0, io = 0, ntfs = 0, resets = 0;
            foreach (var r in diskEv)
            {
                var p = (r.ProviderName ?? "").ToLowerInvariant(); var at = r.TimeCreated ?? DateTime.MinValue;
                if (p == "disk" && r.Id == 7) { bad7++; ev(at, "bad", "Disque", "secteur défectueux : " + Desc(r)); }
                else if (p == "disk" && (r.Id == 11 || r.Id == 51 || r.Id == 153)) { io++; ev(at, "warn", "Disque", "erreur d'entrée/sortie " + r.Id + " : " + Desc(r)); }
                else if (p.Contains("ntfs") && (r.Id == 55 || r.Id == 98)) { ntfs++; ev(at, r.Id == 55 ? "bad" : "warn", "NTFS", Desc(r)); }
                else if (r.Id == 129 && (p.Contains("stornvme") || p.Contains("storahci") || p.Contains("iastor"))) { resets++; ev(at, "warn", "Contrôleur disque", "réinitialisation du périphérique (" + r.ProviderName + ")"); }
            }
            if (bad7 > 0) { add("Disque", "bad", bad7 + " secteur(s) défectueux signalé(s)", "événement Disk 7"); hit("Disque", 4 + bad7); }
            if (io > 0) { add("Disque", io >= 5 ? "bad" : "warn", io + " erreur(s) d'entrée / sortie disque", "Disk 11 / 51 / 153"); hit("Disque", io >= 5 ? 4 : 1); }
            if (ntfs > 0) { add("Disque", "warn", ntfs + " alerte(s) du système de fichiers", "chkdsk à planifier"); hit("Disque", 1); }
            if (resets > 0) { add("Disque", resets >= 3 ? "bad" : "warn", resets + " réinitialisation(s) du contrôleur de stockage", "SSD / firmware / câble"); hit("Disque", resets >= 3 ? 3 : 1); }
            try
            {
                var js = PowerShellJson("Get-PhysicalDisk | % { $r = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; [pscustomobject]@{Name=$_.FriendlyName;Media=[string]$_.MediaType;Bus=[string]$_.BusType;Health=[string]$_.HealthStatus;Op=(($_.OperationalStatus|%{[string]$_}) -join ',');Size=[math]::Round($_.Size/1GB);Temp=$r.Temperature;Wear=$r.Wear;Rerr=$r.ReadErrorsUncorrected;Werr=$r.WriteErrorsUncorrected;Hours=$r.PowerOnHours} } | ConvertTo-Json -Compress");
                foreach (var d in js)
                {
                    Func<string, string> g = key => d.ContainsKey(key) && d[key] != null ? Convert.ToString(d[key]) : "";
                    var health = g("Health"); int temp, wear, rerr, hours; int.TryParse(g("Temp"), out temp); int.TryParse(g("Wear"), out wear); int.TryParse(g("Rerr"), out rerr); int.TryParse(g("Hours"), out hours);
                    var lvl = health != "Healthy" && health != "0" && health.Length > 0 ? "bad" : rerr > 0 || wear >= 90 ? "bad" : wear >= 70 || temp >= 65 ? "warn" : "ok";
                    add("Disque", lvl, g("Name") + " (" + g("Media") + " " + g("Bus") + ", " + g("Size") + " Go) : " + (health == "Healthy" || health == "0" ? "sain" : health),
                        string.Join(" · ", new[] { temp > 0 ? temp + " °C" : "", wear > 0 ? "usure " + wear + " %" : "", rerr > 0 ? rerr + " erreur(s) de lecture non corrigée(s)" : "", hours > 0 ? hours + " h de fonctionnement" : "" }.Where(x => x.Length > 0)));
                    if (lvl == "bad") hit("Disque", 5);
                }
            }
            catch (Exception ex) { add("Disque", "info", "État SMART détaillé indisponible", ex.Message); }
            var predict = Wmi(@"root\wmi", "SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus WHERE PredictFailure = TRUE", o => true);
            if (predict != null) { add("Disque", "bad", "SMART : PANNE IMMINENTE annoncée par le disque", "MSStorageDriver_FailurePredictStatus"); hit("Disque", 8); }

            // ---------------------------------------------------------- matériel (WHEA) et mémoire
            step("matériel et mémoire");
            int whea18 = 0, wheaC = 0;
            foreach (var r in Read("System", Ids(17, 18, 19, 47), days))
            {
                if (!(r.ProviderName ?? "").Contains("WHEA")) continue;
                var at = r.TimeCreated ?? DateTime.MinValue;
                if (r.Id == 18) { whea18++; ev(at, "bad", "Erreur matérielle", "fatale : " + Desc(r)); } else { wheaC++; ev(at, "warn", "Erreur matérielle", "corrigée : " + Desc(r)); }
            }
            if (whea18 > 0) { add("Mémoire / processeur", "bad", whea18 + " erreur(s) matérielle(s) fatale(s) (WHEA 18)", "processeur, RAM, carte mère, chauffe"); hit("Mémoire / processeur", 5); }
            if (wheaC >= 10) { add("Mémoire / processeur", "warn", wheaC + " erreur(s) matérielle(s) corrigée(s)", "souvent PCIe / carte réseau ou graphique"); hit("Mémoire / processeur", 1); }
            foreach (var r in Read("System", Ids(1201, 1202), 3650).Take(1))
            {
                var at = r.TimeCreated ?? DateTime.MinValue;
                if (r.Id == 1202) { add("Mémoire / processeur", "bad", "Le test mémoire Windows a trouvé des erreurs", at.ToString("dd/MM/yyyy")); hit("Mémoire / processeur", 8); }
                else add("Mémoire / processeur", "ok", "Dernier test mémoire Windows sans erreur", at.ToString("dd/MM/yyyy"));
            }

            // ---------------------------------------------------------- pilotes, services, affichage, mises à jour
            step("pilotes et services");
            int svc = 0, tdr = 0, wu = 0, pnp = 0;
            foreach (var r in Read("System", Ids(7000, 7001, 7009, 7011, 7023, 7024, 7026, 7031, 7034, 219, 4101, 20), days))
            {
                var p = r.ProviderName ?? ""; var at = r.TimeCreated ?? DateTime.MinValue;
                if (p.Contains("Service Control Manager")) { svc++; if (svc <= 40) ev(at, "warn", "Service / pilote", Desc(r)); }
                else if (r.Id == 219 && p.Contains("Kernel-PnP")) { pnp++; ev(at, "warn", "Pilote non chargé", Desc(r)); }
                else if (r.Id == 4101) { tdr++; ev(at, "warn", "Affichage", "le pilote graphique a cessé de répondre et a été récupéré"); }
                else if (r.Id == 20 && p.Contains("WindowsUpdateClient")) { wu++; ev(at, "warn", "Windows Update", Desc(r)); }
            }
            if (tdr > 0) { add("Carte graphique", tdr >= 3 ? "bad" : "warn", tdr + " blocage(s) du pilote graphique (TDR)", "écran noir / figé quelques secondes"); hit("Carte graphique", tdr >= 3 ? 3 : 1); }
            if (svc > 0) add("Windows", svc >= 20 ? "warn" : "info", svc + " échec(s) de démarrage de service / pilote", "détail dans la chronologie");
            if (pnp > 0) { add("Pilote", "warn", pnp + " pilote(s) non chargé(s) au démarrage", "Kernel-PnP 219"); hit("Pilote", 1); }
            if (wu > 0) { add("Windows", wu >= 3 ? "warn" : "info", wu + " échec(s) d'installation de mise à jour", "WindowsUpdateClient 20"); hit("Windows", wu >= 3 ? 1 : 0); }
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0 AND ConfigManagerErrorCode <> 22"))
                    foreach (ManagementObject o in s.Get())
                    {
                        int c = Convert.ToInt32(o["ConfigManagerErrorCode"]);
                        add("Pilote", "warn", "Périphérique en erreur : " + (o["Name"] ?? "inconnu"), "code " + c + (c == 28 ? " (pilote absent)" : c == 10 ? " (ne peut pas démarrer)" : c == 43 ? " (arrêté : problème signalé)" : c == 31 ? " (pilote non chargé)" : ""));
                        hit("Pilote", 1);
                    }
            }
            catch { }

            // ---------------------------------------------------------- applications qui plantent
            step("applications");
            var apps = Read("Application", Ids(1000, 1002), days).Where(r => (r.ProviderName ?? "").StartsWith("Application")).Select(r => { try { return Convert.ToString(r.Properties[0].Value); } catch { return "?"; } })
                .GroupBy(a => a, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(5).ToList();
            foreach (var g in apps) add("Applications", g.Count() >= 5 ? "warn" : "info", g.Key + " : " + g.Count() + " plantage(s) / blocage(s)", "");

            // ---------------------------------------------------------- chauffe, batterie, système
            step("chauffe, batterie, système");
            var tz = Wmi(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", o => Convert.ToDouble(o["CurrentTemperature"]) / 10.0 - 273.15);
            if (tz != null)
            {
                var c = (double)tz;
                if (c > 0 && c < 130) { add("Surchauffe", c >= 90 ? "bad" : c >= 80 ? "warn" : "ok", "Température ACPI : " + c.ToString("0") + " °C", "zone thermique de la carte mère"); if (c >= 90) hit("Surchauffe", 4); }
            }
            else add("Surchauffe", "info", "Température non exposée par ce PC", "utiliser HWiNFO si besoin");
            var design = Wmi(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData", o => Convert.ToDouble(o["DesignedCapacity"]));
            var full = Wmi(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", o => Convert.ToDouble(o["FullChargedCapacity"]));
            if (design != null && full != null && (double)design > 0)
            {
                var pct = (double)full / (double)design * 100;
                add("Batterie", pct < 50 ? "bad" : pct < 75 ? "warn" : "ok", "Batterie : " + pct.ToString("0") + " % de sa capacité d'origine", (double)full + " / " + (double)design + " mWh");
                if (pct < 50) hit("Batterie", 3);
            }
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT LastBootUpTime, FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var boot = ManagementDateTimeConverter.ToDateTime(o["LastBootUpTime"].ToString());
                        var up = DateTime.Now - boot;
                        bool fast = false; try { using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power")) fast = Convert.ToInt32(k.GetValue("HiberbootEnabled", 0)) == 1; } catch { }
                        add("Windows", up.TotalDays > 14 ? "warn" : "ok", "Dernier démarrage complet : il y a " + (int)up.TotalDays + " j " + up.Hours + " h", fast ? "démarrage rapide ACTIF : « Arrêter » ne redémarre pas vraiment Windows" : "");
                        double tot = Convert.ToDouble(o["TotalVisibleMemorySize"]) / 1048576.0;
                        add("Mémoire / processeur", tot < 7.5 ? "warn" : "ok", "Mémoire installée : " + tot.ToString("0.#") + " Go", tot < 7.5 ? "insuffisant pour Windows 11 + navigateur" : "");
                    }
            }
            catch { }
            bool pending = false;
            foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired" })
                try { using (var k = Registry.LocalMachine.OpenSubKey(key)) if (k != null) pending = true; } catch { }
            if (pending) add("Windows", "warn", "Redémarrage en attente (mises à jour)", "redémarrer avant tout autre diagnostic");
            try
            {
                var d = new DriveInfo(Proc.SysDrive);
                double freePct = (double)d.AvailableFreeSpace / d.TotalSize * 100;
                add("Windows", freePct < 10 || d.AvailableFreeSpace < 10L << 30 ? "bad" : freePct < 20 ? "warn" : "ok", "Disque système : " + AlexB.Fmt.Size(d.AvailableFreeSpace) + " libres (" + freePct.ToString("0") + " %)", freePct < 10 ? "Windows Update et le système vont échouer" : "");
                if (freePct < 10) hit("Windows", 2);
            }
            catch { }

            // ---------------------------------------------------------- verdict
            R.T = R.T.OrderByDescending(x => x.At).ToList();
            var worst = score.Where(s => s.Value > 0).OrderByDescending(s => s.Value).FirstOrDefault();
            bool anyBad = R.F.Any(f => f.Level == "bad");
            if (worst.Key != null && (anyBad || worst.Value >= 3))
            {
                R.Color = R.F.Any(f => f.Level == "bad" && f.Cat == worst.Key) ? "bad" : "warn";
                R.Verdict = "PISTE PRINCIPALE : " + worst.Key.ToUpperInvariant();
                string fx; R.Advice = Fix.TryGetValue(worst.Key, out fx) ? fx : "";
                var second = score.Where(s => s.Key != worst.Key && s.Value >= 3).OrderByDescending(s => s.Value).FirstOrDefault();
                if (second.Key != null) R.Verdict += "  (puis " + second.Key.ToLowerInvariant() + ")";
            }
            else if (anyBad || R.F.Any(f => f.Level == "warn")) { R.Color = anyBad ? "bad" : "warn"; R.Verdict = "PAS DE PANNE MAJEURE — POINTS À CORRIGER"; R.Advice = "Traiter les points ci-dessous (rouge puis orange)."; }
            else { R.Color = "ok"; R.Verdict = "AUCUNE PANNE DÉTECTÉE SUR " + days + " JOURS"; R.Advice = "Si le client signale un problème, le reproduire puis relancer PanneScan juste après."; }
            var order = new Dictionary<string, int> { { "bad", 0 }, { "warn", 1 }, { "info", 2 }, { "ok", 3 } };
            R.F = R.F.OrderBy(f => order[f.Level]).ToList();
            return R;
        }

        // PowerShell → JSON → liste de dictionnaires (un objet ou un tableau)
        public static List<Dictionary<string, object>> PowerShellJson(string script)
        {
            var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script));
            string o; int rc = Proc.Run(Proc.Sys(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64, 120000, Encoding.UTF8, null, out o);
            var json = (o ?? "").Trim();
            int i = json.IndexOfAny(new[] { '[', '{' }); if (i < 0) throw new Exception("réponse vide (code " + rc + ")");
            var obj = Store.Json().DeserializeObject(json.Substring(i));
            var res = new List<Dictionary<string, object>>();
            var arr = obj as object[]; if (arr == null && obj is ArrayList) arr = ((ArrayList)obj).ToArray();
            if (arr != null) foreach (var x in arr) { var d = x as Dictionary<string, object>; if (d != null) res.Add(d); }
            else { var d = obj as Dictionary<string, object>; if (d != null) res.Add(d); }
            return res;
        }
    }

    class PannePage : Page
    {
        readonly Card verdict; readonly Label vTitle, vSub; readonly LogBox box; readonly Grid timeline; readonly ComboBox days; readonly Chip bRun;
        PanneReport last; bool busy;
        public override string Title { get { return "PanneScan"; } }

        public PannePage()
        {
            Lbl("PANNESCAN", 0, k(2), "accent", 13f, true);
            Lbl("Historique des pannes de ce PC en 30 s : écrans bleus, arrêts brutaux, disque, RAM, pilotes, chauffe, batterie.", k(150), k(10), "muted");
            bRun = Btn("▶  ANALYSER CE PC", t => t.Ok, 0, k(36), (s, e) => Start(), true);
            days = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(bRun.Right + k(10), k(40)), Width = k(120) };
            days.Items.AddRange(new object[] { "7 jours", "30 jours", "90 jours", "1 an" }); days.SelectedIndex = 1; Controls.Add(days);
            int x = days.Right + k(10);
            x = Btn("Copier le rapport", t => t.Accent2, x, k(36), (s, e) => { if (last != null) Clip.Text(last.Text() + "\r\n\r\n" + box.Text.Replace("\n", "\r\n")); }).Right + k(6);
            x = Btn("＋ Intervention avec ce diagnostic", t => t.Accent, x, k(36), (s, e) => { if (last != null) Shell.NewIntervention(null, last.Text()); }).Right + k(6);
            Btn("Moniteur de fiabilité", t => t.Muted, x, k(36), (s, e) => { try { System.Diagnostics.Process.Start("perfmon.exe", "/rel"); } catch { } });
            verdict = new Card { Location = new Point(0, k(80)), Height = k(74), StatusOf = t => t.Muted, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            vTitle = new Label { Text = "PRÊT", AutoSize = true, Font = new Font("Consolas", 14f, FontStyle.Bold), Location = new Point(k(18), k(10)), Tag = "muted" };
            vSub = new Label { Text = "Lance l'analyse. Rien n'est modifié sur le PC.", AutoSize = true, Location = new Point(k(20), k(42)), Tag = "muted" };
            verdict.Controls.Add(vTitle); verdict.Controls.Add(vSub); Controls.Add(verdict);
            box = new LogBox { Location = new Point(0, k(166)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom };
            timeline = new Grid().Col("Date", 120).Col("Type", 150).Col("Détail", 400);
            timeline.Location = new Point(0, k(166)); timeline.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(box); Controls.Add(timeline);
            Resize += (s, e) =>
            {
                verdict.Width = ClientSize.Width; int half = ClientSize.Width / 2;
                box.SetBounds(0, k(166), half - k(8), ClientSize.Height - k(166)); timeline.SetBounds(half + k(8), k(166), ClientSize.Width - half - k(8), ClientSize.Height - k(166));
                vSub.MaximumSize = new Size(verdict.Width - k(40), 0);
            };
        }

        public override void Open(object arg) { if ("run".Equals(arg)) Start(); }

        public override void OnShow()
        {
            if (last == null && Store.D.LastPanneAt.Year > 2000 && !busy)
            {
                vTitle.Text = Store.D.LastPanne; vTitle.Tag = Store.D.LastPanneColor; vSub.Text = "Dernière analyse le " + Store.D.LastPanneAt.ToString("dd/MM/yyyy HH:mm") + " — relance pour actualiser.";
                verdict.StatusOf = t => t.ByName(Store.D.LastPanneColor); CyberForm.Recolor(verdict);
            }
        }

        void Start()
        {
            if (busy) return; busy = true; bRun.Enabled = false; UseWaitCursor = true;
            int d = new[] { 7, 30, 90, 365 }[days.SelectedIndex];
            box.Clear(); timeline.Items.Clear();
            vTitle.Text = "ANALYSE EN COURS…"; vTitle.Tag = "accent"; verdict.StatusOf = t => t.Accent; CyberForm.Recolor(verdict); verdict.Invalidate();
            Task.Run(() =>
            {
                PanneReport r = null;
                try { r = PanneScan.Run(d, s => BeginInvoke(new Action(() => vSub.Text = "analyse : " + s + "…"))); }
                catch (Exception ex) { Log.Write(ex.ToString()); }
                BeginInvoke(new Action(() => Show(r)));
            });
        }

        void Show(PanneReport r)
        {
            busy = false; bRun.Enabled = true; UseWaitCursor = false;
            if (r == null) { vTitle.Text = "ANALYSE IMPOSSIBLE"; vSub.Text = "Voir le journal."; return; }
            last = r; var t = Theme.Current;
            vTitle.Text = r.Verdict; vTitle.Tag = r.Color; vSub.Text = r.Advice;
            verdict.StatusOf = x => x.ByName(r.Color); CyberForm.Recolor(verdict); verdict.Invalidate();
            box.Clear();
            string cat = null;
            foreach (var f in r.F.OrderBy(f => f.Cat == "Écrans bleus" ? "" : f.Cat))
            {
                if (f.Cat != cat) { cat = f.Cat; box.Line("", "text"); box.Line("// " + cat.ToUpperInvariant(), "accent", true); }
                box.Line((f.Level == "bad" ? "  ● " : f.Level == "warn" ? "  ▲ " : f.Level == "ok" ? "  ✓ " : "  · ") + f.Title, f.Level == "info" ? "muted" : f.Level);
                if (f.Detail.Length > 0) box.Line("      " + f.Detail, "muted");
            }
            foreach (var kv in PanneScan.Fix.Where(x => r.F.Any(f => f.Cat == x.Key && f.Level == "bad")))
            { box.Line("", "text"); box.Line("→ " + kv.Key + " : " + kv.Value, "text", true); }
            timeline.BeginUpdate(); timeline.Items.Clear();
            foreach (var e in r.T.Take(500)) timeline.Items.Add(new ListViewItem(new[] { e.At.ToString("dd/MM/yy HH:mm"), e.Source, e.Text }) { ForeColor = t.ByName(e.Level) });
            if (r.T.Count == 0) timeline.Items.Add(new ListViewItem(new[] { "", "✓", "Aucun événement critique sur la période" }) { ForeColor = t.Ok });
            timeline.EndUpdate();
            Store.D.LastPanne = r.Verdict; Store.D.LastPanneColor = r.Color; Store.D.LastPanneAt = DateTime.Now; Store.D.LastPanneText = r.Text();
            try { Store.Save(); } catch { }
        }
    }
}
