// AMI·OPS - Optimisation : bilan noté sur 10 par élément, préconisations, nettoyage, démarrage, applis, MAJ + pilotes, antivirus
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

namespace AmiOps
{
    class Elem { public string Name = "", Constat = "", Preco = ""; public double Note = -1; public double Weight = 1; }

    class BilanResult
    {
        public List<Elem> E = new List<Elem>();
        public List<string> Upgrades = new List<string>();
        public double Global = -1;
        public string Text()
        {
            var sb = new StringBuilder();
            sb.AppendLine("BILAN DU POSTE " + Environment.MachineName + " — note globale " + (Global >= 0 ? Global.ToString("0.0") : "?") + " / 10 (AMI·OPS)");
            foreach (var e in E) sb.AppendLine("- " + e.Name + " : " + (e.Note >= 0 ? e.Note.ToString("0.0") + "/10" : "n/a") + " — " + e.Constat + (e.Preco.Length > 0 ? " → " + e.Preco : ""));
            if (Upgrades.Count > 0) { sb.AppendLine("PRÉCONISATIONS MATÉRIEL :"); foreach (var u in Upgrades) sb.AppendLine("  • " + u); }
            return sb.ToString().TrimEnd();
        }
    }

    class Upd { public int I; public string Title = "", Kb = ""; public int Type; public double Mb; }

    static class Optim
    {
        static double Clamp(double v) { return Math.Max(0, Math.Min(10, Math.Round(v, 1))); }
        static object W(string ns, string q, string prop) { try { using (var s = new ManagementObjectSearcher(ns, q)) foreach (ManagementObject o in s.Get()) return o[prop]; } catch { } return null; }

        public static Dictionary<string, double> WinSat()
        {
            var d = new Dictionary<string, double>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_WinSAT"))
                    foreach (ManagementObject o in s.Get())
                        foreach (var p in new[] { "CPUScore", "MemoryScore", "DiskScore", "GraphicsScore" })
                        { double v = Convert.ToDouble(o[p]); if (v > 0) d[p] = v; }
            }
            catch { }
            return d;
        }

