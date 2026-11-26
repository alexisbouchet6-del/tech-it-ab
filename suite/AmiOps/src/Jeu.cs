// Tech IT AB / PC Santé AB - page Jeu : analyse notée, optimisations réversibles, session de jeu, pilotes,
// overclocking (détection et conseils, jamais appliqué), bibliothèque, latence, composants, outils officiels
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

namespace AmiOps
{
    // Constat de l'analyse jeu. Level : bad / warn / info / ok
    class GFinding
    {
        public string Level, Item, Constat, Conseil;
        public GFinding(string level, string item, string constat, string conseil) { Level = level; Item = item; Constat = constat; Conseil = conseil ?? ""; }
    }

    // ===================================================================== Règles pures (sans Windows : testées par T15)
    static class GameRules
    {
        // --- cartes graphiques
        public static string Vendor(string gpu)
        {
            var g = (gpu ?? "").ToLowerInvariant();
            if (g.Contains("nvidia") || g.Contains("geforce") || g.Contains("quadro")) return "NVIDIA";
            if (g.Contains("amd") || g.Contains("radeon") || g.Contains("ati ")) return "AMD";
            if (g.Contains("intel")) return "Intel";
            return "Autre";
        }

        // "dgpu" carte dédiée, "igpu" puce intégrée au processeur, "virtuel" (bureau à distance, VM, écran virtuel)
        public static string Kind(string gpu)
        {
            var g = gpu ?? "";
            if (Regex.IsMatch(g, @"(?i)basic display|basic render|remote display|virtual|vmware|virtualbox|parsec|citrix|hyper-v|spacedesk|idd|displaylink|meta virtual")) return "virtuel";
            switch (Vendor(g))
            {
                case "NVIDIA": return "dgpu";
                case "AMD": return Regex.IsMatch(g, @"(?i)\bRX\s?\d|Radeon\s*(\(TM\)\s*)?(Pro\b|R9|R7|R5|HD\b|VII)|FirePro") ? "dgpu" : "igpu";
                case "Intel": return Regex.IsMatch(g, @"(?i)\bArc\b.*\b[AB]\d{3}\b") ? "dgpu" : "igpu";
            }
            return "dgpu";
        }

        // version Windows du pilote NVIDIA → version commerciale : 32.0.15.6094 → 560.94
        public static string NvidiaVersion(string driverVersion)
        {
            var p = (driverVersion ?? "").Split('.');
            if (p.Length != 4 || !p.All(x => Regex.IsMatch(x, @"^\d+$"))) return "";
            var s = p[2] + p[3].PadLeft(4, '0');
            if (s.Length < 5) return "";
            s = s.Substring(s.Length - 5);
            return s.Substring(0, 3).TrimStart('0') + "." + s.Substring(3);
        }

        // cartes récentes pour lesquelles le HAGS est utile (génération d'images DLSS / AFMF)
        public static bool ModernGpu(string gpu) { return Regex.IsMatch(gpu ?? "", @"(?i)RTX\s?(40|50)\d{2}|RX\s?(7|9)\d{3}"); }

        // --- processeur et carte mère
        public static bool IsAmdCpu(string cpu) { return Regex.IsMatch(cpu ?? "", @"(?i)\bAMD\b|Ryzen|Athlon|Threadripper"); }
        public static bool IsX3D(string cpu) { return Regex.IsMatch(cpu ?? "", @"(?i)\d{4}X3D"); }
        public static bool DualCcdX3D(string cpu) { return Regex.IsMatch(cpu ?? "", @"(?i)Ryzen 9\s+\d{4}X3D"); }

        // chipset lu dans le modèle de carte mère : « ROG STRIX Z790-E GAMING WIFI » → Z790
        public static string Chipset(string board)
        {
            var m = Regex.Match(board ?? "", @"(?i)(?<![A-Z0-9])([ABHQWXZ])(\d{3})[A-Z]{0,2}(?![0-9])");
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() + m.Groups[2].Value : "";
        }

        // 0 = impossible, 1 = limité, 2 = possible. verdict et marche à suivre en français.
        public static int CpuOc(string cpu, string board, bool laptop, out string verdict, out string how)
        {
            cpu = cpu ?? ""; var chip = Chipset(board);
            if (IsAmdCpu(cpu))
            {
                if (Regex.IsMatch(cpu, @"(?i)Ryzen AI|\d{4}(U|H|HS|HX|HN|C|E)\b"))
                { verdict = "processeur de portable : verrouillé"; how = "utiliser le mode performances du fabricant (Armoury Crate, Legion Vantage, Omen Gaming Hub…)"; return 0; }
                if (laptop) { verdict = "PC portable : réglages verrouillés par le fabricant"; how = "mode performances du fabricant uniquement"; return 0; }
                if (chip.StartsWith("A")) { verdict = "carte mère " + chip + " : pas d'overclocking du processeur"; how = "rester d'origine ; seul le profil EXPO/XMP de la mémoire peut être activé (selon le BIOS)"; return 0; }
                if (IsX3D(cpu))
                {
                    if (Regex.IsMatch(cpu, @"(?i)\b9\d{3}X3D")) { verdict = "débloqué (Ryzen 9000X3D)"; how = "PBO + Curve Optimizer dans le BIOS ou AMD Ryzen Master ; multiplicateur réglable"; return chip.Length == 0 ? 1 : 2; }
                    verdict = "limité (cache 3D V-Cache sensible à la tension)"; how = "pas de hausse de multiplicateur ; Curve Optimizer négatif (sous-tension) si le BIOS le propose : moins de chaleur, fréquence tenue plus longtemps"; return 1;
                }
                if (Regex.IsMatch(cpu, @"(?i)Ryzen\s+(Threadripper\s+)?(PRO\s+)?[3579]\s+\d{4}|Threadripper"))
                {
                    if (Regex.IsMatch(cpu, @"(?i)\bPRO\b")) { verdict = "Ryzen PRO : souvent dans un PC de marque verrouillé"; how = "vérifier le BIOS ; sinon rester d'origine"; return 1; }
                    if (chip.Length == 0) { verdict = "processeur débloqué, carte mère non identifiée (PC de marque ?)"; how = "si le BIOS le permet : PBO (Precision Boost Overdrive) + Curve Optimizer ; outil : AMD Ryzen Master"; return 1; }
                    verdict = "débloqué (carte mère " + chip + ")"; how = "le plus rentable : PBO + Curve Optimizer négatif dans le BIOS (ou AMD Ryzen Master), pas un multiplicateur fixe"; return 2;
                }
                verdict = "modèle AMD non reconnu"; how = "rester d'origine"; return 0;
            }
            // Intel
            bool k = Regex.IsMatch(cpu, @"(?i)\b(i[3579]|Ultra\s+[3579])[\s-]+\d{3,5}(K|KF|KS)\b|\d{3,5}(K|KF|KS)\b|\bi[79]-\d{4,5}X(E)?\b");
            bool hx = Regex.IsMatch(cpu, @"(?i)\d{3,5}HX\b");
            if (hx) { verdict = "processeur HX de portable : partiellement débloqué selon le fabricant"; how = "Intel XTU si le BIOS l'autorise ; sinon mode performances du fabricant"; return 1; }
            if (laptop) { verdict = "PC portable : réglages verrouillés par le fabricant"; how = "mode performances du fabricant uniquement"; return 0; }
            if (!k) { verdict = "processeur verrouillé (pas de suffixe K)"; how = "aucun overclocking du processeur ; profil XMP de la mémoire possible sur les cartes B/H récentes"; return 0; }
            if (chip.StartsWith("Z") || chip.StartsWith("X") || chip.StartsWith("W")) { verdict = "débloqué (carte mère " + chip + ")"; how = "dans le BIOS ou Intel XTU : hausse du multiplicateur par petits pas, ou sous-tension pour moins de chaleur"; return 2; }
            if (chip.StartsWith("B") || chip.StartsWith("H")) { verdict = "processeur débloqué mais carte mère " + chip + " : multiplicateur bloqué"; how = "seulement le profil XMP de la mémoire et les limites de puissance selon le BIOS"; return 1; }
            verdict = "processeur débloqué, carte mère non identifiée (PC de marque ?)"; how = "vérifier le BIOS ; les PC de marque sont souvent verrouillés"; return 1;
        }

        // --- mémoire (SMBIOS : 24 DDR3, 26 DDR4, 34 DDR5, 27-30 et 35 LPDDR soudée)
        public static string RamTypeName(int t) { switch (t) { case 24: return "DDR3"; case 26: return "DDR4"; case 34: return "DDR5"; case 27: case 28: case 29: case 30: return "LPDDR"; case 35: return "LPDDR5"; default: return "?"; } }
        public static bool Soldered(int t) { return t >= 27 && t <= 30 || t == 35; }

        // niveau ("bad"/"warn"/"info"/"ok") du constat mémoire
        public static string RamCheck(int modules, int type, int speed, int configured, bool laptop, out string constat, out string conseil)
        {
            var name = RamTypeName(type); conseil = "";
            if (Soldered(type)) { constat = name + " soudée " + (configured > 0 ? configured + " MT/s" : "") + " : rien à régler"; return "ok"; }
            if (modules == 1) { constat = "un seul module " + name + " : mémoire en simple canal"; conseil = "ajouter un 2e module identique (double canal) : gain net en jeu, surtout avec une puce graphique intégrée"; return "warn"; }
            if (laptop) { constat = modules + " modules " + name + (configured > 0 ? " à " + configured + " MT/s" : ""); return "ok"; }
            if (speed > 0 && configured > 0 && speed > configured + 50) { constat = name + " à " + configured + " MT/s alors que les barrettes annoncent " + speed; conseil = "activer le profil XMP (Intel) ou EXPO (AMD) dans le BIOS, puis tester la stabilité (OCCT)"; return "warn"; }
            if (type == 26 && configured > 0 && configured <= 2666 || type == 34 && configured > 0 && configured <= 4800)
            { constat = name + " à " + configured + " MT/s (vitesse par défaut)"; conseil = "si les barrettes sont vendues plus rapides, activer XMP/EXPO dans le BIOS : gain gratuit, réversible"; return "info"; }
            constat = modules + " modules " + name + (configured > 0 ? " à " + configured + " MT/s" : ""); return "ok";
        }

        // --- bibliothèques Steam (libraryfolders.vdf) et manifestes (appmanifest_*.acf)
        public static List<string> SteamLibraries(string vdf)
        {
            return Regex.Matches(vdf ?? "", "\"path\"\\s+\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value.Replace("\\\\", "\\")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static Dictionary<string, string> Acf(string acf)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(acf ?? "", "^\\s*\"(name|installdir|SizeOnDisk|appid)\"\\s+\"([^\"]*)\"", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!d.ContainsKey(m.Groups[1].Value)) d[m.Groups[1].Value] = m.Groups[2].Value;
            return d;
        }

