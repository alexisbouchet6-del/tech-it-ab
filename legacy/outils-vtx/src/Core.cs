// Outils VTx - bascule VMware Workstation (VT-x natif) / Windows (Hyper-V, VBS, Intégrité de la mémoire)
// Cible : .NET Framework 4.8 (inclus dans Windows 10 / 11)
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Outils VTx")]
[assembly: AssemblyDescription("Bascule VMware Workstation / Hyper-V - VBS")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("Outils VTx")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.1.2.0")]
[assembly: AssemblyFileVersion("1.1.2.0")]

namespace OutilsVTx
{
    // ===================================================================== Journal
    static class Logger
    {
        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OutilsVTx"); } }
        public static string FilePath { get { return Path.Combine(Dir, "outils-vtx.log"); } }
        static readonly object Lock = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ===================================================================== Outils système
    static class Sys
    {
        public const string RegDG = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
        public const string RegHVCI = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
        public const string RegLSA = @"SYSTEM\CurrentControlSet\Control\Lsa";

        static Encoding Oem
        {
            get
            {
                try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
                catch { return Encoding.Default; }
            }
        }

        public static string Exe(string name) { return Path.Combine(Environment.SystemDirectory, name); }

        public static int Run(string exe, string args, out string output, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Oem, StandardErrorEncoding = Oem
            };
            using (var p = Process.Start(psi))
            {
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    output = "Délai dépassé (" + (timeoutMs / 1000) + " s)";
                    Logger.Write(Path.GetFileName(exe) + " " + args + " => délai dépassé");
                    return -1;
                }
                output = (so.Result + " " + se.Result).Trim();
                Logger.Write(Path.GetFileName(exe) + " " + args + " => " + p.ExitCode);
                return p.ExitCode;
            }
        }

        static RegistryKey Hklm() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); }

        public static int? GetDword(string path, string name)
        {
            try
            {
                using (var b = Hklm())
                using (var k = b.OpenSubKey(path))
                {
                    if (k == null) return null;
                    var v = k.GetValue(name);
                    if (v == null) return null;
                    return Convert.ToInt32(v);
                }
            }
            catch { return null; }
        }

        public static void SetDword(string path, string name, int value)
        {
            using (var b = Hklm())
            using (var k = b.CreateSubKey(path))
                k.SetValue(name, value, RegistryValueKind.DWord);
            Logger.Write("Registre " + path + "\\" + name + " = " + value);
        }

        public static void RestoreDword(string path, string name, int? value)
        {
            if (value.HasValue) { SetDword(path, name, value.Value); return; }
            using (var b = Hklm())
            using (var k = b.OpenSubKey(path, true))
                if (k != null) k.DeleteValue(name, false);
            Logger.Write("Registre " + path + "\\" + name + " supprimé");
        }

        public static ManagementObject Wmi(string scope, string query)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(scope, query))
                    foreach (ManagementObject o in s.Get()) return o;
            }
            catch { }
            return null;
        }
    }

    // ===================================================================== État
    class Feature
    {
        public string Name, Label, State;   // State : Enabled / Disabled / Absent
    }

    class VtxStatus
    {
        public string Cpu = "?";
        public bool FirmwareVtx, HypervisorPresent;
        public string LaunchType = "NonDefini";
        public int? VbsConfigured, Hvci, LsaCfgFlags;
        public int VbsStatus = -1;
        public List<int> ServicesRunning = new List<int>();
        public List<Feature> Features = new List<Feature>();
        public bool BitLockerOn;

        public static readonly string[,] FeatureDefs =
        {
            { "Microsoft-Hyper-V-All", "Hyper-V" },
            { "VirtualMachinePlatform", "Plateforme VM (WSL2, Docker)" },
            { "HypervisorPlatform", "Plateforme hyperviseur Windows" },
            { "Containers-DisposableClientVM", "Windows Sandbox" }
        };

        public static readonly Dictionary<int, string> ServiceNames = new Dictionary<int, string>
        {
            { 1, "Credential Guard" }, { 2, "Intégrité de la mémoire (HVCI)" }, { 3, "System Guard Secure Launch" },
            { 4, "SMM Firmware Measurement" }, { 5, "Protection de pile matérielle" }, { 6, "Protection de pile (audit)" },
            { 7, "Hypervisor-Enforced Paging Translation" }
        };

        public enum Mode { VMware, Windows, BiosOff }

        public Mode Current
        {
            get
            {
                if (HypervisorPresent) return Mode.Windows;
                if (!FirmwareVtx) return Mode.BiosOff;
                return Mode.VMware;
            }
        }

        public string Pending
        {
            get
            {
                if (HypervisorPresent && LaunchType.Equals("Off", StringComparison.OrdinalIgnoreCase))
                    return "Mode VMware appliqué : redémarrage nécessaire.";
                if (!HypervisorPresent && LaunchType.Equals("Auto", StringComparison.OrdinalIgnoreCase) &&
                    (Hvci == 1 || VbsConfigured == 1 || Features.Any(f => f.State == "Enabled")))
                    return "Mode Windows appliqué : l'hyperviseur se chargera au prochain redémarrage.";
                return null;
            }
        }

        public static VtxStatus Read()
        {
            var s = new VtxStatus();

            var cpu = Sys.Wmi(@"root\CIMV2", "SELECT Name, VirtualizationFirmwareEnabled FROM Win32_Processor");
            if (cpu != null)
            {
                s.Cpu = Convert.ToString(cpu["Name"]).Trim();
                try { s.FirmwareVtx = Convert.ToBoolean(cpu["VirtualizationFirmwareEnabled"]); } catch { }
            }
            var cs = Sys.Wmi(@"root\CIMV2", "SELECT HypervisorPresent FROM Win32_ComputerSystem");
            if (cs != null) { try { s.HypervisorPresent = Convert.ToBoolean(cs["HypervisorPresent"]); } catch { } }

            var dg = Sys.Wmi(@"root\Microsoft\Windows\DeviceGuard", "SELECT * FROM Win32_DeviceGuard");
            if (dg != null)
            {
                try { s.VbsStatus = Convert.ToInt32(dg["VirtualizationBasedSecurityStatus"]); } catch { }
                try
                {
                    var arr = dg["SecurityServicesRunning"] as Array;
                    if (arr != null) foreach (var x in arr) { int v = Convert.ToInt32(x); if (v != 0) s.ServicesRunning.Add(v); }
                }
                catch { }
            }

            string o;
            if (Sys.Run(Sys.Exe("bcdedit.exe"), "/enum {current}", out o, 15000) == 0)
            {
                var m = System.Text.RegularExpressions.Regex.Match(o, @"hypervisorlaunchtype\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) s.LaunchType = m.Groups[1].Value;
            }

            s.VbsConfigured = Sys.GetDword(Sys.RegDG, "EnableVirtualizationBasedSecurity");
            s.Hvci = Sys.GetDword(Sys.RegHVCI, "Enabled");
            s.LsaCfgFlags = Sys.GetDword(Sys.RegLSA, "LsaCfgFlags");

            for (int i = 0; i < FeatureDefs.GetLength(0); i++)
            {
                var f = new Feature { Name = FeatureDefs[i, 0], Label = FeatureDefs[i, 1], State = "Absent" };
                var w = Sys.Wmi(@"root\CIMV2", "SELECT InstallState FROM Win32_OptionalFeature WHERE Name='" + f.Name + "'");
                if (w != null)
                {
                    int st = 0;
                    try { st = Convert.ToInt32(w["InstallState"]); } catch { }
                    f.State = st == 1 ? "Enabled" : st == 2 ? "Disabled" : "Absent";
                }
                s.Features.Add(f);
            }

            var bl = Sys.Wmi(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='" + Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\') + "'");
            if (bl != null) { try { s.BitLockerOn = Convert.ToInt32(bl["ProtectionStatus"]) == 1; } catch { } }

            return s;
        }
    }

    // ===================================================================== Sauvegarde de l'état d'origine
    static class Backup
    {
        static string FilePath { get { return Path.Combine(Logger.Dir, "backup.ini"); } }

        public static bool Exists { get { return File.Exists(FilePath); } }

        static string N(int? v) { return v.HasValue ? v.Value.ToString() : "null"; }
        static int? P(string v) { int n; return int.TryParse(v, out n) ? (int?)n : null; }

        public static void SaveIfMissing(VtxStatus s)
        {
            if (Exists) { Logger.Write("Sauvegarde d'origine déjà présente, conservée"); return; }
            Directory.CreateDirectory(Logger.Dir);
            var sb = new StringBuilder();
            sb.AppendLine("# Outils VTx - état d'origine (by alex.b)");
            sb.AppendLine("Date=" + DateTime.Now.ToString("s"));
            sb.AppendLine("LaunchType=" + s.LaunchType);
            sb.AppendLine("VBS=" + N(s.VbsConfigured));
            sb.AppendLine("HVCI=" + N(s.Hvci));
            sb.AppendLine("LsaCfgFlags=" + N(s.LsaCfgFlags));
            sb.AppendLine("Features=" + string.Join(";", s.Features.Select(f => f.Name + ":" + f.State)));
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            Logger.Write("Sauvegarde d'origine créée");
        }

        public class Data
        {
            public string Date = "";
            public int? Vbs, Hvci, Lsa;
            public Dictionary<string, string> Features = new Dictionary<string, string>();
        }

        public static Data Load()
        {
            if (!Exists) return null;
            var d = new Data();
            foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                int eq = raw.IndexOf('=');
                if (eq < 1 || raw.StartsWith("#")) continue;
                string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "Date": d.Date = v; break;
                    case "VBS": d.Vbs = P(v); break;
                    case "HVCI": d.Hvci = P(v); break;
                    case "LsaCfgFlags": d.Lsa = P(v); break;
                    case "Features":
                        foreach (var pair in v.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var kv = pair.Split(':');
                            if (kv.Length == 2) d.Features[kv[0]] = kv[1];
                        }
                        break;
                }
            }
            return d;
        }
    }

    // ===================================================================== Actions
    static class Actions
    {
        static void Bcd(string value)
        {
            string o;
            if (Sys.Run(Sys.Exe("bcdedit.exe"), "/set {current} hypervisorlaunchtype " + value, out o, 15000) != 0)
                throw new Exception("bcdedit a échoué : " + o);
        }

        // Une modification du BCD peut déclencher la demande de clé BitLocker : suspension pour 1 redémarrage
        static string SuspendBitLocker(VtxStatus s)
        {
            if (!s.BitLockerOn) return null;
            string drive = Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\');
            string o;
            int rc = Sys.Run(Sys.Exe("manage-bde.exe"), "-protectors -disable " + drive + " -RebootCount 1", out o, 30000);
            return rc == 0 ? "BitLocker suspendu pour 1 redémarrage." : "ATTENTION : BitLocker non suspendu (" + o + "). Garde ta clé de récupération à portée de main.";
        }

        static int Dism(string args)
        {
            string o;
            return Sys.Run(Sys.Exe("dism.exe"), "/online /norestart /quiet " + args, out o, 600000);
        }

        public static string EnableVMwareMode()
        {
            var log = new List<string>();
            var s = VtxStatus.Read();
            Backup.SaveIfMissing(s);
            var bl = SuspendBitLocker(s); if (bl != null) log.Add(bl);

            Bcd("off");
            log.Add("Hyperviseur Windows désactivé au démarrage (hypervisorlaunchtype off).");
            Sys.SetDword(Sys.RegHVCI, "Enabled", 0);
            log.Add("Intégrité de la mémoire désactivée.");
            Sys.SetDword(Sys.RegDG, "EnableVirtualizationBasedSecurity", 0);
            log.Add("VBS désactivée.");
            if (s.LsaCfgFlags.HasValue && s.LsaCfgFlags.Value != 0)
            {
                Sys.SetDword(Sys.RegLSA, "LsaCfgFlags", 0);
                log.Add("Credential Guard désactivé (si verrou UEFI par GPO, le registre ne suffit pas).");
            }
            Logger.Write("Mode VMware appliqué");
            return string.Join("\n", log);
        }

        public static string EnableWindowsMode()
        {
            var log = new List<string>();
            var s = VtxStatus.Read();
            var bl = SuspendBitLocker(s); if (bl != null) log.Add(bl);

            Bcd("auto");
            log.Add("Hyperviseur Windows réactivé au démarrage (hypervisorlaunchtype auto).");

            var b = Backup.Load();
            if (b != null)
            {
                Sys.RestoreDword(Sys.RegHVCI, "Enabled", b.Hvci);
                Sys.RestoreDword(Sys.RegDG, "EnableVirtualizationBasedSecurity", b.Vbs);
                Sys.RestoreDword(Sys.RegLSA, "LsaCfgFlags", b.Lsa);
                log.Add("VBS / Intégrité mémoire / Credential Guard restaurés (sauvegarde du " + b.Date + ").");
                foreach (var f in s.Features)
                {
                    string orig;
                    if (b.Features.TryGetValue(f.Name, out orig) && orig == "Enabled" && f.State == "Disabled")
                    {
                        int rc = Dism("/enable-feature /featurename:" + f.Name + " /all");
                        log.Add((rc == 0 || rc == 3010 ? "Réactivé : " : "Échec réactivation (" + rc + ") : ") + f.Label);
                    }
                }
            }
            else
            {
                Sys.SetDword(Sys.RegHVCI, "Enabled", 1);
                Sys.RestoreDword(Sys.RegDG, "EnableVirtualizationBasedSecurity", null);
                log.Add("Aucune sauvegarde : Intégrité de la mémoire réactivée (réglage Windows par défaut).");
            }
            Logger.Write("Mode Windows appliqué");
            return string.Join("\n", log);
        }

        public static string DisableFeatures()
        {
            var log = new List<string>();
            var s = VtxStatus.Read();
            Backup.SaveIfMissing(s);
            var enabled = s.Features.Where(f => f.State == "Enabled").ToList();
            if (enabled.Count == 0) return "Aucune fonctionnalité hyperviseur active.";
            foreach (var f in enabled)
            {
                int rc = Dism("/disable-feature /featurename:" + f.Name);
                log.Add((rc == 0 || rc == 3010 ? "Désactivé : " : "Échec (" + rc + ") : ") + f.Label);
            }
            return string.Join("\n", log);
        }
    }

}