        public static bool OnBattery()
        {
            try { using (var s = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery")) foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["BatteryStatus"]) == 1; } catch { }
            return false;
        }

        // ------------------------------------------------------------------ Windows Update (COM WUA via PowerShell)
        public static List<Upd> SearchUpdates(out string err)
        {
            err = null;
            try
            {
                var l = PanneScan.PowerShellJson("$s=New-Object -ComObject Microsoft.Update.Session;$r=$s.CreateUpdateSearcher().Search('IsInstalled=0 and IsHidden=0');$o=@();$i=0;" +
                    "foreach($u in $r.Updates){$o+=[pscustomobject]@{I=$i;T=$u.Title;Ty=[int]$u.Type;Mb=[math]::Round($u.MaxDownloadSize/1MB,1);Kb=($u.KBArticleIDs -join ',')};$i++};" +
                    "if($o.Count -eq 0){'[]'}else{ConvertTo-Json -InputObject $o -Compress}");
                return l.Select(d => new Upd { I = Convert.ToInt32(d["I"]), Title = Convert.ToString(d["T"]), Type = Convert.ToInt32(d["Ty"]), Mb = Convert.ToDouble(d["Mb"]), Kb = Convert.ToString(d["Kb"]) }).ToList();
            }
            catch (Exception ex) { err = ex.Message; return new List<Upd>(); }
        }

        public static bool InstallUpdates(List<Upd> sel, Action<string, string> log)
        {
            if (sel.Count == 0) return true;
            var tmp = Path.Combine(Path.GetTempPath(), "amiops-wu-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(tmp, sel.Select(u => u.Title), Encoding.UTF8);
            try
            {
                string o; bool reboot = false; int ok = 0, ko = 0;
                Sh.Ps("$want=Get-Content -LiteralPath " + Sh.Q(tmp) + " -Encoding UTF8;$s=New-Object -ComObject Microsoft.Update.Session;$r=$s.CreateUpdateSearcher().Search('IsInstalled=0 and IsHidden=0');" +
                      "$c=New-Object -ComObject Microsoft.Update.UpdateColl;foreach($u in $r.Updates){if($want -contains $u.Title){if(-not $u.EulaAccepted){$u.AcceptEula()};[void]$c.Add($u)}};" +
                      "if($c.Count -eq 0){'AUCUNE'}else{'TELECHARGEMENT|'+$c.Count;$d=$s.CreateUpdateDownloader();$d.Updates=$c;[void]$d.Download();'INSTALLATION';$in=$s.CreateUpdateInstaller();$in.Updates=$c;$res=$in.Install();" +
                      "for($j=0;$j -lt $c.Count;$j++){'R|'+$res.GetUpdateResult($j).ResultCode+'|'+$c.Item($j).Title};'REBOOT|'+$res.RebootRequired}", 4 * 3600 * 1000,
                    l =>
                    {
                        if (l.StartsWith("TELECHARGEMENT")) log("  téléchargement de " + l.Split('|')[1] + " mise(s) à jour…", "muted");
                        else if (l == "INSTALLATION") log("  installation…", "muted");
                        else if (l.StartsWith("R|")) { var p = l.Split(new[] { '|' }, 3); bool s = p[1] == "2" || p[1] == "3"; if (s) ok++; else ko++; log("  " + (s ? "✓ " : "✗ ") + p[2] + (s ? "" : " (code " + p[1] + ")"), s ? "ok" : "bad"); }
                        else if (l.StartsWith("REBOOT|")) reboot = l.EndsWith("True");
                        else if (l == "AUCUNE") log("  plus rien à installer", "muted");
                    }, out o);
                log("Mises à jour : " + ok + " installée(s), " + ko + " échec(s)" + (reboot ? " — REDÉMARRAGE NÉCESSAIRE" : ""), ko == 0 ? "ok" : "warn");
                return ko == 0;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // ------------------------------------------------------------------ Nettoyage
        public class CleanCat { public string Name = "", Hint = ""; public long Size = -1; public bool Default = true; public List<string> Dirs = new List<string>(); public Func<Action<string, string>, long> Special; public DateTime Older = DateTime.Now.AddDays(-1); }

        [StructLayout(LayoutKind.Sequential, Pack = 4)] struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

        public static long RecycleSize() { var i = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) }; try { if (SHQueryRecycleBin(null, ref i) == 0) return i.i64Size; } catch { } return 0; }

        public static List<CleanCat> CleanScan()
        {
            var win = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var l = new List<CleanCat>
            {
                new CleanCat { Name = "Fichiers temporaires (utilisateur)", Hint = "%TEMP%, plus de 24 h", Dirs = { Path.GetTempPath() } },
                new CleanCat { Name = "Fichiers temporaires Windows", Hint = @"Windows\Temp, plus de 24 h", Dirs = { Path.Combine(win, "Temp") } },
                new CleanCat { Name = "Cache Windows Update", Hint = "mises à jour déjà téléchargées", Dirs = { Path.Combine(win, @"SoftwareDistribution\Download") }, Older = DateTime.Now,
                    Special = log => { Sh.Service("wuauserv", false); Sh.Service("bits", false); long f = Sh.Purge(Path.Combine(win, @"SoftwareDistribution\Download"), DateTime.Now); Sh.Service("bits", true); Sh.Service("wuauserv", true); return f; } },
                new CleanCat { Name = "Cache d'optimisation de distribution", Hint = "partage P2P des mises à jour", Dirs = { Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") },
                    Special = log => { long before = Sh.DirSize(Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), DateTime.MaxValue); Sh.Ps("Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue", 120000); return before; } },
                new CleanCat { Name = "Rapports d'erreurs Windows", Hint = "WER (les minidumps sont conservés)", Dirs = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportQueue") }, Older = DateTime.Now },
                new CleanCat { Name = "Miniatures et cache d'icônes", Hint = "reconstruits automatiquement", Dirs = { Path.Combine(local, @"Microsoft\Windows\Explorer") }, Older = DateTime.Now, Default = false,
                    Special = log => { long f = 0; foreach (var p in Directory.GetFiles(Path.Combine(local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db")) try { long s = new FileInfo(p).Length; File.Delete(p); f += s; } catch { } return f; } },
                new CleanCat { Name = "Caches des navigateurs", Hint = "Chrome, Edge, Firefox (navigateurs fermés)", Older = DateTime.Now },
                new CleanCat { Name = "Corbeille", Hint = "vidée définitivement", Older = DateTime.Now, Special = log => { long s = RecycleSize(); SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); return s; } },
                new CleanCat { Name = "Composants Windows obsolètes (WinSxS)", Hint = "DISM StartComponentCleanup, 5 à 20 min", Default = false, Size = -1,
                    Special = log => { log("  DISM /StartComponentCleanup…", "muted"); Sh.Cmd(Proc.Sys("dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup", 3600000); return 0; } },
            };
            var bc = l.First(c => c.Name.StartsWith("Caches des nav"));
            foreach (var pat in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data" })
            {
                var root = Path.Combine(local, pat); if (!Directory.Exists(root)) continue;
                foreach (var prof in Directory.GetDirectories(root).Where(d => Path.GetFileName(d) == "Default" || Path.GetFileName(d).StartsWith("Profile ")))
                    foreach (var c in new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache" }) bc.Dirs.Add(Path.Combine(prof, c));
            }
            var ff = Path.Combine(local, @"Mozilla\Firefox\Profiles"); if (Directory.Exists(ff)) foreach (var p in Directory.GetDirectories(ff)) bc.Dirs.Add(Path.Combine(p, "cache2"));
            foreach (var c in l)
            {
                if (c.Name == "Corbeille") c.Size = RecycleSize();
                else if (c.Name.StartsWith("Composants")) c.Size = -1;
                else c.Size = c.Dirs.Sum(d => Directory.Exists(d) ? Sh.DirSize(d, c.Older) : 0);
                if (c.Name.StartsWith("Miniatures")) try { c.Size = Directory.GetFiles(c.Dirs[0], "thumbcache_*.db").Sum(f => new FileInfo(f).Length); } catch { }
            }
            return l;
        }

        public static long Clean(List<CleanCat> cats, Action<string, string> log)
        {
            long total = 0;
            foreach (var c in cats)
            {
                long f = 0;
                if (c.Name.StartsWith("Caches des nav") && Process.GetProcesses().Any(p => Regex.IsMatch(p.ProcessName, "^(chrome|msedge|firefox)$", RegexOptions.IgnoreCase)))
                { log("  " + c.Name + " : navigateur ouvert, ignoré (ferme-le puis relance)", "warn"); continue; }
                try { f = c.Special != null ? c.Special(log) : c.Dirs.Sum(d => Sh.Purge(d, c.Older)); }
                catch (Exception ex) { log("  " + c.Name + " : " + ex.Message, "warn"); }
                total += f; log("  ✓ " + c.Name + (f > 0 ? " : " + AlexB.Fmt.Size(f) + " libérés" : ""), "ok");
            }
            log("Nettoyage terminé : " + AlexB.Fmt.Size(total) + " libérés.", "ok");
            return total;
        }

        // ------------------------------------------------------------------ Antivirus
        public static string MpCmd { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe"); } }

        public static Dictionary<string, object> Defender()
        {
            try { var l = PanneScan.PowerShellJson("Get-MpComputerStatus | select @{n='Mode';e={[string]$_.AMRunningMode}},AntivirusEnabled,RealTimeProtectionEnabled,AntivirusSignatureAge,QuickScanAge,FullScanAge | ConvertTo-Json -Compress"); return l.FirstOrDefault(); }
            catch { return null; }
        }

        public static List<string> OtherAv()
        {
            var l = new List<string>();
            try { using (var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct")) foreach (ManagementObject o in s.Get()) l.Add(Convert.ToString(o["displayName"]) + "|" + Convert.ToString(o["productState"])); } catch { }
            return l;
        }

        public static void AvScan(bool full, Action<string, string> log)
        {
            var d = Defender();
            var mode = d != null && d.ContainsKey("Mode") ? Convert.ToString(d["Mode"]) : "";
            if (mode == "Normal" && File.Exists(MpCmd))
            {
                log("  mise à jour des définitions Defender…", "muted");
                Sh.Cmd(MpCmd, "-SignatureUpdate", 600000);
                log("  analyse " + (full ? "complète (longue)" : "rapide") + " en cours…", "muted");
                int rc = Sh.Cmd(MpCmd, "-Scan -ScanType " + (full ? "2" : "1"), (full ? 12 : 1) * 3600 * 1000);
                var th = Sh.Ps("Get-MpThreat | ?{$_.IsActive -or $_.DidThreatExecute} | %{ 'MENACE|'+$_.ThreatName+'|'+$_.SeverityID }", 120000);
                var found = th.Split('\n').Where(x => x.StartsWith("MENACE|")).ToList();
                foreach (var t in found) { var p = t.Trim().Split('|'); log("  ⚠ " + p[1] + " (gravité " + p[2] + ")", "bad"); }
                log(rc == 0 && found.Count == 0 ? "Analyse Defender terminée : aucune menace active." : rc == 2 || found.Count > 0 ? "MENACE(S) DÉTECTÉE(S) : voir Sécurité Windows > Protection contre les virus > Historique." : "Analyse terminée (code " + rc + ").", rc == 0 && found.Count == 0 ? "ok" : "bad");
                return;
            }
            var avs = OtherAv().Select(a => a.Split('|')[0]).Where(a => a.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            log("  Defender n'est pas l'antivirus actif" + (avs.Count > 0 ? " (antivirus : " + string.Join(", ", avs) + " — lance aussi une analyse depuis sa console)" : "") + ".", "warn");
            log("  Second avis : Microsoft Safety Scanner (outil officiel Microsoft, autonome)…", "muted");
            var exe = Path.Combine(Hardening.SecureTempDir("AmiOps-msert"), "msert.exe");   // dossier admin seul : pas d'échange de l'exe entre la vérification de signature et le lancement
            try
            {
                using (var wc = new System.Net.WebClient()) wc.DownloadFile("https://go.microsoft.com/fwlink/?LinkId=212732", exe);
                // signature valide + éditeur Microsoft + chaîne qui remonte à une racine « Microsoft Root Certificate Authority »
                var sig = Sh.Ps("$s=Get-AuthenticodeSignature -LiteralPath " + Sh.Q(exe) + ";$c=New-Object Security.Cryptography.X509Certificates.X509Chain;$null=$c.Build($s.SignerCertificate);" +
                    "[string]$s.Status+'|'+$s.SignerCertificate.Subject+'|'+$c.ChainElements[$c.ChainElements.Count-1].Certificate.Subject", 60000).Trim();
                if (!SafetyScannerTrusted(sig)) { File.Delete(exe); log("  Signature de msert.exe non conforme (" + sig + ") : fichier supprimé, non exécuté.", "bad"); return; }
                log("  Signature Microsoft vérifiée. Safety Scanner s'ouvre : choisis l'analyse rapide ou complète.", "ok");
                Process.Start(exe);
            }
            catch (Exception ex) { log("  Téléchargement impossible : " + ex.Message, "bad"); }
        }

        // testable : « Valid|CN=…, O=Microsoft Corporation, …|CN=Microsoft Root Certificate Authority 2011, O=Microsoft Corporation, … »
        public static bool SafetyScannerTrusted(string sig)
        {
            var p = (sig ?? "").Split('|'); if (p.Length < 3 || p[0] != "Valid") return false;
            return Regex.IsMatch(p[1], @"(^|,\s*)O=Microsoft Corporation(,|$)") && Regex.IsMatch(p[2], @"^CN=Microsoft Root Certificate Authority( \d{4})?,") && Regex.IsMatch(p[2], @"(^|,\s*)O=Microsoft Corporation(,|$)");
        }

        // ------------------------------------------------------------------ Bilan
        public static BilanResult Bilan(bool speedTest, Action<string> step)
        {
            var R = new BilanResult(); var add = new Action<Elem>(x => R.E.Add(x));
            double age = -1; try { var bd = Convert.ToString(W("root\\cimv2", "SELECT ReleaseDate FROM Win32_BIOS", "ReleaseDate")); if (bd.Length > 8) age = (DateTime.Now - ManagementDateTimeConverter.ToDateTime(bd)).TotalDays / 365.25; } catch { }

            if (speedTest)
            {
                if (OnBattery()) step("test de rapidité ignoré : PC sur batterie (brancher le secteur)");
                else { step("test de rapidité WinSAT (1 à 3 min)"); Sh.Cmd(Proc.Sys("winsat.exe"), "formal -restart clean", 900000); }
            }
            var ws = WinSat();
            Func<string, double> wsn = key => ws.ContainsKey(key) ? Clamp(ws[key] / 9.9 * 10) : -1;

            // processeur
            step("processeur");
            var cpu = Convert.ToString(W("root\\cimv2", "SELECT Name FROM Win32_Processor", "Name") ?? "").Trim();
            var e = new Elem { Name = "Processeur", Weight = 1.2 };
            if (ws.ContainsKey("CPUScore")) { e.Note = wsn("CPUScore"); e.Constat = cpu + " — WinSAT " + ws["CPUScore"].ToString("0.0") + "/9.9"; }
            else { e.Note = age < 0 ? 6 : age < 3 ? 8 : age < 5 ? 7 : age < 7 ? 5 : 3; e.Constat = cpu + " — note estimée d'après l'âge (lancer le test de rapidité)"; }
            if (e.Note < 5) e.Preco = "processeur dépassé : remplacement du PC à prévoir";
            add(e);

            // mémoire
            step("mémoire");
            long ram = 0; int sticks = 0, slots = 0, type = 0, speed = 0, ff = 0;
            try { using (var s = new ManagementObjectSearcher("SELECT Capacity, SMBIOSMemoryType, Speed, FormFactor FROM Win32_PhysicalMemory")) foreach (ManagementObject o in s.Get()) { ram += Convert.ToInt64(o["Capacity"]); sticks++; type = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0); speed = Convert.ToInt32(o["Speed"] ?? 0); ff = Convert.ToInt32(o["FormFactor"] ?? 0); } } catch { }
            try { slots = Convert.ToInt32(W("root\\cimv2", "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray", "MemoryDevices") ?? 0); } catch { }
            double gb = ram / 1073741824.0;
            var ddr = type == 34 ? "DDR5" : type == 26 ? "DDR4" : type == 24 ? "DDR3" : type == 35 ? "LPDDR5" : type == 30 ? "LPDDR4" : "";
            double used = 0; try { using (var s = new ManagementObjectSearcher("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem")) foreach (ManagementObject o in s.Get()) used = 100 - Convert.ToDouble(o["FreePhysicalMemory"]) / Convert.ToDouble(o["TotalVisibleMemorySize"]) * 100; } catch { }
            e = new Elem { Name = "Mémoire vive", Weight = 1.5, Note = gb >= 31 ? 10 : gb >= 15 ? 9 : gb >= 11 ? 7.5 : gb >= 7 ? 6 : gb >= 3.5 ? 3 : 1 };
            if (ws.ContainsKey("MemoryScore")) e.Note = Clamp((e.Note + wsn("MemoryScore")) / 2);
            e.Constat = gb.ToString("0") + " Go " + ddr + (speed > 0 ? " " + speed + " MT/s" : "") + " · " + sticks + " barrette(s)" + (slots > 0 ? " / " + slots + " emplacement(s)" : "") + " · utilisée à " + used.ToString("0") + " %";
            if (gb < 15)
            {
                var freeSlots = slots - sticks;
                e.Preco = "passer à 16 Go" + (ddr.Length > 0 && !ddr.StartsWith("LP") ? " " + ddr + (ff == 12 ? " SO-DIMM (portable)" : " DIMM") : "") + (ddr.StartsWith("LP") ? " impossible (mémoire soudée)" : freeSlots > 0 ? " : " + freeSlots + " emplacement(s) libre(s)" : " : remplacer les barrettes");
                if (!ddr.StartsWith("LP")) R.Upgrades.Add("Mémoire : " + e.Preco + " (vérifier le modèle exact avant achat)");
            }
            add(e);

            // disque système
            step("disque système");
            var letter = Proc.SysDrive.TrimEnd(':', '\\');
            Dictionary<string, object> dk = null;
            try { dk = PanneScan.PowerShellJson("$n=(Get-Partition -DriveLetter " + Sh.Q(letter) + ").DiskNumber;Get-PhysicalDisk|?{$_.DeviceId -eq [string]$n}|%{$r=$_|Get-StorageReliabilityCounter -ErrorAction SilentlyContinue;[pscustomobject]@{Name=$_.FriendlyName;Media=[string]$_.MediaType;Bus=[string]$_.BusType;Health=[string]$_.HealthStatus;Size=[math]::Round($_.Size/1GB);Wear=$r.Wear;Rerr=$r.ReadErrorsUncorrected;Temp=$r.Temperature;Hours=$r.PowerOnHours}}|ConvertTo-Json -Compress").FirstOrDefault(); } catch { }
            Func<string, string> g = key => dk != null && dk.ContainsKey(key) && dk[key] != null ? Convert.ToString(dk[key]) : "";
            var media = g("Media"); var bus = g("Bus");
            bool hdd = media == "HDD" || media == "3", nvme = bus == "NVMe" || bus == "17";
            e = new Elem { Name = "Disque système (type / vitesse)", Weight = 1.5, Note = hdd ? 2 : nvme ? 10 : (media == "SSD" || media == "4") ? 8 : 6 };
            if (ws.ContainsKey("DiskScore")) e.Note = Clamp(Math.Min(e.Note, wsn("DiskScore") + 1));
            e.Constat = (g("Name").Length > 0 ? g("Name") + " — " : "") + (hdd ? "disque dur mécanique" : nvme ? "SSD NVMe" : media == "SSD" || media == "4" ? "SSD SATA" : "type non déterminé") + (g("Size").Length > 0 ? ", " + g("Size") + " Go" : "") + (ws.ContainsKey("DiskScore") ? " — WinSAT " + ws["DiskScore"].ToString("0.0") : "");
            if (hdd) { e.Preco = "remplacer par un SSD : gain de rapidité le plus important"; R.Upgrades.Add("Disque : SSD 500 Go à 1 To (NVMe si la carte mère a un port M.2, sinon SATA 2,5\") + clonage du disque actuel"); }
            add(e);

            // santé du disque
            int wear, rerr, temp; int.TryParse(g("Wear"), out wear); int.TryParse(g("Rerr"), out rerr); int.TryParse(g("Temp"), out temp);
            var health = g("Health");
            e = new Elem { Name = "Santé du disque", Weight = 1.2 };
            if (dk == null) { e.Constat = "état SMART non lisible"; }
            else
            {
                e.Note = health != "Healthy" && health != "0" && health.Length > 0 ? 1 : rerr > 0 ? 2 : wear >= 90 ? 3 : wear >= 70 ? 6 : 10;
                e.Constat = (health == "Healthy" || health == "0" ? "sain" : health) + (wear > 0 ? " · usure " + wear + " %" : "") + (rerr > 0 ? " · " + rerr + " erreurs de lecture" : "") + (temp > 0 ? " · " + temp + " °C" : "") + (g("Hours").Length > 0 ? " · " + g("Hours") + " h" : "");
                if (e.Note < 5) { e.Preco = "SAUVEGARDER puis remplacer le disque"; R.Upgrades.Insert(0, "URGENT : disque en fin de vie, sauvegarde immédiate puis remplacement"); }
                else if (e.Note < 8) e.Preco = "surveiller ; test de surface conseillé (Dépannage)";
            }
            add(e);

            // espace
            try
            {
                var d = new DriveInfo(Proc.SysDrive); double pct = (double)d.AvailableFreeSpace / d.TotalSize * 100;
                e = new Elem { Name = "Espace disque", Note = pct >= 30 ? 10 : pct >= 20 ? 8 : pct >= 12 ? 6 : pct >= 8 ? 3 : 1, Constat = AlexB.Fmt.Size(d.AvailableFreeSpace) + " libres sur " + AlexB.Fmt.Size(d.TotalSize) + " (" + pct.ToString("0") + " %)" };
                if (pct < 20) e.Preco = "lancer le nettoyage" + (d.TotalSize < 200L << 30 ? " ; disque trop petit : passer à 500 Go" : "");
                add(e);
            }
            catch { }

            // démarrage
            step("démarrage");
            int bootMs = 0;
            try { int.TryParse(Sh.Ps("$e=Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Diagnostics-Performance/Operational';Id=100} -MaxEvents 1 -ErrorAction SilentlyContinue;if($e){(([xml]$e.ToXml()).Event.EventData.Data|?{$_.Name -eq 'BootTime'}).'#text'}", 60000).Trim(), out bootMs); } catch { }
            var st = Startup.Scan(false, false);
            int on = st.Count(i => i.Enabled), rec = st.Count(i => i.Enabled && i.Recommend);
            double bs = bootMs / 1000.0;
            e = new Elem { Name = "Démarrage", Note = Clamp((bootMs <= 0 ? 7 : bs < 30 ? 10 : bs < 60 ? 8 : bs < 90 ? 6 : bs < 120 ? 4 : 2) - Math.Min(3, rec * 0.5) - (on > 15 ? 1 : 0)) };
            e.Constat = (bootMs > 0 ? "démarrage " + bs.ToString("0") + " s" : "durée de démarrage inconnue") + " · " + on + " programme(s) au démarrage dont " + rec + " inutile(s)";
            if (rec > 0) e.Preco = "désactiver les " + rec + " programme(s) inutile(s) (bouton Démarrage)";
            add(e);

            // applications en arrière-plan
            var top = Process.GetProcesses().Where(p => { try { return p.SessionId != 0; } catch { return false; } }).GroupBy(p => p.ProcessName).Select(grp => Tuple.Create(grp.Key, grp.Sum(p => { try { return p.WorkingSet64; } catch { return 0L; } }))).OrderByDescending(t => t.Item2).Take(3).ToList();
            e = new Elem { Name = "Applications en arrière-plan", Note = used < 60 ? 10 : used < 75 ? 8 : used < 85 ? 5 : 3, Constat = "mémoire utilisée " + used.ToString("0") + " % · plus gourmandes : " + string.Join(", ", top.Select(t => t.Item1 + " " + AlexB.Fmt.Size(t.Item2))) };
            if (used >= 75) e.Preco = "fermer les applis inutiles (bouton Applis en cours)" + (gb < 15 ? " ou ajouter de la mémoire" : "");
            add(e);

            // mises à jour + pilotes
            step("mises à jour Windows (recherche, jusqu'à 2 min)");
            string err; var ups = SearchUpdates(out err);
            int sw = ups.Count(u => u.Type == 1), drv = ups.Count(u => u.Type == 2);
            int days = -1; try { var h = Sh.Ps("$s=(New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher();$c=$s.GetTotalHistoryCount();if($c -gt 0){$s.QueryHistory(0,1)|%{[int]((Get-Date)-$_.Date).TotalDays}}", 60000).Trim(); int.TryParse(h.Split('\n').Last().Trim(), out days); } catch { }
            e = new Elem { Name = "Mises à jour Windows", Weight = 1.2, Note = err != null ? -1 : Clamp(10 - sw * 1.5 - (days > 45 ? 3 : days > 30 ? 1 : 0)) };
            e.Constat = err != null ? "recherche impossible : " + err : sw + " mise(s) à jour en attente" + (days >= 0 ? " · dernière installation il y a " + days + " j" : "");
            if (sw > 0) e.Preco = "installer les mises à jour (bouton Mises à jour + pilotes)";
            add(e);
            int pnp = 0; try { using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0 AND ConfigManagerErrorCode <> 22")) pnp = s.Get().Count; } catch { }
            e = new Elem { Name = "Pilotes", Note = Clamp(10 - pnp * 2 - drv * 1), Constat = pnp + " périphérique(s) en erreur · " + drv + " pilote(s) à mettre à jour (Windows Update)" };
            if (pnp + drv > 0) e.Preco = "installer les pilotes proposés" + (pnp > 0 ? " ; périphérique en erreur : outil du fabricant (Dell / Lenovo / HP via MaintenancePC)" : "");
            add(e);

            // sécurité
            step("sécurité");
            var dfd = Defender(); var avs = OtherAv();
            bool avOn = avs.Any(a => { var p = a.Split('|'); int s; return int.TryParse(p.Length > 1 ? p[1] : "", out s) && ((s >> 12) & 0xF) == 1; });
            bool avUp = avs.Any(a => { var p = a.Split('|'); int s; return int.TryParse(p.Length > 1 ? p[1] : "", out s) && ((s >> 4) & 0xF) == 0; });
            bool fw = Sh.Ps("(Get-NetFirewallProfile|?{-not $_.Enabled}).Count", 30000).Trim() == "0";
            int qAge = -1; if (dfd != null && Convert.ToString(dfd["Mode"]) == "Normal") int.TryParse(Convert.ToString(dfd["QuickScanAge"]), out qAge);
            e = new Elem { Name = "Sécurité (antivirus, pare-feu)", Weight = 1.5, Note = Clamp((avOn ? 5 : 0) + (avUp ? 2 : 0) + (fw ? 2 : 0) + (qAge >= 0 && qAge <= 7 ? 1 : qAge < 0 ? 1 : 0)) };
            e.Constat = (avs.Count > 0 ? string.Join(", ", avs.Select(a => a.Split('|')[0])) : "aucun antivirus déclaré") + (avOn ? " actif" : " INACTIF") + (avUp ? ", à jour" : ", définitions anciennes") + " · pare-feu " + (fw ? "actif" : "DÉSACTIVÉ (au moins un profil)") + (qAge >= 0 ? " · dernière analyse il y a " + qAge + " j" : "");
            if (e.Note < 8) e.Preco = (!avOn ? "activer un antivirus ; " : "") + (!fw ? "réactiver le pare-feu (Sécurité > Audit) ; " : "") + "lancer une analyse antivirus";
            add(e);

            // batterie
            object des = W("root\\wmi", "SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity"), full = W("root\\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
            if (des != null && full != null && Convert.ToDouble(des) > 0)
            {
                double pct = Convert.ToDouble(full) / Convert.ToDouble(des) * 100;
                e = new Elem { Name = "Batterie", Note = Clamp(pct / 10), Constat = pct.ToString("0") + " % de la capacité d'origine" };
                if (pct < 60) { e.Preco = "remplacer la batterie"; R.Upgrades.Add("Batterie : remplacement (capacité " + pct.ToString("0") + " %)"); }
                add(e);
            }

            // graphisme
            if (ws.ContainsKey("GraphicsScore")) add(new Elem { Name = "Graphisme", Weight = 0.5, Note = wsn("GraphicsScore"), Constat = "WinSAT " + ws["GraphicsScore"].ToString("0.0") + "/9.9" });

            // version de Windows
            step("version de Windows");
            int build = 0; try { using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) { int.TryParse(Convert.ToString(k.GetValue("CurrentBuildNumber")), out build); } } catch { }
            e = new Elem { Name = "Version de Windows", Weight = 1.2, Constat = Report.WinVersion() };
            if (build < 22000)
            {
                bool tpm2 = Sh.Ps("$t=Get-CimInstance -Namespace root\\cimv2\\Security\\MicrosoftTpm -ClassName Win32_Tpm -ErrorAction SilentlyContinue;if($t){$t.SpecVersion}", 30000).Trim().StartsWith("2.0");
                bool sb = Sh.Ps("try{Confirm-SecureBootUEFI}catch{'False'}", 30000).Trim() == "True";
                e.Note = 2; e.Constat += " — FIN DE SUPPORT (14/10/2025, sauf ESU)";
                e.Preco = tpm2 && gb >= 4 ? "passer à Windows 11 (TPM 2.0 présent" + (sb ? ", Secure Boot actif" : ", activer le Secure Boot") + " ; vérifier le processeur avec « Contrôle d'intégrité du PC »)" : "TPM 2.0 absent : Windows 11 non supporté → remplacer le PC";
                R.Upgrades.Add(tpm2 ? "Système : migration Windows 11 (licence Windows 10 compatible)" : "Système : PC non compatible Windows 11 → remplacement");
            }
            else if (build < 26100) { e.Note = 4; e.Preco = "version de Windows 11 plus supportée : installer la dernière version (26H2) via Windows Update"; }
            else if (build < 26300 && build < 28000) { e.Note = build < 26200 ? 7 : 8.5; e.Preco = "passer à Windows 11 26H2 (petit paquet d'activation via Windows Update, un redémarrage)"; }
            else e.Note = 10;   // 26H2 (26300) ou 26H1 (28000+, autre socle)
            add(e);

            // synthèse
            var rated = R.E.Where(x => x.Note >= 0).ToList();
            R.Global = rated.Count == 0 ? -1 : Clamp(rated.Sum(x => x.Note * x.Weight) / rated.Sum(x => x.Weight));
            var cpuE = R.E.First(); bool old = age >= 7 || cpuE.Note < 4.5;
            if (old) R.Upgrades.Add("Remplacement du PC conseillé" + (age > 0 ? " (environ " + age.ToString("0") + " ans)" : "") + " : processeur Intel Core i5 / AMD Ryzen 5 récent, 16 Go de RAM, SSD NVMe 512 Go, Windows 11 Pro — plutôt que de nouvelles pièces");
            if (R.Upgrades.Count == 0) R.Upgrades.Add("Aucun achat nécessaire : matériel adapté à un usage bureautique.");
            return R;
        }
    }

    // ===================================================================== Page Optimisation
    class OptimPage : Page
    {
        readonly Label note, noteSub; readonly CheckBox speed; readonly Grid grid; readonly LogBox box; readonly Chip bBilan; readonly Panel noteBox;
        BilanResult last;
        public override string Title { get { return "Optimisation"; } }

        public OptimPage()
        {
            Lbl("OPTIMISATION", 0, k(2), "accent", 13f, true);
            Lbl("Bilan noté sur 10 par élément, puis nettoyage, démarrage, applis, mises à jour, pilotes, antivirus. Point de restauration avant l'optimisation 1 clic.", k(170), k(10), "muted");
            int x = 0;
            bBilan = Btn("▶  BILAN DU POSTE", t => t.Ok, x, k(36), (s, e) => RunBilan(), true); x = bBilan.Right + k(8);
            speed = new CheckBox { Text = "+ test de rapidité (2 min)", Checked = true, AutoSize = true, Location = new Point(x, k(42)) }; Controls.Add(speed); x += k(180);
            x = Btn("⚡ Optimisation 1 clic", t => t.Warn, x, k(36), (s, e) => OneClick()).Right + k(6);
            x = Btn("Nettoyage + corbeille…", t => t.Accent, x, k(36), (s, e) => CleanDialog()).Right + k(6);
            x = Btn("Démarrage…", t => t.Accent, x, k(36), (s, e) => StartupDialog(false)).Right + k(6);
            x = Btn("Services tiers…", t => t.Accent, x, k(36), (s, e) => StartupDialog(true)).Right + k(6);
            x = Btn("Applis en cours…", t => t.Accent, x, k(36), (s, e) => ProcDialog()).Right + k(6);
            x = 0;
            x = Btn("Mises à jour + pilotes…", t => t.Accent2, x, k(74), (s, e) => UpdDialog()).Right + k(6);
            x = Btn("Antivirus rapide", t => t.Accent2, x, k(74), (s, e) => Go("Analyse antivirus rapide", () => Optim.AvScan(false, Out), "av")).Right + k(6);
            x = Btn("Antivirus complet", t => t.Accent2, x, k(74), (s, e) => { if (CyberForm.Ask(this, "Analyse complète : 30 min à plusieurs heures. Lancer ?")) Go("Analyse antivirus complète", () => Optim.AvScan(true, Out), "av"); }).Right + k(6);
            x = Btn("Test du disque (surface)", t => t.Muted, x, k(74), (s, e) => Shell.Go("depann", "surface")).Right + k(6);
            x = Btn("Copier le bilan", t => t.Muted, x, k(74), (s, e) => { if (last != null) Clip.Text(last.Text()); }).Right + k(6);
            Btn("＋ Intervention avec ce bilan", t => t.Muted, x, k(74), (s, e) => { if (last != null) Shell.NewIntervention(null, last.Text()); });
            noteBox = new Panel { Location = new Point(0, k(116)), Size = new Size(k(190), k(150)) };
            note = new Label { Text = "—", AutoSize = false, Dock = DockStyle.Top, Height = k(80), TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Consolas", 36f, FontStyle.Bold), Tag = "muted" };
            noteSub = new Label { Text = "note globale / 10", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopCenter, Tag = "muted" };
            noteBox.Controls.Add(noteSub); noteBox.Controls.Add(note);
            noteBox.Paint += (s, e) => { var t = Theme.Current; using (var p = new Pen(t.ByName((string)note.Tag), 2)) e.Graphics.DrawRectangle(p, 1, 1, noteBox.Width - 3, noteBox.Height - 3); };
            Controls.Add(noteBox);
            grid = new Grid().Col("Élément", 210).Col("Note", 60).Col("Constat", 420).Col("Préconisation", 400);
            grid.Location = new Point(k(200), k(116)); grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(grid);
            box = new LogBox { Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Controls.Add(box);
            Resize += (s, e) => { int h = (ClientSize.Height - k(116)) * 55 / 100; grid.Size = new Size(ClientSize.Width - k(200), h); box.SetBounds(0, k(124) + h, ClientSize.Width, ClientSize.Height - k(124) - h); noteBox.Height = Math.Min(k(150), h); };
        }

        void Out(string s, string c) { box.Line(s, c); var j = Job.Current; if (j != null) j.Record(s, c); }
        void Ui(Action a) { try { BeginInvoke(a); } catch { } }

        void Go(string title, Action a, string group = null)
        {
            if (Jobs.Busy(title)) { CyberForm.Info(this, "« " + title + " » est déjà en cours (page Tâches)."); return; }
            box.Line("", "text"); box.Line("▶ " + title + " — " + DateTime.Now.ToString("HH:mm:ss"), "accent", true);
            Jobs.Run(title, group, j => a());
        }

        void RunBilan()
        {
            bool sp = speed.Checked;
            note.Text = "…"; noteSub.Text = "analyse en cours";
            Go("Bilan du poste", () =>
            {
                var r = Optim.Bilan(sp, s => { Out("  " + s + "…", "muted"); Ui(() => noteSub.Text = s); });
                Ui(() => Show(r));
            });
        }

        void Show(BilanResult r)
        {
            last = r; var t = Theme.Current;
            Func<double, string> col = n => n < 0 ? "muted" : n >= 8 ? "ok" : n >= 5 ? "warn" : "bad";
            note.Text = r.Global >= 0 ? r.Global.ToString("0.0") : "?"; note.Tag = col(r.Global); note.ForeColor = t.ByName((string)note.Tag);
            noteSub.Text = "note globale / 10\r\n" + (r.Global >= 8 ? "poste en bonne forme" : r.Global >= 5 ? "à optimiser" : "intervention nécessaire");
            noteBox.Invalidate();
            grid.BeginUpdate(); grid.Items.Clear();
            foreach (var e in r.E) grid.Items.Add(new ListViewItem(new[] { e.Name, e.Note >= 0 ? e.Note.ToString("0.0") : "—", e.Constat, e.Preco }) { ForeColor = t.ByName(col(e.Note) == "muted" ? "text" : col(e.Note)) });
            grid.EndUpdate();
            Out("", "text"); Out("// PRÉCONISATIONS", "accent");
            foreach (var u in r.Upgrades) Out("  • " + u, u.StartsWith("URGENT") ? "bad" : "text");
            Out("Bilan terminé : " + note.Text + " / 10. Utilise les boutons pour corriger, puis relance le bilan pour comparer.", "ok");
        }

        // ---------------------------------------------------------------- optimisation 1 clic
        void OneClick()
        {
            var msg = "Optimisation 1 clic :\n\n1. point de restauration\n2. nettoyage (temporaires, cache Windows Update, rapports d'erreurs, navigateurs fermés) + VIDAGE DE LA CORBEILLE\n3. désactivation des programmes de démarrage inutiles (réversible)\n4. installation des mises à jour Windows ET des pilotes proposés\n5. analyse antivirus rapide\n6. nouveau bilan\n\nDurée : 10 à 60 min. Le PC peut demander un redémarrage. Continuer ?";
            if (!CyberForm.Ask(this, msg)) return;
            Go("Optimisation 1 clic", () =>
            {
                Out("1/6 point de restauration…", "text");
                string o; int rc = Sh.Ps("Enable-ComputerRestore -Drive ($env:SystemDrive+'\\') -ErrorAction SilentlyContinue;New-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' -Name SystemRestorePointCreationFrequency -Value 0 -PropertyType DWord -Force|Out-Null;Checkpoint-Computer -Description 'AMI-OPS avant optimisation' -RestorePointType MODIFY_SETTINGS", 600000, null, out o);
                Out(rc == 0 ? "  ✓ point de restauration créé" : "  point de restauration impossible (protection système désactivée ?) : on continue", rc == 0 ? "ok" : "warn");
                Out("2/6 nettoyage…", "text"); Optim.Clean(Optim.CleanScan().Where(c => c.Default).ToList(), Out);
                Out("3/6 démarrage…", "text");
                var rec = Startup.Scan(false, false).Where(i => i.Enabled && i.Recommend).ToList();
                if (rec.Count == 0) Out("  rien d'inutile au démarrage", "ok"); foreach (var i in rec) Startup.SetEnabled(i, false, Out);
                Out("4/6 mises à jour + pilotes (recherche)…", "text");
                string err; var ups = Optim.SearchUpdates(out err);
                if (err != null) Out("  recherche impossible : " + err, "warn"); else if (ups.Count == 0) Out("  ✓ Windows et pilotes à jour", "ok"); else Optim.InstallUpdates(ups, Out);
                Out("5/6 antivirus…", "text"); Optim.AvScan(false, Out);
                Out("6/6 nouveau bilan…", "text");
                var r = Optim.Bilan(false, s => { });
                Ui(() => Show(r));
            }, "servicing");
        }

        // ---------------------------------------------------------------- fenêtres
        void CleanDialog()
        {
            UseWaitCursor = true;
            Task.Run(() =>
            {
                var cats = Optim.CleanScan();
                Ui(() =>
                {
                    UseWaitCursor = false;
                    var g = new Grid { CheckBoxes = true, Dock = DockStyle.Fill }; g.Col("Catégorie", 300).Col("Taille", 100).Col("Détail", 320);
                    foreach (var c in cats) g.Items.Add(new ListViewItem(new[] { c.Name, c.Size < 0 ? "?" : AlexB.Fmt.Size(c.Size), c.Hint }) { Tag = c, Checked = c.Default || c.Name == "Corbeille" });
                    using (var f = new Form { Text = "Nettoyage · by alex.b", Size = new Size(k(800), k(470)), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Current.Bg, ShowInTaskbar = false })
                    {
                        var b = new Chip("✓ Nettoyer la sélection (" + AlexB.Fmt.Size(cats.Where(c => c.Size > 0).Sum(c => c.Size)) + " au total)") { AccentOf = t => t.Ok, Dock = DockStyle.Bottom };
                        b.Click += (s, e) =>
                        {
                            var sel = g.CheckedItems.Cast<ListViewItem>().Select(i => (Optim.CleanCat)i.Tag).ToList();
                            if (sel.Count == 0) return;
                            if (sel.Any(c => c.Name == "Corbeille") && !CyberForm.Ask(f, "La corbeille sera vidée DÉFINITIVEMENT. Continuer ?")) return;
                            f.Close(); Go("Nettoyage", () => Optim.Clean(sel, Out), "servicing");
                        };
                        f.Controls.Add(g); f.Controls.Add(b); CyberForm.Recolor(f); f.ShowDialog(this);
                    }
                });
            });
        }

        void StartupDialog(bool services)
        {
            box.Line("Lecture du démarrage (signatures)…", "muted"); UseWaitCursor = true;
            Task.Run(() =>
            {
                var l = Startup.Scan(services, !services);
                if (services) l = l.Where(i => i.Kind == "Service").ToList(); else l = l.Where(i => i.Kind != "Service").ToList();
                Ui(() => { UseWaitCursor = false; using (var f = new StartupForm(l, Out, false)) { if (services) f.Text = "Services tiers (non Microsoft) · by alex.b"; f.ShowDialog(this); } });
            });
        }

        void ProcDialog()
        {
            var me = Process.GetCurrentProcess().Id;
            var sys = new HashSet<string>(new[] { "explorer", "dwm", "csrss", "winlogon", "svchost", "lsass", "services", "smss", "wininit", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "runtimebroker", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "system", "idle", "registry", "memory compression", "securityhealthsystray", "msmpeng", "nissrv", "audiodg", "conhost", "dllhost", "sihost", "applicationframehost", "lockapp", "widgets", "searchindexer", "spoolsv", "wudfhost", "amiops" }, StringComparer.OrdinalIgnoreCase);
            var rows = Process.GetProcesses().Where(p => { try { return p.Id != me && p.SessionId != 0 && !sys.Contains(p.ProcessName); } catch { return false; } })
                .GroupBy(p => p.ProcessName).Select(grp => new { Name = grp.Key, Procs = grp.ToList(), Mem = grp.Sum(p => { try { return p.WorkingSet64; } catch { return 0L; } }), Path = grp.Select(p => { try { return p.MainModule.FileName; } catch { return ""; } }).FirstOrDefault(s => s.Length > 0) ?? "" })
                .OrderByDescending(r => r.Mem).ToList();
            var g = new Grid { CheckBoxes = true, Dock = DockStyle.Fill }; g.Col("Application", 200).Col("Mémoire", 90).Col("Instances", 70).Col("Éditeur", 200).Col("Chemin", 420);
            foreach (var r in rows)
            {
                string pub = ""; try { if (r.Path.Length > 0) pub = FileVersionInfo.GetVersionInfo(r.Path).CompanyName ?? ""; } catch { }
                g.Items.Add(new ListViewItem(new[] { r.Name, AlexB.Fmt.Size(r.Mem), r.Procs.Count.ToString(), pub, r.Path }) { Tag = r.Procs });
            }
            using (var f = new Form { Text = "Applications en cours (session utilisateur) · by alex.b", Size = new Size(k(1050), k(620)), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Current.Bg, ShowInTaskbar = false })
            {
                var b = new Chip("Arrêter les applications cochées (travail non enregistré perdu)") { AccentOf = t => t.Bad, Dock = DockStyle.Bottom };
                b.Click += (s, e) =>
                {
                    var sel = g.CheckedItems.Cast<ListViewItem>().ToList(); if (sel.Count == 0) return;
                    if (!CyberForm.Ask(f, "Arrêter : " + string.Join(", ", sel.Select(i => i.Text)) + " ?\nLes documents non enregistrés de ces applis seront perdus.")) return;
                    foreach (var it in sel) foreach (var p in (List<Process>)it.Tag) try { p.Kill(); } catch { }
                    Out("Arrêtées : " + string.Join(", ", sel.Select(i => i.Text)), "ok");
                    foreach (var it in sel) g.Items.Remove(it);
                };
                f.Controls.Add(g); f.Controls.Add(b); CyberForm.Recolor(f); f.ShowDialog(this);
            }
        }

        void UpdDialog()
        {
            box.Line("Recherche des mises à jour Windows et des pilotes (jusqu'à 2 min)…", "muted"); UseWaitCursor = true;
            Task.Run(() =>
            {
                string err; var ups = Optim.SearchUpdates(out err);
                Ui(() =>
                {
                    UseWaitCursor = false;
                    if (err != null) { Out("Recherche impossible : " + err, "bad"); return; }
                    if (ups.Count == 0) { Out("✓ Windows et pilotes à jour.", "ok"); return; }
                    var g = new Grid { CheckBoxes = true, Dock = DockStyle.Fill }; g.Col("Mise à jour", 560).Col("Type", 80).Col("Taille", 80).Col("KB", 90);
                    foreach (var u in ups) g.Items.Add(new ListViewItem(new[] { u.Title, u.Type == 2 ? "pilote" : "Windows", u.Mb + " Mo", u.Kb }) { Tag = u, Checked = true, ForeColor = u.Type == 2 ? Theme.Current.Accent2 : Theme.Current.Text });
                    using (var f = new Form { Text = "Mises à jour + pilotes · by alex.b", Size = new Size(k(900), k(520)), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Current.Bg, ShowInTaskbar = false })
                    {
                        var b = new Chip("✓ Installer la sélection") { AccentOf = t => t.Ok, Dock = DockStyle.Bottom };
                        b.Click += (s, e) => { var sel = g.CheckedItems.Cast<ListViewItem>().Select(i => (Upd)i.Tag).ToList(); f.Close(); if (sel.Count > 0) Go("Installation de " + sel.Count + " mise(s) à jour", () => Optim.InstallUpdates(sel, Out), "servicing"); };
                        f.Controls.Add(g); f.Controls.Add(b); CyberForm.Recolor(f); f.ShowDialog(this);
                    }
                });
            });
        }
    }
}