        // --- session de jeu : 0 = jamais fermé, 1 = conseillé (coché), 2 = possible (décoché)
        static readonly HashSet<string> Never = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system","idle","registry","smss","csrss","wininit","winlogon","services","lsass","lsaiso","svchost","dwm","explorer","sihost","fontdrvhost","ctfmon","taskhostw",
            "runtimebroker","searchhost","searchindexer","searchapp","searchui","startmenuexperiencehost","shellexperiencehost","textinputhost","applicationframehost","systemsettings",
            "securityhealthsystray","securityhealthservice","msmpeng","nissrv","mpdefendercoreservice","smartscreen","audiodg","conhost","dllhost","wudfhost","spoolsv","taskmgr","lockapp",
            "memory compression","secure system","msedgewebview2","useroobebroker","dashost","wmiprvse","unsecapp",
            "avp","avpui","ksde","ksdeui","ekrn","egui","bdagent","bdservicehost","vsserv","avastui","avastsvc","avgui","avgsvc","mbam","mbamservice","mbamtray","nortonsecurity","ns","mcshield","mfemms","wrsa","sophosui",
            "teamviewer","teamviewer_service","tv_w32","tv_x64","anydesk","rustdesk","quickassist","meshagent","tacticalrmm",
            "steam","steamwebhelper","steamservice","epicgameslauncher","epicwebhelper","battle.net","agent","eadesktop","eabackgroundservice","ealocalhostsvc","origin","upc","ubisoftconnect","uplaywebcore",
            "galaxyclient","galaxyclient helper","riotclientservices","riotclientux","riotclientuxrender","vgc","vgtray","easyanticheat","easyanticheat_eos","beservice","faceit","faceitclient","faceitservice",
            "xboxpcapp","xboxpcappft","gamingservices","gamingservicesnet","gamebar","gamebarftserver","gameinputsvc","xboxgamebarwidgets","launcher","rockstarservice","socialclubhelper","overwolf",
            "nvcontainer","nvdisplay.container","nvsphelper64","nvidia share","nvidia overlay","nvidia app","nvidia web helper","amdrsserv","radeonsoftware","amdow","amdrssrcext","atieclxx","atiesrxx",
            "igfxem","igfxhk","igfxtray","igcc","igcctray","rtkauduservice64","ravbg64","nahimicsvc64","dax3api","wavessvc64","lghub","lghub_agent","lghub_system_tray","icue","razerappengine",
            "steelseriesgg","steelseriesengine","armourycrate.service","asusoptimization",
            "techitab","pcsanteab","amiops"
        };
        static readonly string[] Vendors = { "nvidia", "advanced micro devices", "intel corporation", "realtek", "logitech", "corsair", "razer", "steelseries", "asustek", "micro-star", "gigabyte", "dolby", "waves", "a-volute", "valve", "epic games", "riot games", "blizzard", "electronic arts", "ubisoft", "teamviewer", "anydesk", "kaspersky", "eset", "bitdefender", "avast", "malwarebytes", "norton", "mcafee", "gen digital" };
        static readonly HashSet<string> Advise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome","msedge","firefox","opera","brave","vivaldi","onedrive","teams","ms-teams","msteams","skype","zoom","slack","dropbox","googledrivefs","icloudservices","icloud",
            "adobecollabsync","creative cloud","ccxprocess","adobeipcbroker","adobe desktop service","adobearm","acrotray","jusched","jucheck","phoneexperiencehost","yourphone",
            "widgets","widgetservice","copilot","m365copilot","cortana","ccleaner","ccleaner64","microsoftedgeupdate","webex","ciscowebexstart"
        };
        // fermeture forcée acceptable (aucun document de l'utilisateur ouvert dedans)
        static readonly HashSet<string> Force = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "onedrive","teams","ms-teams","msteams","skype","zoom","slack","dropbox","googledrivefs","icloudservices","icloud","adobecollabsync","creative cloud","ccxprocess","adobeipcbroker",
            "adobe desktop service","adobearm","acrotray","jusched","jucheck","phoneexperiencehost","yourphone","widgets","widgetservice","copilot","m365copilot","cortana","ccleaner","ccleaner64",
            "microsoftedgeupdate","spotify","discord","webex","ciscowebexstart"
        };

        public static int Classify(string name, string company, string path, string windowsDir)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || Never.Contains(name)) return 0;
            var c = (company ?? "").ToLowerInvariant();
            if (Vendors.Any(v => c.Contains(v))) return 0;
            if (Advise.Contains(name)) return 1;
            if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(windowsDir) && path.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.IsNullOrEmpty(path)) return 0;   // processus protégé illisible : on n'y touche pas
            return 2;
        }
        public static bool ForceOk(string name) { return Force.Contains((name ?? "").Trim()); }

        // --- note sur 100
        public static int Score(IEnumerable<GFinding> l)
        {
            int s = 100;
            foreach (var f in l) s -= f.Level == "bad" ? 15 : f.Level == "warn" ? 6 : 0;
            return Math.Max(0, s);
        }

        // --- sauvegarde des réglages d'avant (liste blanche stricte : les préférences sont modifiables par l'utilisateur
        //     alors que l'appli tourne en administrateur, une ligne trafiquée ne doit jamais écrire ailleurs)
        public class Slot { public bool Hklm; public string Path, Name; public bool Sz; public string Pattern; }
        public static readonly Dictionary<string, Slot> Slots = new Dictionary<string, Slot>
        {
            { "gamemode", new Slot { Path = @"Software\Microsoft\GameBar", Name = "AutoGameModeEnabled", Pattern = @"^[01]$" } },
            { "bgrec", new Slot { Path = @"Software\Microsoft\Windows\CurrentVersion\GameDVR", Name = "HistoricalCaptureEnabled", Pattern = @"^[01]$" } },
            { "hags", new Slot { Hklm = true, Path = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", Name = "HwSchMode", Pattern = @"^[012]$" } },
            { "mspeed", new Slot { Path = @"Control Panel\Mouse", Name = "MouseSpeed", Sz = true, Pattern = @"^[0-2]$" } },
            { "mth1", new Slot { Path = @"Control Panel\Mouse", Name = "MouseThreshold1", Sz = true, Pattern = @"^\d{1,2}$" } },
            { "mth2", new Slot { Path = @"Control Panel\Mouse", Name = "MouseThreshold2", Sz = true, Pattern = @"^\d{1,2}$" } },
        };
        public const string GpuPrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
        public static readonly string[] Schemes = { "381b4222-f694-41f0-9685-ff5bb260df2e", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "a1841308-3541-4fab-bc81-f71556f20b4a", "e9a42b02-d5df-448d-aa00-03f14749eb61" };
        public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e", HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", Saver = "a1841308-3541-4fab-bc81-f71556f20b4a", Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

        public class PrevOp { public string Kind, Slot, Value, Path; }   // Kind : REG / POWER / GPU ; Value null = absent

        static string B64(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); }
        static string UnB64(string s) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch { return null; } }
        public static string PrevReg(string slot, object old) { return "REG|" + slot + "|" + (old == null ? "ABSENT" : Convert.ToString(old)); }
        public static string PrevPower(string guid) { return "POWER|" + guid; }
        public static string PrevGpu(string exe, string old) { return "GPU|" + B64(exe) + "|" + (old == null ? "ABSENT" : B64(old)); }

        public static List<PrevOp> ParsePrev(IEnumerable<string> lines)
        {
            var l = new List<PrevOp>();
            foreach (var raw in lines ?? new string[0])
            {
                var p = (raw ?? "").Trim().Split('|');
                if (p.Length == 3 && p[0] == "REG" && Slots.ContainsKey(p[1]) && (p[2] == "ABSENT" || Regex.IsMatch(p[2], Slots[p[1]].Pattern)))
                    l.Add(new PrevOp { Kind = "REG", Slot = p[1], Value = p[2] == "ABSENT" ? null : p[2] });
                else if (p.Length == 2 && p[0] == "POWER" && Schemes.Contains(p[1].ToLowerInvariant()))
                    l.Add(new PrevOp { Kind = "POWER", Value = p[1].ToLowerInvariant() });
                else if (p.Length == 3 && p[0] == "GPU")
                {
                    var exe = UnB64(p[1]); var v = p[2] == "ABSENT" ? null : UnB64(p[2]);
                    if (exe == null || !ValidExe(exe) || p[2] != "ABSENT" && (v == null || !Regex.IsMatch(v, @"^[A-Za-z0-9=;]{0,120}$"))) continue;
                    l.Add(new PrevOp { Kind = "GPU", Path = exe, Value = v });
                }
            }
            return l;
        }
        public static bool ValidExe(string p) { return !string.IsNullOrEmpty(p) && p.Length < 260 && Regex.IsMatch(p, @"^[A-Za-z]:\\[^<>""|?*\x00-\x1F]+\.exe$", RegexOptions.IgnoreCase); }

        public static string Pack(IEnumerable<string> lines) { return IniLines.Pack(lines); }
        public static List<string> Unpack(string v) { return IniLines.Unpack(v); }
    }

    // valeur multiligne dans config.ini (une ligne par clé) : encodée en base64
    static class IniLines
    {
        public static string Pack(IEnumerable<string> lines) { var t = string.Join("\n", lines ?? new string[0]); return t.Length == 0 ? "" : "b64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(t)); }
        public static List<string> Unpack(string v)
        {
            v = v ?? ""; string t = v;
            if (v.StartsWith("b64:")) try { t = Encoding.UTF8.GetString(Convert.FromBase64String(v.Substring(4))); } catch { return new List<string>(); }
            return t.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();   // ancien format (1.4) : texte brut
        }
    }

    // ===================================================================== Lecture du PC (Windows)
    static class GameScan
    {
        public class Gpu { public string Name = "", Driver = "", Vendor = "", Kind = ""; public DateTime Date; public int AgeDays = -1; }
        public class Disp { public string Device = "", Adapter = ""; public int W, H, Hz, MaxHz, Bpp; }
        public class Game { public string Name = "", Launcher = "", Dir = "", Drive = "", Media = ""; public long Size; }

        static IEnumerable<ManagementObject> Q(string q, string ns = null)
        {
            var l = new List<ManagementObject>();
            try { using (var s = ns == null ? new ManagementObjectSearcher(q) : new ManagementObjectSearcher(ns, q)) foreach (ManagementObject o in s.Get()) l.Add(o); } catch { }
            return l;
        }

        public static bool Laptop()
        {
            var mobile = new ushort[] { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };
            foreach (var o in Q("SELECT ChassisTypes FROM Win32_SystemEnclosure")) { var t = o["ChassisTypes"] as ushort[]; if (t != null && t.Any(x => mobile.Contains(x))) return true; }
            return Q("SELECT Name FROM Win32_Battery").Any();
        }

        public static List<Gpu> Gpus()
        {
            var l = new List<Gpu>();
            foreach (var o in Q("SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController"))
            {
                var g = new Gpu { Name = Convert.ToString(o["Name"]).Trim(), Driver = Convert.ToString(o["DriverVersion"]) };
                g.Vendor = GameRules.Vendor(g.Name); g.Kind = GameRules.Kind(g.Name);
                try { var d = Convert.ToString(o["DriverDate"]); if (d.Length > 0) { g.Date = ManagementDateTimeConverter.ToDateTime(d); g.AgeDays = (int)(DateTime.Now - g.Date).TotalDays; } } catch { }
                l.Add(g);
            }
            return l;
        }

        public static string Cpu() { foreach (var o in Q("SELECT Name FROM Win32_Processor")) return Regex.Replace(Convert.ToString(o["Name"]), @"\s+", " ").Trim(); return ""; }
        public static string Board() { foreach (var o in Q("SELECT Manufacturer, Product FROM Win32_BaseBoard")) return (Convert.ToString(o["Manufacturer"]) + " " + Convert.ToString(o["Product"])).Trim(); return ""; }

        public static void Ram(out int modules, out long totalMb, out int type, out int speed, out int configured)
        {
            modules = 0; totalMb = 0; type = 0; speed = 0; configured = 0;
            foreach (var o in Q("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
            {
                modules++;
                try { totalMb += (long)(Convert.ToUInt64(o["Capacity"]) / 1048576); } catch { }
                try { int t = Convert.ToInt32(o["SMBIOSMemoryType"]); if (type == 0 && t > 0) type = t; } catch { }
                try { speed = Math.Max(speed, Convert.ToInt32(o["Speed"])); } catch { }
                try { int c = Convert.ToInt32(o["ConfiguredClockSpeed"]); if (c > 0) configured = configured == 0 ? c : Math.Min(configured, c); } catch { }
            }
        }

        // ------------------------------------------------------------- écrans (API Win32)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
        const int ENUM_CURRENT_SETTINGS = -1, DM_DISPLAYFREQUENCY = 0x400000, DM_INTERLACED = 2;
        const uint CDS_UPDATEREGISTRY = 1, CDS_TEST = 2;

        static DEVMODE NewMode() { var m = new DEVMODE(); m.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); return m; }

        public static List<Disp> Displays()
        {
            var l = new List<Disp>();
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    var dd = new DISPLAY_DEVICE(); dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                    if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                    if ((dd.StateFlags & 1) == 0) continue;   // pas relié au bureau
                    var cur = NewMode(); if (!EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref cur)) continue;
                    var d = new Disp { Device = dd.DeviceName, Adapter = dd.DeviceString ?? "", W = cur.dmPelsWidth, H = cur.dmPelsHeight, Hz = cur.dmDisplayFrequency, MaxHz = cur.dmDisplayFrequency, Bpp = cur.dmBitsPerPel };
                    for (int m = 0; m < 4000; m++)
                    {
                        var dm = NewMode(); if (!EnumDisplaySettings(dd.DeviceName, m, ref dm)) break;
                        if (dm.dmPelsWidth == d.W && dm.dmPelsHeight == d.H && dm.dmBitsPerPel == d.Bpp && (dm.dmDisplayFlags & DM_INTERLACED) == 0) d.MaxHz = Math.Max(d.MaxHz, dm.dmDisplayFrequency);
                    }
                    l.Add(d);
                }
            }
            catch (Exception ex) { Log.Write("Écrans : " + ex.Message); }
            return l;
        }

        // change la fréquence (testée d'abord par Windows) ; renvoie null si OK, sinon le motif
        public static string SetHz(string device, int hz)
        {
            var dm = NewMode();
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) return "lecture du mode actuel impossible";
            dm.dmDisplayFrequency = hz; dm.dmFields = DM_DISPLAYFREQUENCY;
            int t = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            if (t != 0) return "mode refusé par Windows (code " + t + ")";
            int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            return r == 0 ? null : r == 1 ? "redémarrage nécessaire" : "échec (code " + r + ")";
        }

        // ------------------------------------------------------------- réglages Windows
        public static object RegGet(GameRules.Slot s)
        {
            try { using (var k = (s.Hklm ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(s.Path)) return k == null ? null : k.GetValue(s.Name); } catch { return null; }
        }
        public static void RegSet(GameRules.Slot s, string value)
        {
            using (var k = (s.Hklm ? Registry.LocalMachine : Registry.CurrentUser).CreateSubKey(s.Path))
            {
                if (value == null) k.DeleteValue(s.Name, false);
                else if (s.Sz) k.SetValue(s.Name, value, RegistryValueKind.String);
                else k.SetValue(s.Name, int.Parse(value), RegistryValueKind.DWord);
            }
        }
        public static int RegInt(string slot, int def) { var v = RegGet(GameRules.Slots[slot]); int n; return v != null && int.TryParse(Convert.ToString(v), out n) ? n : def; }

        public static string PowerScheme()
        {
            var m = Regex.Match(Sh.CmdOut(Proc.Sys("powercfg.exe"), "/getactivescheme", 15000), @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            return m.Success ? m.Value.ToLowerInvariant() : "";
        }
        public static bool HasScheme(string guid) { return Sh.CmdOut(Proc.Sys("powercfg.exe"), "/list", 15000).ToLowerInvariant().Contains(guid); }
        public static string SchemeName(string g) { return g == GameRules.Balanced ? "Équilibré" : g == GameRules.HighPerf ? "Performances élevées" : g == GameRules.Saver ? "Économie d'énergie" : g == GameRules.Ultimate ? "Performances optimales" : g.Length == 0 ? "?" : "personnalisé"; }

        public static bool OnBattery() { try { return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline; } catch { return false; } }

        public static bool Hvci()
        {
            foreach (var o in Q("SELECT SecurityServicesRunning FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard"))
            { var a = o["SecurityServicesRunning"] as uint[]; if (a != null && a.Contains(2u)) return true; }
            return false;
        }

        public static string VcRedist(bool x64)
        {
            // l'installateur Microsoft écrit la clé dans la vue 32 bits, parfois aussi en 64 bits : on lit les deux
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = b.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + (x64 ? "x64" : "x86")))
                        if (k != null && Convert.ToInt32(k.GetValue("Installed", 0)) == 1) return Convert.ToString(k.GetValue("Version", "installé"));
                }
                catch { }
            return null;
        }
        public static bool DxLegacy()
        {
            var w = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return File.Exists(Path.Combine(w, @"System32\d3dx9_43.dll")) || File.Exists(Path.Combine(w, @"SysWOW64\d3dx9_43.dll"));
        }

        public static List<string> DevicesInError()
        {
            return Q("SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0")
                .Where(o => { int c = Convert.ToInt32(o["ConfigManagerErrorCode"]); return c != 22 && c != 45; })
                .Select(o => Convert.ToString(o["Name"]) + " (code " + o["ConfigManagerErrorCode"] + ")").Take(12).ToList();
        }

        public static bool NoPagefile()
        {
            foreach (var o in Q("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem")) if (Convert.ToBoolean(o["AutomaticManagedPagefile"])) return false;
            return !Q("SELECT Name FROM Win32_PageFileUsage").Any();
        }

        public static string NvidiaSmi()
        {
            var exe = new[] { Proc.Sys("nvidia-smi.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvidia-smi.exe") }.FirstOrDefault(File.Exists);
            if (exe == null) return null;
            return Sh.CmdOut(exe, "--query-gpu=name,driver_version,temperature.gpu,pcie.link.gen.current,pcie.link.gen.max,pcie.link.width.current,pcie.link.width.max,power.draw,power.limit --format=csv,noheader,nounits", 20000).Trim();
        }

        // lettre de lecteur → "SSD" / "HDD" / "?"
        public static Dictionary<string, string> DriveMedia()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var r in PanneScan.PowerShellJson("Get-Partition|?{$_.DriveLetter}|%{$n=$_.DiskNumber;$p=Get-PhysicalDisk|?{$_.DeviceId -eq [string]$n}|select -First 1;[pscustomobject]@{L=[string]$_.DriveLetter;M=[string]$p.MediaType;B=[string]$p.BusType}}|ConvertTo-Json -Compress"))
                {
                    var m = Convert.ToString(r["M"]); var b = Convert.ToString(r["B"]);
                    d[Convert.ToString(r["L"])] = m == "SSD" || b == "NVMe" ? "SSD" : m == "HDD" ? "HDD" : "?";
                }
            }
            catch { }
            return d;
        }

        public static List<Game> Library()
        {
            var l = new List<Game>();
            // Steam
            string steam = null;
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) if (k != null) steam = Convert.ToString(k.GetValue("SteamPath")).Replace('/', '\\'); } catch { }
            if (string.IsNullOrEmpty(steam)) try { using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")) if (k != null) steam = Convert.ToString(k.GetValue("InstallPath")); } catch { }
            if (!string.IsNullOrEmpty(steam))
            {
                var libs = new List<string> { steam };
                try { var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf"); if (File.Exists(vdf)) libs.AddRange(GameRules.SteamLibraries(File.ReadAllText(vdf))); } catch { }
                foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var apps = Path.Combine(lib, "steamapps");
                    if (!Directory.Exists(apps)) continue;
                    foreach (var acf in SafeFiles(apps, "appmanifest_*.acf"))
                    {
                        var a = GameRules.Acf(ReadSafe(acf)); string n, dir, sz; long size = 0;
                        if (!a.TryGetValue("name", out n) || n.Contains("Redistributable") || n.StartsWith("Proton") || n.StartsWith("Steam Linux")) continue;
                        a.TryGetValue("installdir", out dir); if (a.TryGetValue("SizeOnDisk", out sz)) long.TryParse(sz, out size);
                        l.Add(new Game { Name = n, Launcher = "Steam", Dir = Path.Combine(apps, "common", dir ?? ""), Size = size });
                    }
                }
            }
            // Epic Games
            var man = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            var js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
            foreach (var f in SafeFiles(man, "*.item"))
            {
                try
                {
                    var d = js.DeserializeObject(ReadSafe(f)) as Dictionary<string, object>; if (d == null) continue;
                    object n, loc, size; d.TryGetValue("DisplayName", out n); d.TryGetValue("InstallLocation", out loc); d.TryGetValue("InstallSize", out size);
                    long s = 0; try { s = Convert.ToInt64(size); } catch { }
                    if (n != null && loc != null) l.Add(new Game { Name = Convert.ToString(n), Launcher = "Epic", Dir = Convert.ToString(loc), Size = s });
                }
                catch { }
            }
            var media = DriveMedia();
            foreach (var g in l)
            {
                g.Drive = g.Dir.Length > 1 && g.Dir[1] == ':' ? g.Dir.Substring(0, 1).ToUpperInvariant() : "?";
                string m; g.Media = media.TryGetValue(g.Drive, out m) ? m : "?";
            }
            return l.OrderBy(g => g.Name).ToList();
        }
        static IEnumerable<string> SafeFiles(string dir, string pat) { try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pat) : new string[0]; } catch { return new string[0]; } }
        static string ReadSafe(string f) { try { var i = new FileInfo(f); return i.Length > 4 * 1024 * 1024 ? "" : File.ReadAllText(f); } catch { return ""; } }

        // ------------------------------------------------------------- analyse complète
        public static List<GFinding> Analyze(Action<string, string> log, bool withUpdates)
        {
            var f = new List<GFinding>();
            Action<string> step = s => log("  … " + s, "muted");
            bool laptop = Laptop();
            step("carte graphique et pilote");
            var gpus = Gpus(); var real = gpus.Where(g => g.Kind != "virtuel").ToList(); var dg = real.Where(g => g.Kind == "dgpu").ToList();
            if (real.Count == 0) f.Add(new GFinding("bad", "Carte graphique", "aucun pilote graphique (« carte vidéo de base Microsoft »)", "installer le pilote du fabricant (tuile Pilotes)"));
            else if (dg.Count == 0) f.Add(new GFinding("info", "Carte graphique", "puce intégrée seulement : " + string.Join(", ", real.Select(g => g.Name)), "jeux légers ou réglages bas ; mémoire en double canal indispensable"));
            foreach (var g in real)
            {
                var ver = g.Vendor == "NVIDIA" ? GameRules.NvidiaVersion(g.Driver) : g.Driver;
                var what = g.Name + " · pilote " + (ver.Length > 0 ? ver : g.Driver) + (g.AgeDays >= 0 ? " du " + g.Date.ToString("dd/MM/yyyy") : "");
                if (g.AgeDays > 365) f.Add(new GFinding(g.Kind == "dgpu" ? "bad" : "warn", "Pilote graphique", what + " (plus d'un an)", "mettre à jour avec l'outil officiel du fabricant (tuile Pilotes)"));
                else if (g.Kind == "dgpu" && g.AgeDays > 120) f.Add(new GFinding("warn", "Pilote graphique", what + " (" + g.AgeDays + " jours)", "les jeux récents sont optimisés dans les pilotes récents : mettre à jour"));
                else f.Add(new GFinding("ok", "Pilote graphique", what, ""));
            }
            step("écrans");
            var disp = Displays();
            bool onDgpu = disp.Any(d => GameRules.Kind(d.Adapter) == "dgpu");
            foreach (var d in disp)
            {
                if (d.MaxHz >= d.Hz + 10) f.Add(new GFinding("bad", "Écran " + d.Device.Replace(@"\\.\", ""), d.W + "×" + d.H + " réglé à " + d.Hz + " Hz alors qu'il accepte " + d.MaxHz + " Hz", "tuile « Écran : fréquence max » (essai de 15 s avec retour automatique)"));
                else f.Add(new GFinding("ok", "Écran " + d.Device.Replace(@"\\.\", ""), d.W + "×" + d.H + " à " + d.Hz + " Hz (maximum)", ""));
                if (!laptop && dg.Count > 0 && GameRules.Kind(d.Adapter) == "igpu")
                    f.Add(new GFinding(onDgpu ? "warn" : "bad", "Branchement de l'écran", "écran " + d.Device.Replace(@"\\.\", "") + " relié à la carte mère (" + d.Adapter + ") alors qu'une carte graphique dédiée est présente", "brancher le câble de l'écran de jeu sur la carte graphique (" + dg[0].Name + "), pas sur la carte mère"));
            }
            step("processeur, carte mère et mémoire");
            var cpu = Cpu(); var board = Board();
            int mods, type, speed, conf; long mb; Ram(out mods, out mb, out type, out speed, out conf);
            if (mb > 0 && mb < 7600) f.Add(new GFinding("bad", "Mémoire", (mb / 1024.0).ToString("0") + " Go", "8 Go est le strict minimum, 16 Go conseillés pour les jeux récents"));
            else if (mb > 0 && mb < 15000) f.Add(new GFinding("info", "Mémoire", (mb / 1024.0).ToString("0") + " Go", "16 Go conseillés pour les jeux récents"));
            string rc, rk; var rl = GameRules.RamCheck(mods, type, speed, conf, laptop, out rc, out rk);
            if (mods > 0) f.Add(new GFinding(rl, "Mémoire", rc, rk));
            string ov, oh; int oc = GameRules.CpuOc(cpu, board, laptop, out ov, out oh);
            f.Add(new GFinding("info", "Overclocking processeur", cpu + " : " + ov, oc > 0 ? oh + " (tuile Overclocking pour la marche à suivre)" : ""));
            step("réglages Windows pour le jeu");
            if (GameScan.RegInt("gamemode", 1) == 0) f.Add(new GFinding("warn", "Mode Jeu", "désactivé", "l'activer (tuile Optimiser) : Windows donne la priorité au jeu"));
            else f.Add(new GFinding("ok", "Mode Jeu", "activé", ""));
            if (GameScan.RegInt("bgrec", 0) == 1) f.Add(new GFinding("warn", "Enregistrement en arrière-plan", "la Xbox Game Bar enregistre en continu les dernières minutes de jeu", "le couper (tuile Optimiser) : moins de charge sur la carte graphique et le disque"));
            int hags = GameScan.RegInt("hags", 0);
            bool modern = dg.Any(g => GameRules.ModernGpu(g.Name));
            if (hags == 2) f.Add(new GFinding("ok", "Ordonnancement GPU matériel (HAGS)", "activé", ""));
            else f.Add(new GFinding(modern && hags == 1 ? "warn" : "info", "Ordonnancement GPU matériel (HAGS)", hags == 1 ? "désactivé" : "réglage par défaut de Windows", modern ? "à activer : nécessaire pour la génération d'images (DLSS Frame Generation)" : "gain variable selon la carte ; activable dans Optimiser"));
            var scheme = PowerScheme(); bool amd = GameRules.IsAmdCpu(cpu);
            if (scheme == GameRules.Saver) f.Add(new GFinding(laptop ? "warn" : "bad", "Alimentation", "plan « Économie d'énergie » : processeur et carte graphique bridés", "passer en Équilibré" + (!amd && !laptop ? " ou Performances élevées" : "") + " (tuile Optimiser)"));
            else if (GameRules.DualCcdX3D(cpu) && (scheme == GameRules.HighPerf || scheme == GameRules.Ultimate)) f.Add(new GFinding("warn", "Alimentation", "plan « " + SchemeName(scheme) + " » avec un " + cpu, "AMD demande le plan Équilibré pour que le jeu soit placé sur les cœurs à cache 3D (tuile Optimiser)"));
            else f.Add(new GFinding("ok", "Alimentation", "plan « " + SchemeName(scheme) + " »", !amd && !laptop && scheme == GameRules.Balanced ? "Performances élevées possible (tuile Optimiser)" : ""));
            if (laptop && OnBattery()) f.Add(new GFinding("warn", "Alimentation", "PC portable sur batterie : performances réduites", "brancher le chargeur pour jouer et choisir le mode « Meilleures performances » (Paramètres > Système > Alimentation)"));
            var ms = Convert.ToString(GameScan.RegGet(GameRules.Slots["mspeed"]) ?? "1");
            if (ms != "0") f.Add(new GFinding("info", "Souris", "« Améliorer la précision du pointeur » activée (accélération)", "la couper pour une visée régulière dans les jeux de tir (tuile Optimiser)"));
            if (NoPagefile()) f.Add(new GFinding("warn", "Mémoire virtuelle", "aucun fichier d'échange", "remettre la gestion automatique : sans fichier d'échange les jeux gourmands plantent quand la mémoire est pleine"));
            if (Hvci()) f.Add(new GFinding("info", "Intégrité de la mémoire", "active (protection contre les pilotes malveillants)", "on la garde : le gain en jeu est faible face au risque (pilotes piégés, triches, rançongiciels)"));
            step("composants des jeux");
            var v64 = VcRedist(true); var v86 = VcRedist(false);
            if (v64 == null || v86 == null) f.Add(new GFinding("warn", "Visual C++ 2015-2022", "manquant : " + (v64 == null ? "x64 " : "") + (v86 == null ? "x86" : ""), "tuile Composants des jeux (erreurs « MSVCP140.dll introuvable »)"));
            else f.Add(new GFinding("ok", "Visual C++ 2015-2022", "x64 " + v64 + " · x86 " + v86, ""));
            if (!DxLegacy()) f.Add(new GFinding("info", "DirectX (ancien runtime)", "absent", "seulement pour les anciens jeux (erreur « d3dx9_43.dll ») : tuile Composants des jeux"));
            var err = DevicesInError();
            if (err.Count > 0) f.Add(new GFinding("warn", "Périphériques en erreur", string.Join(" ; ", err), "Gestionnaire de périphériques : réinstaller le pilote"));
            step("disques et bibliothèque de jeux");
            try
            {
                var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                if (sys.TotalSize > 0 && sys.AvailableFreeSpace * 100 / sys.TotalSize < 10) f.Add(new GFinding("warn", "Disque système", "moins de 10 % libre (" + Fmt.Size(sys.AvailableFreeSpace) + ")", "libérer de la place : mises à jour de jeux et fichier d'échange en ont besoin"));
                var lib = Library(); var hdd = lib.Where(g => g.Media == "HDD").ToList();
                if (hdd.Count > 0) f.Add(new GFinding("warn", "Jeux sur disque dur", hdd.Count + " jeu(x) sur disque dur classique : " + string.Join(", ", hdd.Take(5).Select(g => g.Name)), "les déplacer sur un SSD (Steam : Paramètres > Stockage > Déplacer) : chargements et saccades réduits"));
                else if (lib.Count > 0) f.Add(new GFinding("ok", "Bibliothèque", lib.Count + " jeu(x) Steam/Epic, aucun sur disque dur classique", ""));
            }
            catch { }
            step("applications ouvertes");
            try
            {
                var apps = GameOpt.RunningApps(500).Where(a => a.Class == 1).ToList();
                if (apps.Count > 0) f.Add(new GFinding("info", "Applis ouvertes", apps.Count + " appli(s) inutile(s) pendant une partie : " + string.Join(", ", apps.Take(6).Select(a => a.Title)) + " (" + Fmt.Size(apps.Sum(a => a.Mem)) + ")", "tuile Session de jeu avant de lancer le jeu"));
            }
            catch { }
            step("réseau");
            try
            {
                var up = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)).ToList();
                if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) && !up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
                    f.Add(new GFinding("info", "Réseau", "connexion en Wi-Fi", "un câble Ethernet réduit la latence et les à-coups en ligne (tuile Latence pour mesurer)"));
                foreach (var n in up.Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && n.Speed > 0 && n.Speed < 1000000000))
                    f.Add(new GFinding("info", "Réseau", n.Name + " lié à " + (n.Speed / 1000000) + " Mb/s", "câble ou port à vérifier (un lien gigabit est attendu)"));
            }
            catch { }
            if (withUpdates)
            {
                step("pilotes proposés par Windows Update (jusqu'à 2 min)");
                string e; var u = Optim.SearchUpdates(out e);
                if (e != null) f.Add(new GFinding("info", "Windows Update", "recherche impossible : " + e, ""));
                else { var drv = u.Where(x => x.Type == 2).ToList(); if (drv.Count > 0) f.Add(new GFinding("warn", "Pilotes en attente", string.Join(" ; ", drv.Select(x => x.Title).Take(5)), "tuile Pilotes : installer")); }
            }
            return f;
        }

        public static void Print(List<GFinding> f, Action<string, string> log)
        {
            foreach (var x in f.OrderBy(x => x.Level == "bad" ? 0 : x.Level == "warn" ? 1 : x.Level == "info" ? 2 : 3))
            {
                var mark = x.Level == "bad" ? "✗" : x.Level == "warn" ? "!" : x.Level == "info" ? "i" : "✓";
                log("  " + mark + " " + x.Item + " : " + x.Constat, x.Level == "info" ? "text" : x.Level);
                if (x.Conseil.Length > 0) log("      → " + x.Conseil, "muted");
            }
            int s = GameRules.Score(f);
            log("BILAN JEU : " + s + "/100 · " + f.Count(x => x.Level == "bad") + " critique(s), " + f.Count(x => x.Level == "warn") + " à corriger, " + f.Count(x => x.Level == "info") + " conseil(s)", s >= 85 ? "ok" : s >= 60 ? "warn" : "bad");
        }
    }

    // ===================================================================== Actions (réversibles)
    static class GameOpt
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, int[] vparam, uint winIni);
        const uint SPI_SETMOUSE = 0x0004, SPIF_UPDATE = 3;

        public class Choice { public string Id, Label; public bool On; }

        public static bool HasPrev { get { return GameRules.Unpack(Prefs.Ini.Get("GamePrev", "")).Count > 0; } }

        // ce que l'optimisation changerait sur CE PC (rien n'est proposé s'il est déjà bon)
        public static List<Choice> Choices()
        {
            var l = new List<Choice> { new Choice { Id = "restore", Label = "Créer un point de restauration avant de modifier", On = true } };
            if (GameScan.RegInt("gamemode", 1) == 0) l.Add(new Choice { Id = "gamemode", Label = "Activer le Mode Jeu", On = true });
            if (GameScan.RegInt("bgrec", 0) == 1) l.Add(new Choice { Id = "bgrec", Label = "Couper l'enregistrement en arrière-plan (Xbox Game Bar)", On = true });
            var gpus = GameScan.Gpus();
            if (GameScan.RegInt("hags", 0) != 2 && gpus.Any(g => g.Kind == "dgpu"))
                l.Add(new Choice { Id = "hags", Label = "Activer l'ordonnancement GPU matériel (HAGS) — ignoré si la carte ne le gère pas, redémarrage", On = gpus.Any(g => GameRules.ModernGpu(g.Name)) });
            var target = PowerTarget();
            if (target != null) l.Add(new Choice { Id = "power", Label = "Plan d'alimentation : « " + GameScan.SchemeName(target) + " »", On = true });
            if (Convert.ToString(GameScan.RegGet(GameRules.Slots["mspeed"]) ?? "1") != "0") l.Add(new Choice { Id = "mouse", Label = "Couper l'accélération de la souris (« Améliorer la précision du pointeur »)", On = true });
            return l;
        }

        // plan cible, ou null si on ne touche à rien. AMD : toujours Équilibré (exigé par les X3D, conseillé par AMD).
        static string PowerTarget()
        {
            var cur = GameScan.PowerScheme(); if (cur.Length == 0) return null;
            var cpu = GameScan.Cpu(); bool laptop = GameScan.Laptop();
            if (GameRules.IsAmdCpu(cpu) || laptop) return cur == GameRules.Saver || GameRules.DualCcdX3D(cpu) && (cur == GameRules.HighPerf || cur == GameRules.Ultimate) ? GameRules.Balanced : null;
            if ((cur == GameRules.Balanced || cur == GameRules.Saver) && GameScan.HasScheme(GameRules.HighPerf)) return GameRules.HighPerf;
            return cur == GameRules.Saver ? GameRules.Balanced : null;
        }

        static List<string> Prev() { return GameRules.Unpack(Prefs.Ini.Get("GamePrev", "")); }
        static void SavePrev(List<string> l) { Prefs.Ini.Set("GamePrev", GameRules.Pack(l)); Prefs.Ini.Set("GamePrevDate", DateTime.Now.ToString("dd/MM/yyyy HH:mm")); Prefs.Save(); }

        // garde la PREMIÈRE valeur connue : après deux optimisations, l'annulation revient bien à l'état d'origine
        static void Remember(List<string> prev, string line)
        {
            var key = string.Join("|", line.Split('|').Take(line.StartsWith("POWER") ? 1 : 2));
            if (!prev.Any(p => p.StartsWith(key + "|") || p == key)) prev.Add(line);
        }

        static void SetSlot(List<string> prev, string slot, string value)
        {
            var s = GameRules.Slots[slot];
            Remember(prev, GameRules.PrevReg(slot, GameScan.RegGet(s)));
            GameScan.RegSet(s, value);
        }

        public static void Apply(List<Choice> sel, Action<string, string> log)
        {
            var on = new HashSet<string>(sel.Where(c => c.On).Select(c => c.Id));
            if (on.Contains("restore")) Restore.Point(Ed.Name + " - optimisation jeu", log);
            var prev = Prev(); bool reboot = false;
            try
            {
                if (on.Contains("gamemode")) { SetSlot(prev, "gamemode", "1"); log("  ✓ Mode Jeu activé", "ok"); }
                if (on.Contains("bgrec")) { SetSlot(prev, "bgrec", "0"); log("  ✓ enregistrement en arrière-plan coupé", "ok"); }
                if (on.Contains("hags")) { SetSlot(prev, "hags", "2"); reboot = true; log("  ✓ HAGS activé (pris en compte au redémarrage)", "ok"); }
                if (on.Contains("power"))
                {
                    var t = PowerTarget(); var cur = GameScan.PowerScheme();
                    if (t != null && cur.Length > 0)
                    {
                        Remember(prev, GameRules.PrevPower(cur));
                        int rc = Sh.Cmd(Proc.Sys("powercfg.exe"), "/setactive " + t, 15000);
                        log(rc == 0 ? "  ✓ plan d'alimentation « " + GameScan.SchemeName(t) + " »" : "  ✗ powercfg : code " + rc, rc == 0 ? "ok" : "bad");
                    }
                }
                if (on.Contains("mouse"))
                {
                    SetSlot(prev, "mspeed", "0"); SetSlot(prev, "mth1", "0"); SetSlot(prev, "mth2", "0");
                    try { SystemParametersInfo(SPI_SETMOUSE, 0, new[] { 0, 0, 0 }, SPIF_UPDATE); } catch { }
                    log("  ✓ accélération de la souris coupée", "ok");
                }
            }
            finally { SavePrev(prev); }
            log("Optimisation terminée. Tout est annulable (tuile « Tout annuler »)." + (reboot ? " REDÉMARRAGE conseillé." : ""), "ok");
        }

        public static void Undo(Action<string, string> log)
        {
            var ops = GameRules.ParsePrev(Prev());
            if (ops.Count == 0) { log("Rien à annuler : aucune optimisation jeu enregistrée sur ce PC.", "muted"); return; }
            int ko = 0; bool mouse = false;
            foreach (var o in ops)
            {
                try
                {
                    if (o.Kind == "REG") { GameScan.RegSet(GameRules.Slots[o.Slot], o.Value); mouse |= o.Slot.StartsWith("m"); log("  ✓ " + GameRules.Slots[o.Slot].Name + " → " + (o.Value ?? "valeur par défaut"), "ok"); }
                    else if (o.Kind == "POWER") { int rc = Sh.Cmd(Proc.Sys("powercfg.exe"), "/setactive " + o.Value, 15000); log("  " + (rc == 0 ? "✓" : "✗") + " plan « " + GameScan.SchemeName(o.Value) + " »", rc == 0 ? "ok" : "bad"); if (rc != 0) ko++; }
                    else if (o.Kind == "GPU")
                    {
                        using (var k = Registry.CurrentUser.CreateSubKey(GameRules.GpuPrefKey)) { if (o.Value == null) k.DeleteValue(o.Path, false); else k.SetValue(o.Path, o.Value, RegistryValueKind.String); }
                        log("  ✓ carte graphique de " + Path.GetFileName(o.Path) + " : choix de Windows", "ok");
                    }
                }
                catch (Exception ex) { ko++; log("  ✗ " + o.Kind + " : " + ex.Message, "bad"); }
            }
            if (mouse)
                try
                {
                    Func<string, int> v = s => { int n; return int.TryParse(Convert.ToString(GameScan.RegGet(GameRules.Slots[s]) ?? ""), out n) ? n : s == "mspeed" ? 1 : s == "mth1" ? 6 : 10; };
                    SystemParametersInfo(SPI_SETMOUSE, 0, new[] { v("mth1"), v("mth2"), v("mspeed") }, SPIF_UPDATE);
                }
                catch { }
            if (ko == 0) { Prefs.Ini.Remove("GamePrev"); Prefs.Ini.Remove("GamePrevDate"); Prefs.Save(); }
            log(ko == 0 ? "Réglages d'origine remis." : ko + " réglage(s) non restauré(s) : sauvegarde conservée, réessayer.", ko == 0 ? "ok" : "warn");
        }

        // forcer la carte dédiée pour un jeu (Paramètres > Système > Écran > Graphiques)
        public static void GpuFor(string exe, Action<string, string> log)
        {
            if (!GameRules.ValidExe(exe) || !File.Exists(exe)) { log("Fichier invalide : choisir le .exe du jeu.", "bad"); return; }
            var prev = Prev(); string old = null;
            using (var k = Registry.CurrentUser.CreateSubKey(GameRules.GpuPrefKey))
            {
                old = k.GetValue(exe) as string;
                Remember(prev, GameRules.PrevGpu(exe, old));
                var v = Regex.Replace(old ?? "", @"GpuPreference=\d;", "") + "GpuPreference=2;";
                if (!Regex.IsMatch(v, @"^[A-Za-z0-9=;]{0,120}$")) v = "GpuPreference=2;";
                k.SetValue(exe, v, RegistryValueKind.String);
            }
            SavePrev(prev);
            log("✓ " + Path.GetFileName(exe) + " utilisera la carte graphique haute performance (relancer le jeu).", "ok");
        }

        // ------------------------------------------------------------- session de jeu
        public class App { public string Name = "", Title = "", Path = "", Company = ""; public long Mem; public double Cpu; public int Class; public bool Window; public List<int> Pids = new List<int>(); }

        public static List<App> RunningApps(int sampleMs = 1000)
        {
            var me = Process.GetCurrentProcess(); int sess = me.SessionId, myId = me.Id; var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var procs = Process.GetProcesses().Where(p => { try { return p.SessionId == sess && p.Id != myId; } catch { return false; } }).ToList();
            var t0 = new Dictionary<int, TimeSpan>();
            foreach (var p in procs) try { t0[p.Id] = p.TotalProcessorTime; } catch { }
            Thread.Sleep(sampleMs);
            var l = new List<App>();
            foreach (var grp in procs.GroupBy(p => p.ProcessName.ToLowerInvariant()))
            {
                var a = new App { Name = grp.Key };
                foreach (var p in grp)
                {
                    try
                    {
                        p.Refresh(); a.Pids.Add(p.Id); a.Mem += p.WorkingSet64;
                        TimeSpan b; if (t0.TryGetValue(p.Id, out b)) a.Cpu += (p.TotalProcessorTime - b).TotalMilliseconds;
                        if (p.MainWindowHandle != IntPtr.Zero) { a.Window = true; if (a.Title.Length == 0) a.Title = p.MainWindowTitle; }
                        if (a.Path.Length == 0) a.Path = p.MainModule.FileName;
                    }
                    catch { }
                }
                try { if (a.Path.Length > 0) a.Company = FileVersionInfo.GetVersionInfo(a.Path).CompanyName ?? ""; } catch { }
                a.Cpu = a.Cpu / Math.Max(1, sampleMs) / Environment.ProcessorCount * 100;
                a.Class = GameRules.Classify(a.Name, a.Company, a.Path, win);
                if (a.Class == 0 || a.Class == 2 && !a.Window) continue;
                try { var d = FileVersionInfo.GetVersionInfo(a.Path).FileDescription; if (!string.IsNullOrWhiteSpace(d)) a.Title = d.Trim(); } catch { }
                if (a.Title.Length == 0) a.Title = a.Name;
                l.Add(a);
            }
            return l.OrderByDescending(a => a.Class == 1).ThenByDescending(a => a.Mem).ToList();
        }

        public static void Close(List<App> apps, Action<string, string> log, Func<bool> cancelled)
        {
            var saved = GameRules.Unpack(Prefs.Ini.Get("GameClosed", ""));
            foreach (var a in apps)
            {
                if (cancelled()) break;
                var ps = a.Pids.Select(id => { try { return Process.GetProcessById(id); } catch { return null; } }).Where(p => p != null).ToList();
                foreach (var p in ps) try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
                var until = DateTime.Now.AddSeconds(5);
                while (DateTime.Now < until && ps.Any(Alive)) Thread.Sleep(250);
                var left = ps.Where(Alive).ToList();
                if (left.Count > 0 && GameRules.ForceOk(a.Name)) { foreach (var p in left) try { p.Kill(); } catch { } Thread.Sleep(500); left = ps.Where(Alive).ToList(); }
                if (left.Count == 0)
                {
                    log("  ✓ fermé : " + a.Title + " (" + Fmt.Size(a.Mem) + " libérés)", "ok");
                    if (GameRules.ValidExe(a.Path) && !saved.Any(s => s.StartsWith(a.Name + "|"))) saved.Add(a.Name + "|" + a.Path);
                }
                else log("  ! " + a.Title + " reste ouvert (document non enregistré ?) : le fermer soi-même", "warn");
            }
            Prefs.Ini.Set("GameClosed", GameRules.Pack(saved)); Prefs.Save();
            log("Session de jeu prête. Après la partie : tuile « Rouvrir les applis ».", "ok");
        }
        static bool Alive(Process p) { try { return !p.HasExited; } catch { return false; } }

        // relance SANS les droits administrateur (via l'Explorateur, au nom de l'utilisateur connecté)
        public static void Reopen(Action<string, string> log)
        {
            var saved = GameRules.Unpack(Prefs.Ini.Get("GameClosed", ""));
            if (saved.Count == 0) { log("Aucune appli fermée par la session de jeu.", "muted"); return; }
            foreach (var s in saved)
            {
                var i = s.IndexOf('|'); if (i < 1) continue;
                var name = s.Substring(0, i); var path = s.Substring(i + 1);
                if (!GameRules.ValidExe(path) || !File.Exists(path)) { log("  ! introuvable : " + path, "warn"); continue; }
                if (Process.GetProcessesByName(name).Length > 0) { log("  · déjà ouverte : " + name, "muted"); continue; }
                try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + path + "\"") { UseShellExecute = false }); log("  ✓ relancée : " + Path.GetFileNameWithoutExtension(path), "ok"); }
                catch (Exception ex) { log("  ✗ " + name + " : " + ex.Message, "bad"); }
                Thread.Sleep(400);
            }
            Prefs.Ini.Remove("GameClosed"); Prefs.Save();
            log("Les applis du Microsoft Store (Teams, Téléphone…) se rouvrent parfois seulement depuis le menu Démarrer.", "muted");
        }

        // ------------------------------------------------------------- latence réseau
        public static void Latency(int seconds, Action<string, string> log, Func<bool> cancelled)
        {
            var gw = ScanParc.Scanner.Gateway();
            var targets = new List<string>(); if (gw != null) targets.Add(gw.ToString()); targets.Add("1.1.1.1");
            var res = targets.ToDictionary(t => t, t => new List<long>()); var lost = targets.ToDictionary(t => t, t => 0);
            log("Mesure pendant " + seconds + " s (ne pas lancer de téléchargement)…", "muted");
            for (int i = 0; i < seconds && !cancelled(); i++)
            {
                foreach (var t in targets)
                    try { using (var p = new Ping()) { var r = p.Send(t, 1000); if (r.Status == IPStatus.Success) res[t].Add(r.RoundtripTime); else lost[t]++; } }
                    catch { lost[t]++; }
                Thread.Sleep(Math.Max(0, 1000 - 50 * targets.Count));
            }
            foreach (var t in targets)
            {
                var v = res[t]; int n = v.Count + lost[t]; if (n == 0) continue;
                double avg = v.Count > 0 ? v.Average() : 0; double jit = v.Count > 1 ? v.Zip(v.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0; int loss = lost[t] * 100 / n;
                bool local = gw != null && t == gw.ToString();
                string lvl = loss > 2 ? "bad" : loss > 0 || jit > (local ? 5 : 10) || avg > (local ? 10 : 60) ? "warn" : "ok";
                log("  " + (local ? "box " : "Internet ") + t + " : " + avg.ToString("0") + " ms en moyenne, max " + (v.Count > 0 ? v.Max() : 0) + " ms, gigue " + jit.ToString("0.0") + " ms, pertes " + loss + " %", lvl);
            }
            bool wifi = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.GetIPProperties().GatewayAddresses.Count > 0);
            log(wifi ? "→ PC en Wi-Fi : gigue et pertes viennent souvent du Wi-Fi ; un câble (ou un répéteur CPL de qualité) règle la plupart des à-coups." : "→ PC en Ethernet : si la box est bonne mais Internet mauvais, c'est l'accès opérateur (heure de pointe, ligne).", "muted");
        }

        // ------------------------------------------------------------- composants et outils (winget, paquets officiels vérifiés par empreinte)
        public class ToolDef { public string Name, Use, Winget, Url; }
        public static readonly ToolDef[] Tools =
        {
            new ToolDef { Name = "HWiNFO", Use = "températures, fréquences et capteurs (surveiller pendant le jeu)", Winget = "REALiX.HWiNFO", Url = "https://www.hwinfo.com/" },
            new ToolDef { Name = "MSI Afterburner", Use = "overclocking / sous-tension de la carte graphique, courbe des ventilateurs", Winget = "Guru3D.Afterburner", Url = "https://www.msi.com/Landing/afterburner/graphics-cards" },
            new ToolDef { Name = "RivaTuner Statistics Server", Use = "compteur de FPS à l'écran et limiteur d'images", Winget = "Guru3D.RTSS", Url = "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/" },
            new ToolDef { Name = "CPU-Z", Use = "processeur et mémoire (double canal, vitesse XMP/EXPO)", Winget = "CPUID.CPU-Z", Url = "https://www.cpuid.com/softwares/cpu-z.html" },
            new ToolDef { Name = "GPU-Z", Use = "carte graphique, lien PCIe, capteurs", Winget = "TechPowerUp.GPU-Z", Url = "https://www.techpowerup.com/gpuz/" },
            new ToolDef { Name = "DDU (Display Driver Uninstaller)", Use = "désinstallation propre du pilote graphique (en mode sans échec)", Winget = "Wagnardsoft.DisplayDriverUninstaller", Url = "https://www.wagnardsoft.com/" },
            new ToolDef { Name = "CapFrameX", Use = "mesure des FPS et du 1 % low (comparer avant / après)", Winget = "CXWorld.CapFrameX", Url = "https://www.capframex.com/" },
            new ToolDef { Name = "Intel PresentMon", Use = "latence d'affichage et temps d'image, toutes cartes", Winget = "Intel.PresentMon", Url = "https://game.intel.com/us/intel-presentmon/" },
            new ToolDef { Name = "OCCT", Use = "test de stabilité processeur, carte graphique, mémoire (après overclocking)", Winget = "OCBase.OCCT.Personal", Url = "https://www.ocbase.com/" },
            new ToolDef { Name = "CrystalDiskInfo", Use = "santé des SSD et disques durs", Winget = "CrystalDewWorld.CrystalDiskInfo", Url = "https://crystalmark.info/" },
            new ToolDef { Name = "Intel XTU", Use = "overclocking officiel des processeurs Intel débloqués (K)", Winget = "Intel.IntelExtremeTuningUtility", Url = "https://www.intel.com/content/www/us/en/download/17881/intel-extreme-tuning-utility-intel-xtu.html" },
            new ToolDef { Name = "Intel Driver & Support Assistant", Use = "pilotes Intel (graphique intégré, Arc, Wi-Fi, chipset)", Winget = "Intel.IntelDriverAndSupportAssistant", Url = "https://www.intel.fr/content/www/fr/fr/support/detect.html" },
            new ToolDef { Name = "NVIDIA App", Use = "pilotes Game Ready, réglages et superposition NVIDIA", Winget = null, Url = "https://www.nvidia.com/fr-fr/software/nvidia-app/" },
            new ToolDef { Name = "AMD Software Adrenalin", Use = "pilotes Radeon et chipset Ryzen, réglages AMD", Winget = null, Url = "https://www.amd.com/fr/support/graphics" },
            new ToolDef { Name = "AMD Ryzen Master", Use = "réglages officiels des processeurs Ryzen (PBO, Curve Optimizer)", Winget = null, Url = "https://www.amd.com/en/products/software/ryzen-master.html" },
        };

        public static int Winget(string id, Action<string, string> log)
        {
            if (!Regex.IsMatch(id ?? "", @"^[A-Za-z0-9][A-Za-z0-9.+\-]{1,80}$")) { log("Identifiant winget invalide.", "bad"); return -1; }
            log("  winget install " + id + "…", "muted");
            int rc;
            try { rc = Sh.Cmd("winget", "install --id " + id + " -e --silent --source winget --accept-package-agreements --accept-source-agreements", 1800000, null, Encoding.UTF8); }
            catch (Exception ex) { log("  ✗ winget introuvable (installer « Programme d'installation d'application » depuis le Microsoft Store) : " + ex.Message, "bad"); return -1; }
            bool ok = rc == 0 || rc == unchecked((int)0x8A15002B) || rc == unchecked((int)0x8A150061);
            log(ok ? "  ✓ " + id + " installé (ou déjà à jour)" : "  ✗ " + id + " : code winget 0x" + rc.ToString("X8"), ok ? "ok" : "bad");
            return ok ? 0 : rc;
        }

        public static void Components(bool install, Action<string, string> log)
        {
            var v64 = GameScan.VcRedist(true); var v86 = GameScan.VcRedist(false); bool dx = GameScan.DxLegacy();
            log("  Visual C++ 2015-2022 x64 : " + (v64 ?? "ABSENT"), v64 != null ? "ok" : "warn");
            log("  Visual C++ 2015-2022 x86 : " + (v86 ?? "ABSENT"), v86 != null ? "ok" : "warn");
            log("  DirectX ancien runtime (d3dx9, jeux d'avant 2012) : " + (dx ? "présent" : "absent"), dx ? "ok" : "muted");
            if (!install) return;
            if (v64 == null) Winget("Microsoft.VCRedist.2015+.x64", log);
            if (v86 == null) Winget("Microsoft.VCRedist.2015+.x86", log);
            if (!dx) Winget("Microsoft.DirectX", log);
            if (v64 != null && v86 != null && dx) log("Tout est déjà installé.", "ok");
        }

        // ------------------------------------------------------------- pilotes et overclocking (lecture, conseils)
        public static void Drivers(Action<string, string> log)
        {
            foreach (var g in GameScan.Gpus())
            {
                var ver = g.Vendor == "NVIDIA" ? GameRules.NvidiaVersion(g.Driver) : "";
                log("  " + g.Name + " (" + (g.Kind == "dgpu" ? "carte dédiée" : g.Kind == "igpu" ? "intégrée" : "virtuelle") + ") · pilote " + (ver.Length > 0 ? ver + " (" + g.Driver + ")" : g.Driver) + (g.AgeDays >= 0 ? " · " + g.Date.ToString("dd/MM/yyyy") + " (" + g.AgeDays + " j)" : ""), g.AgeDays > 365 ? "bad" : g.Kind == "dgpu" && g.AgeDays > 120 ? "warn" : "ok");
                if (g.Kind == "virtuel") continue;
                log("    outil officiel : " + (g.Vendor == "NVIDIA" ? "NVIDIA App" : g.Vendor == "AMD" ? "AMD Software Adrenalin" : g.Vendor == "Intel" ? "Intel Driver & Support Assistant" : "site du fabricant") + " (tuile « Site du pilote »)", "muted");
            }
            var smi = GameScan.NvidiaSmi();
            if (!string.IsNullOrEmpty(smi))
                foreach (var line in smi.Split('\n').Where(x => x.Trim().Length > 0))
                {
                    var p = line.Split(',').Select(x => x.Trim()).ToArray();
                    if (p.Length < 9) continue;
                    log("    NVIDIA : " + p[2] + " °C · PCIe gen " + p[3] + "/" + p[4] + " · x" + p[5] + "/x" + p[6] + " · " + p[7] + " W / " + p[8] + " W", "text");
                    int wc, wm; if (int.TryParse(p[5], out wc) && int.TryParse(p[6], out wm) && wc < wm) log("    ! lien PCIe x" + wc + " au lieu de x" + wm + " : carte mal enfoncée ou mauvais emplacement (au repos, la génération PCIe baisse : c'est normal, pas la largeur)", "warn");
                }
            var err = GameScan.DevicesInError();
            log(err.Count == 0 ? "  ✓ aucun périphérique en erreur" : "  ! périphériques en erreur : " + string.Join(" ; ", err), err.Count == 0 ? "ok" : "warn");
            log("  Jamais de « logiciel de mise à jour de pilotes » tiers (Driver Booster…) : pilotes du fabricant ou Windows Update uniquement.", "muted");
        }

        public static void Overclock(Action<string, string> log)
        {
            bool laptop = GameScan.Laptop(); var cpu = GameScan.Cpu(); var board = GameScan.Board();
            int mods, type, speed, conf; long mb; GameScan.Ram(out mods, out mb, out type, out speed, out conf);
            string v, h; int c = GameRules.CpuOc(cpu, board, laptop, out v, out h);
            log("Ce rapport ne modifie rien : " + Ed.Name + " n'applique jamais d'overclocking.", "muted");
            log("PROCESSEUR : " + cpu + (board.Length > 0 ? " · carte mère " + board + (GameRules.Chipset(board).Length > 0 ? " (" + GameRules.Chipset(board) + ")" : "") : ""), "accent");
            log("  " + (c == 2 ? "✓ possible" : c == 1 ? "~ limité" : "✗ non") + " : " + v, c == 2 ? "ok" : c == 1 ? "warn" : "muted");
            if (h.Length > 0) log("  comment : " + h, "text");
            if (c > 0) log("  gain réaliste en jeu : faible (quelques %) sur un processeur récent ; la sous-tension apporte souvent plus de silence que de FPS", "muted");
            log("MÉMOIRE : " + mods + " module(s) " + GameRules.RamTypeName(type) + (conf > 0 ? " à " + conf + " MT/s" : "") + (speed > 0 ? " (annoncé " + speed + ")" : ""), "accent");
            string rc, rk; var rl = GameRules.RamCheck(mods, type, speed, conf, laptop, out rc, out rk);
            log("  " + rc, rl == "ok" ? "ok" : rl == "info" ? "text" : "warn");
            if (rk.Length > 0) log("  comment : " + rk, "text");
            log("  le profil XMP/EXPO est l'« overclocking » le plus rentable et le plus sûr : réglage du constructeur des barrettes, annulable dans le BIOS", "muted");
            foreach (var g in GameScan.Gpus().Where(x => x.Kind == "dgpu"))
            {
                log("CARTE GRAPHIQUE : " + g.Name, "accent");
                if (laptop) log("  ~ limité : puissance fixée par le fabricant du portable ; utiliser son mode performances", "warn");
                else log("  ✓ possible avec l'outil officiel : " + (g.Vendor == "AMD" ? "AMD Software Adrenalin > Performances > Réglage" : "MSI Afterburner (toutes marques) ou NVIDIA App > Système > Performances") + " ; gain réaliste 3 à 8 %, sous-tension = moins de bruit", "ok");
            }
            log("RÈGLES DE SÉCURITÉ", "accent");
            log("  1. Jamais sur le PC d'un client sans son accord écrit : AMD et Intel excluent de la garantie les dommages hors spécifications.", "text");
            log("  2. Par petits pas, un seul réglage à la fois ; après chaque pas : OCCT 30 min + une vraie partie, températures surveillées (HWiNFO).", "text");
            log("  3. Au moindre plantage, écran noir ou erreur : revenir au pas précédent (BIOS : « Load Optimized Defaults » remet tout d'origine).", "text");
        }
    }

    // point de restauration partagé par la page Jeu et les scénarios
    static class Restore
    {
        public static bool Point(string description, Action<string, string> log)
        {
            string o; int rc = Sh.Ps("Enable-ComputerRestore -Drive ($env:SystemDrive+'\\') -ErrorAction SilentlyContinue;New-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' -Name SystemRestorePointCreationFrequency -Value 0 -PropertyType DWord -Force|Out-Null;Checkpoint-Computer -Description " + Sh.Q(description) + " -RestorePointType MODIFY_SETTINGS", 600000, null, out o);
            log(rc == 0 ? "  ✓ point de restauration créé" : "  ! point de restauration impossible (protection système désactivée ?)", rc == 0 ? "ok" : "warn");
            return rc == 0;
        }
    }

    // ===================================================================== Page Jeu
    class JeuPage : HubPage
    {
        public override string Title { get { return "Jeu"; } }

        public JeuPage() : base("JEU", "Optimiser le PC pour les jeux. Chaque réglage est annulable ; aucun overclocking n'est appliqué.", 356)
        {
            T(Theme.Icon("\uE7FC", "▶"), "Analyse jeu", "note /100 et conseils", t => t.Accent, () => { var f = GameScan.Analyze(Out, true); GameScan.Print(f, Out); }, true, null, "jeu");
            T(Theme.Icon("\uEC4A", "⚡"), "Optimiser", "réglages sûrs, annulables", t => t.Ok, OptimDialog, false);
            T(Theme.Icon("\uE7A7", "↶"), "Tout annuler", "remet les réglages d'origine", t => t.Warn, () => GameOpt.Undo(Out), true, "Remettre les réglages d'avant les optimisations jeu ?", "jeu");
            T(Theme.Icon("\uE768", "▷"), "Session de jeu", "ferme les applis inutiles", t => t.Ok, SessionDialog, false);
            T(Theme.Icon("\uE72C", "↻"), "Rouvrir les applis", "après la partie", t => t.Accent, () => GameOpt.Reopen(Out), true, null, "jeu");
            T(Theme.Icon("\uE7F4", "▭"), "Écran : fréquence max", "essai 15 s, retour auto", t => t.Accent2, RefreshDialog, false);
            T(Theme.Icon("\uE90F", "⚒"), "Pilotes", "âge, Windows Update", t => t.Accent, DriversJob, false);
            T(Theme.Icon("\uE774", "⌂"), "Site du pilote", "outil officiel du fabricant", t => t.Muted, VendorSite, false);
            T(Theme.Icon("\uE945", "↯"), "Overclocking", "possible ? comment ?", t => t.Accent2, () => GameOpt.Overclock(Out));
            T(Theme.Icon("\uE8F1", "▤"), "Bibliothèque de jeux", "Steam, Epic : SSD ou disque dur", t => t.Accent, LibraryJob, false);
            T(Theme.Icon("\uE7F8", "▣"), "GPU par jeu", "forcer la carte dédiée", t => t.Accent, GpuForGame, false);
            T(Theme.Icon("\uE839", "≋"), "Latence (30 s)", "ping, gigue, pertes", t => t.Accent, () => { var j = Job.Current; GameOpt.Latency(30, Out, () => j != null && j.Cts.IsCancellationRequested); }, true, null, "jeu-lat");
            T(Theme.Icon("\uE7B8", "▦"), "Composants des jeux", "Visual C++, DirectX", t => t.Accent, ComponentsJob, false);
            T(Theme.Icon("\uE71D", "☰"), "Outils pour les jeux", "HWiNFO, DDU, OCCT…", t => t.Muted, ToolsDialog, false);
            T(Theme.Icon("\uE73E", "✓"), "Prêt à jouer", "scénario complet enchaîné", t => t.Ok, () => { using (var sf = new ScenarioForm(Shell, "Prêt à jouer")) sf.ShowDialog(this); }, false);
        }

        public override void OnShow()
        {
            if (GameOpt.HasPrev) sub.Text = "Optimisations jeu appliquées le " + Prefs.Ini.Get("GamePrevDate", "?") + " · annulables à tout moment.";
        }

        Form Dlg(string title, int w, int h) { return new Form { Text = title + " · by alex.b", Size = new Size(k(w), k(h)), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Current.Bg, ShowInTaskbar = false, Font = new Font("Segoe UI", 9.5f) }; }

        void OptimDialog()
        {
            box.Line("Lecture des réglages actuels…", "muted");
            System.Threading.Tasks.Task.Run(() =>
            {
                List<GameOpt.Choice> ch; try { ch = GameOpt.Choices(); } catch (Exception ex) { Out("Lecture impossible : " + ex.Message, "bad"); return; }
                Ui(() =>
                {
                    if (ch.Count == 1) { Out("✓ Rien à optimiser : les réglages Windows pour le jeu sont déjà bons. Voir aussi Écran, Pilotes et Session de jeu.", "ok"); return; }
                    using (var f = Dlg("Optimiser pour le jeu", 720, 380))
                    {
                        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10f) };
                        foreach (var c in ch) list.Items.Add(c.Label, c.On);
                        var info = new Label { Dock = DockStyle.Top, Height = k(44), Tag = "muted", Text = "  Seuls les réglages qui changent quelque chose sur ce PC sont proposés. Les valeurs d'origine sont gardées :\n  « Tout annuler » les remet. Rien n'est désactivé côté sécurité." };
                        var go = new Chip("✓ Appliquer la sélection") { AccentOf = t => t.Ok, Dock = DockStyle.Bottom, Primary = true };
                        go.Click += (s, e) =>
                        {
                            for (int i = 0; i < ch.Count; i++) ch[i].On = list.GetItemChecked(i);
                            if (!ch.Skip(1).Any(c => c.On)) return;
                            f.Close(); Start("Optimisation jeu", () => GameOpt.Apply(ch, Out), "jeu");
                        };
                        f.Controls.Add(list); f.Controls.Add(info); f.Controls.Add(go); CyberForm.Recolor(f); f.ShowDialog(this);
                    }
                });
            });
        }

        void SessionDialog()
        {
            box.Line("Recherche des applis ouvertes…", "muted"); UseWaitCursor = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                List<GameOpt.App> apps; try { apps = GameOpt.RunningApps(); } catch (Exception ex) { Ui(() => UseWaitCursor = false); Out("Lecture impossible : " + ex.Message, "bad"); return; }
                Ui(() =>
                {
                    UseWaitCursor = false;
                    if (apps.Count == 0) { Out("✓ Aucune appli inutile ouverte : prêt à jouer.", "ok"); return; }
                    var g = new Grid { CheckBoxes = true, Dock = DockStyle.Fill }; g.Col("Application", 300).Col("Mémoire", 90).Col("Processeur", 90).Col("Conseil", 260);
                    foreach (var a in apps) g.Items.Add(new ListViewItem(new[] { a.Title, Fmt.Size(a.Mem), a.Cpu.ToString("0.0") + " %", a.Class == 1 ? "inutile pendant une partie" : a.Name == "discord" || a.Name == "spotify" ? "souvent gardé en jeu" : "à toi de voir" }) { Tag = a, Checked = a.Class == 1, ForeColor = a.Class == 1 ? Theme.Current.Text : Theme.Current.Muted });
                    using (var f = Dlg("Session de jeu", 820, 520))
                    {
                        var info = new Label { Dock = DockStyle.Top, Height = k(44), Tag = "muted", Text = "  Fermeture propre (comme la croix). Une appli avec un document non enregistré reste ouverte.\n  Jamais fermés : Windows, antivirus, pilotes, lanceurs de jeux, anti-triche, assistance à distance." };
                        var go = new Chip("▷ Fermer la sélection") { AccentOf = t => t.Ok, Dock = DockStyle.Bottom, Primary = true };
                        go.Click += (s, e) =>
                        {
                            var sel = g.CheckedItems.Cast<ListViewItem>().Select(i => (GameOpt.App)i.Tag).ToList(); if (sel.Count == 0) return;
                            f.Close(); Start("Session de jeu", () => { var j = Job.Current; GameOpt.Close(sel, Out, () => j != null && j.Cts.IsCancellationRequested); }, "jeu");
                        };
                        f.Controls.Add(g); f.Controls.Add(info); f.Controls.Add(go); CyberForm.Recolor(f); f.ShowDialog(this);
                    }
                });
            });
        }

        void RefreshDialog()
        {
            var ds = GameScan.Displays();
            if (ds.Count == 0) { Out("Écrans illisibles sur ce PC.", "warn"); return; }
            var todo = ds.Where(d => d.MaxHz >= d.Hz + 10).ToList();
            foreach (var d in ds) Out("  " + d.Device.Replace(@"\\.\", "") + " (" + d.Adapter + ") : " + d.W + "×" + d.H + " à " + d.Hz + " Hz, maximum " + d.MaxHz + " Hz", todo.Contains(d) ? "warn" : "ok");
            if (todo.Count == 0) { Out("✓ Tous les écrans sont à leur fréquence maximale.", "ok"); return; }
            foreach (var d in todo)
            {
                if (!CyberForm.Ask(this, "Passer " + d.Device.Replace(@"\\.\", "") + " de " + d.Hz + " Hz à " + d.MaxHz + " Hz ?\n\nSi l'image disparaît, rien à faire : retour automatique à " + d.Hz + " Hz au bout de 15 secondes.")) continue;
                var err = GameScan.SetHz(d.Device, d.MaxHz);
                if (err != null) { Out("  ✗ " + d.Device + " : " + err, "bad"); continue; }
                if (Confirm15s(d.MaxHz)) Out("  ✓ " + d.Device.Replace(@"\\.\", "") + " réglé à " + d.MaxHz + " Hz", "ok");
                else { var back = GameScan.SetHz(d.Device, d.Hz); Out(back == null ? "  ↶ retour à " + d.Hz + " Hz" : "  ✗ retour impossible : " + back + " (Paramètres > Écran > Écran avancé)", back == null ? "warn" : "bad"); }
            }
        }

        bool Confirm15s(int hz)
        {
            bool keep = false; int left = 15;
            using (var f = Dlg("Nouvelle fréquence", 460, 190))
            {
                f.TopMost = true;
                var l = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Tag = "text", Font = new Font("Segoe UI", 11f) };
                Action upd = () => l.Text = "L'image est correcte à " + hz + " Hz ?\nRetour automatique dans " + left + " s.";
                upd();
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = k(46), Padding = new Padding(k(8)) };
                var ok = new Chip("Garder") { AccentOf = t => t.Ok, Primary = true }; ok.Click += (s, e) => { keep = true; f.Close(); };
                var no = new Chip("Revenir") { AccentOf = t => t.Warn }; no.Click += (s, e) => f.Close();
                bar.Controls.Add(ok); bar.Controls.Add(no);
                var tm = new System.Windows.Forms.Timer { Interval = 1000 };
                tm.Tick += (s, e) => { left--; if (left <= 0) { tm.Stop(); f.Close(); } else upd(); };
                f.Shown += (s, e) => tm.Start(); f.FormClosed += (s, e) => tm.Dispose();
                f.Controls.Add(l); f.Controls.Add(bar); CyberForm.Recolor(f); f.ShowDialog(this);
            }
            return keep;
        }

        void DriversJob()
        {
            Start("Pilotes (jeu)", () =>
            {
                GameOpt.Drivers(Out);
                Out("  … pilotes proposés par Windows Update (jusqu'à 2 min)", "muted");
                string err; var ups = Optim.SearchUpdates(out err);
                if (err != null) { Out("  Windows Update : recherche impossible : " + err, "warn"); return; }
                var drv = ups.Where(u => u.Type == 2).ToList();
                if (drv.Count == 0) { Out("  ✓ aucun pilote en attente dans Windows Update", "ok"); return; }
                Ui(() =>
                {
                    var g = new Grid { CheckBoxes = true, Dock = DockStyle.Fill }; g.Col("Pilote", 600).Col("Taille", 90);
                    foreach (var u in drv) g.Items.Add(new ListViewItem(new[] { u.Title, u.Mb + " Mo" }) { Tag = u, Checked = true });
                    using (var f = Dlg("Pilotes Windows Update", 780, 420))
                    {
                        var b = new Chip("✓ Installer la sélection") { AccentOf = t => t.Ok, Dock = DockStyle.Bottom, Primary = true };
                        b.Click += (s, e) => { var sel = g.CheckedItems.Cast<ListViewItem>().Select(i => (Upd)i.Tag).ToList(); f.Close(); if (sel.Count > 0) Start("Installation de " + sel.Count + " pilote(s)", () => Optim.InstallUpdates(sel, Out), "servicing"); };
                        f.Controls.Add(g); f.Controls.Add(b); CyberForm.Recolor(f); f.ShowDialog(this);
                    }
                });
            }, "jeu");
        }

        void VendorSite()
        {
            var g = GameScan.Gpus().Where(x => x.Kind != "virtuel").OrderBy(x => x.Kind == "dgpu" ? 0 : 1).FirstOrDefault();
            var vendor = g != null ? g.Vendor : "";
            var t = GameOpt.Tools.FirstOrDefault(x => vendor == "NVIDIA" ? x.Name == "NVIDIA App" : vendor == "AMD" ? x.Name.StartsWith("AMD Software") : vendor == "Intel" ? x.Name.StartsWith("Intel Driver") : false);
            if (t == null) { Out("Carte graphique non reconnue : pilote sur le site du fabricant du PC ou de la carte.", "warn"); return; }
            OpenUrl(t.Url); Out("Ouvert : " + t.Name + " (" + t.Url + ")", "ok");
        }

        void OpenUrl(string url)
        {
            if (!Regex.IsMatch(url ?? "", @"^https://[A-Za-z0-9.\-]+/")) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Out("Navigateur : " + ex.Message, "bad"); }
        }

        void LibraryJob()
        {
            Start("Bibliothèque de jeux", () =>
            {
                var l = GameScan.Library();
                if (l.Count == 0) { Out("Aucun jeu Steam ou Epic trouvé sur ce PC.", "muted"); return; }
                foreach (var d in l.GroupBy(x => x.Drive)) Out("  " + d.Key + ": (" + d.First().Media + ") : " + d.Count() + " jeu(x), " + Fmt.Size(d.Sum(x => x.Size)), d.First().Media == "HDD" ? "warn" : "ok");
                Ui(() =>
                {
                    var g = new Grid { Dock = DockStyle.Fill }; g.Col("Jeu", 320).Col("Lanceur", 70).Col("Taille", 90).Col("Disque", 70).Col("Dossier", 300);
                    foreach (var x in l) g.Items.Add(new ListViewItem(new[] { x.Name, x.Launcher, x.Size > 0 ? Fmt.Size(x.Size) : "?", x.Drive + ": " + x.Media, x.Dir }) { ForeColor = x.Media == "HDD" ? Theme.Current.Warn : Theme.Current.Text });
                    using (var f = Dlg("Bibliothèque de jeux", 920, 520))
                    {
                        var info = new Label { Dock = DockStyle.Top, Height = k(30), Tag = "muted", Text = "  En orange : jeu sur disque dur classique → le déplacer sur un SSD (Steam : Paramètres > Stockage ; Epic : désinstaller puis réinstaller sur le SSD)." };
                        f.Controls.Add(g); f.Controls.Add(info); CyberForm.Recolor(f); f.ShowDialog(this);
                    }
                });
            }, "jeu");
        }

        void GpuForGame()
        {
            var gpus = GameScan.Gpus().Where(x => x.Kind != "virtuel").ToList();
            if (gpus.Count < 2 || !gpus.Any(x => x.Kind == "dgpu")) { Out("Un seul processeur graphique sur ce PC : réglage inutile.", "muted"); return; }
            using (var d = new OpenFileDialog { Title = "Choisir le programme (.exe) du jeu", Filter = "Programme du jeu (*.exe)|*.exe", CheckFileExists = true })
            {
                var steam = @"C:\Program Files (x86)\Steam\steamapps\common"; if (Directory.Exists(steam)) d.InitialDirectory = steam;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { GameOpt.GpuFor(d.FileName, Out); } catch (Exception ex) { Out("Réglage impossible : " + ex.Message, "bad"); }
            }
        }

        void ComponentsJob()
        {
            Start("Composants des jeux", () =>
            {
                GameOpt.Components(false, Out);
                bool missing = GameScan.VcRedist(true) == null || GameScan.VcRedist(false) == null || !GameScan.DxLegacy();
                if (missing && AskUi("Installer les composants manquants avec winget (paquets officiels Microsoft) ?")) GameOpt.Components(true, Out);
            }, "jeu");
        }

        void ToolsDialog()
        {
            var g = new Grid { Dock = DockStyle.Fill }; g.Col("Outil", 230).Col("À quoi ça sert", 430).Col("Installation", 110);
            foreach (var t in GameOpt.Tools) g.Items.Add(new ListViewItem(new[] { t.Name, t.Use, t.Winget != null ? "winget" : "site officiel" }) { Tag = t });
            using (var f = Dlg("Outils pour les jeux", 860, 520))
            {
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = k(46), Padding = new Padding(k(6)) };
                var inst = new Chip("Installer (winget)") { AccentOf = t => t.Ok, Primary = true };
                inst.Click += (s, e) =>
                {
                    var t = g.Sel as GameOpt.ToolDef; if (t == null) return;
                    if (t.Winget == null) { OpenUrl(t.Url); return; }
                    if (!CyberForm.Ask(f, "Installer " + t.Name + " depuis le dépôt officiel winget (" + t.Winget + ") ?")) return;
                    f.Close(); Start("Installation de " + t.Name, () => GameOpt.Winget(t.Winget, Out));
                };
                var site = new Chip("Site officiel") { AccentOf = t => t.Accent }; site.Click += (s, e) => { var t = g.Sel as GameOpt.ToolDef; if (t != null) OpenUrl(t.Url); };
                bar.Controls.Add(inst); bar.Controls.Add(site);
                var info = new Label { Dock = DockStyle.Top, Height = k(30), Tag = "muted", Text = "  Uniquement des outils reconnus, installés depuis le dépôt winget de Microsoft (empreinte vérifiée) ou le site de l'éditeur." };
                f.Controls.Add(g); f.Controls.Add(info); f.Controls.Add(bar); CyberForm.Recolor(f); f.ShowDialog(this);
            }
        }
    }
}
