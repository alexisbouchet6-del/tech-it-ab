// AMI·OPS - Dépannage : écrans bleus (WinDbg), réparations express, démarrage & malwares, imprimantes, licence, test de surface
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
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using AlexB;

namespace AmiOps
{
    static class Bsod
    {
        static readonly string[][] Hints =
        {
            new[] { "^nvlddmkm", "carte graphique NVIDIA", "réinstaller proprement le pilote NVIDIA (DDU en mode sans échec), version stable précédente si le problème est récent" },
            new[] { "^(atikmdag|amdkmdag|atikmpag|amdkmpfd)", "carte graphique AMD", "réinstaller proprement le pilote AMD (DDU), version Adrenalin stable" },
            new[] { "^igd", "graphique Intel", "pilote graphique Intel du fabricant du PC (Dell / Lenovo / HP) plutôt que générique" },
            new[] { "^(dxgkrnl|dxgmms|win32k|watchdog)", "pile graphique Windows", "pilote graphique en cause le plus souvent : mettre à jour / réinstaller" },
            new[] { "^(rt640|rt68|rtwlan|rtux|rtkvhd|rtl)", "puce réseau / Wi-Fi Realtek", "mettre à jour le pilote réseau Realtek (site du fabricant du PC)" },
            new[] { "^(netwtw|netwbw|netwns|netwaw)", "Wi-Fi Intel", "pilote Wi-Fi Intel à jour (Intel Driver & Support Assistant ou fabricant)" },
            new[] { "^(e1d|e1r|e2f|e1i)", "carte réseau Intel", "mettre à jour le pilote Ethernet Intel" },
            new[] { "^(tcpip|ndis|netio|afd|wfplwfs|fwpkclnt)", "pile réseau Windows", "souvent un VPN, un antivirus ou un pilote réseau qui filtre le trafic : mettre à jour / désinstaller le VPN" },
            new[] { "^(iastor|iavroc|rstmw)", "Intel RST (stockage)", "mettre à jour Intel RST ; vérifier le mode SATA dans le BIOS" },
            new[] { "^(stornvme|storport|storahci|nvme|samsung|secnvme)", "pilote de stockage / SSD", "firmware du SSD à jour (outil du fabricant), test SMART ; DPC_WATCHDOG = souvent firmware" },
            new[] { "^(klif|kneps|klhk|klwfp|kl1|klflt|klbackup|klids|klgse|klpd)", "antivirus Kaspersky", "mettre à jour Kaspersky ; réinstaller l'agent si ça persiste (console Kaspersky Business Hub)" },
            new[] { "^(bd|trufos|gzflt|avc3|avckf)", "antivirus Bitdefender", "mettre à jour / réinstaller Bitdefender" },
            new[] { "^(asw|aswsp|aswarpot)", "antivirus Avast / AVG", "mettre à jour ou désinstaller (outil aswclear)" },
            new[] { "^(mfe|mfehidk)", "antivirus McAfee", "désinstaller McAfee préinstallé (MCPR) s'il n'est pas utilisé" },
            new[] { "^(sym|srtsp|ccsetx)", "antivirus Norton", "désinstaller Norton préinstallé (Norton Remove and Reinstall)" },
            new[] { "^(vmx86|vmnet|hcmon|vmci|vmusb)", "VMware", "mettre à jour VMware Workstation ; conflit possible avec Hyper-V / VBS (Outils VTx)" },
            new[] { "^(vboxdrv|vboxnet|vboxusb)", "VirtualBox", "mettre à jour VirtualBox" },
            new[] { "^(usbxhci|usbhub3|ucx01000|usbport)", "USB", "débrancher les périphériques USB / dock, mettre à jour le pilote du chipset et du dock" },
            new[] { "^(acpi|intelppm|amdppm|pci)", "BIOS / gestion d'énergie", "mettre à jour le BIOS et le chipset du fabricant" },
            new[] { "^(fltmgr)", "filtre de fichiers", "antivirus, sauvegarde ou chiffrement qui filtre le disque : mettre à jour" },
            new[] { "memory_corruption|^hardware", "mémoire / matériel", "test mémoire (mdsched / MemTest86), retirer l'overclock / XMP" },
            new[] { "^(ntoskrnl|ntkrnlmp|hal)", "noyau Windows", "pilote non identifiable : souvent RAM, chauffe ou pilote récent ; test mémoire + PanneScan" },
        };

        public static string Cdb()
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            { var p = Path.Combine(root, @"Windows Kits\10\Debuggers\x64\cdb.exe"); if (File.Exists(p)) return p; }
            try
            {
                var loc = Sh.Ps("(Get-AppxPackage -AllUsers -Name Microsoft.WinDbg -ErrorAction SilentlyContinue | sort Version -Descending | select -First 1).InstallLocation", 60000).Trim();
                foreach (var sub in new[] { @"amd64\cdb.exe", @"x64\cdb.exe", @"arm64\cdb.exe" }) { var p = Path.Combine(loc, sub); if (loc.Length > 0 && File.Exists(p)) return p; }
            }
            catch { }
            return null;
        }

        public static List<FileInfo> Dumps()
        {
            var win = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows"; var l = new List<FileInfo>();
            try { var d = Path.Combine(win, "Minidump"); if (Directory.Exists(d)) l.AddRange(new DirectoryInfo(d).GetFiles("*.dmp")); } catch { }
            try { var m = new FileInfo(Path.Combine(win, "MEMORY.DMP")); if (m.Exists) l.Add(m); } catch { }
            return l.OrderByDescending(f => f.LastWriteTime).ToList();
        }

        public static void Analyze(string dump, Action<string, string> log)
        {
            var cdb = Cdb();
            if (cdb == null) { log("Débogueur absent : clique « Installer WinDbg » (winget install Microsoft.WinDbg), puis relance.", "bad"); return; }
            var cache = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Symbols"); Directory.CreateDirectory(cache);
            var copy = Path.Combine(Path.GetTempPath(), "amiops-" + Path.GetFileName(dump));
            File.Copy(dump, copy, true);
            log("  analyse de " + Path.GetFileName(dump) + " (téléchargement des symboles Microsoft : 1 à 5 min la première fois)…", "muted");
            string o;
            int rc = Proc.Run(cdb, "-z \"" + copy + "\" -y \"srv*" + cache + "*https://msdl.microsoft.com/download/symbols\" -c \"!analyze -v; q\"", 900000, Encoding.UTF8, null, out o);
            try { File.Delete(copy); } catch { }
            Func<string, string> f = key => { var m = Regex.Match(o ?? "", "^" + key + @":\s*(.+)$", RegexOptions.Multiline); return m.Success ? m.Groups[1].Value.Trim() : ""; };
            var code = f("BUGCHECK_CODE"); var module = f("MODULE_NAME"); var image = f("IMAGE_NAME"); var bucket = f("FAILURE_BUCKET_ID"); var proc = f("PROCESS_NAME"); var ver = f("IMAGE_VERSION");
            if (image.Length == 0) { var m = Regex.Match(o ?? "", @"Probably caused by\s*:\s*(\S+)"); if (m.Success) image = m.Groups[1].Value; }
            if (code.Length == 0 && image.Length == 0) { log("  Analyse impossible (code " + rc + "). Vérifie l'accès Internet (symboles Microsoft).", "bad"); Log.Write(o); return; }
            uint c; uint.TryParse(code, System.Globalization.NumberStyles.HexNumber, null, out c);
            log("  Code d'arrêt : 0x" + (code.Length > 0 ? code.ToUpperInvariant() : "?") + " " + PanneScan.BugName(c), "bad");
            log("  PILOTE / MODULE EN CAUSE : " + (image.Length > 0 ? image : module) + (ver.Length > 0 ? " (version " + ver + ")" : ""), "bad");
            if (proc.Length > 0) log("  Processus au moment du plantage : " + proc, "text");
            if (bucket.Length > 0) log("  Signature : " + bucket, "muted");
            var name = (image.Length > 0 ? image : module).ToLowerInvariant().Replace(".sys", "");
            var drv = Path.Combine(Environment.SystemDirectory, "drivers", name + ".sys");
            if (File.Exists(drv)) try { var vi = FileVersionInfo.GetVersionInfo(drv); log("  Fichier : " + drv + " — " + vi.CompanyName + " " + vi.FileVersion + " (" + File.GetLastWriteTime(drv).ToString("dd/MM/yyyy") + ")", "text"); } catch { }
            var hint = Hints.FirstOrDefault(h => Regex.IsMatch(name + " " + bucket.ToLowerInvariant(), h[0]));
            if (hint != null) { log("  → Composant : " + hint[1], "warn"); log("  → À faire : " + hint[2], "ok"); }
            else log("  → À faire : rechercher « " + name + " » (éditeur ci-dessus), mettre à jour ou désinstaller le logiciel / pilote correspondant.", "ok");
        }
    }

    static class Repair
    {
        static string Win { get { return Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows"; } }
        static string Stamp { get { return DateTime.Now.ToString("yyyyMMdd-HHmmss"); } }

        public static void WindowsUpdate(Action<string, string> log)
        {
            foreach (var s in new[] { "wuauserv", "bits", "cryptsvc", "msiserver", "usosvc" }) { Sh.Service(s, false); log("  service arrêté : " + s, "muted"); }
            foreach (var d in new[] { Path.Combine(Win, "SoftwareDistribution"), Path.Combine(Win, @"System32\catroot2") })
                try { if (Directory.Exists(d)) { Directory.Move(d, d + ".old-" + Stamp); log("  renommé : " + d + " → .old-" + Stamp + " (à supprimer plus tard)", "ok"); } }
                catch (Exception ex) { log("  " + d + " : " + ex.Message, "warn"); }
            foreach (var s in new[] { "cryptsvc", "bits", "wuauserv", "usosvc" }) Sh.Service(s, true);
            Sh.Cmd(Proc.Sys("UsoClient.exe"), "StartScan", 30000);
            log("Windows Update réinitialisé, recherche relancée. Ouvre Paramètres > Windows Update pour suivre.", "ok");
        }

        public static void Spooler(Action<string, string> log)
        {
            Sh.Service("spooler", false);
            long n = 0; var d = Path.Combine(Win, @"System32\spool\PRINTERS");
            try { foreach (var f in Directory.GetFiles(d)) try { File.Delete(f); n++; } catch { } } catch { }
            Sh.Service("spooler", true);
            log("File d'impression vidée (" + n + " fichier(s)) et spouleur redémarré.", "ok");
        }

        public static void SearchIndex(Action<string, string> log)
        {
            Sh.Service("wsearch", false);
            using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows Search")) k.SetValue("SetupCompletedSuccessfully", 0, RegistryValueKind.DWord);
            Sh.Service("wsearch", true);
            log("Index de recherche en reconstruction (Outlook et Windows) : compter 30 min à quelques heures, PC allumé.", "ok");
        }

        public static void Network(bool full, Action<string, string> log)
        {
            Sh.Cmd(Proc.Sys("ipconfig.exe"), "/flushdns", 30000); log("  cache DNS vidé", "ok");
            if (!full)
            {
                Sh.Cmd(Proc.Sys("ipconfig.exe"), "/release", 60000); Sh.Cmd(Proc.Sys("ipconfig.exe"), "/renew", 90000);
                log("Adresse IP renouvelée.", "ok"); return;
            }
            Sh.Cmd(Proc.Sys("netsh.exe"), "winsock reset", 60000); log("  Winsock réinitialisé", "ok");
            Sh.Cmd(Proc.Sys("netsh.exe"), "int ip reset", 60000); log("  pile TCP/IP IPv4 réinitialisée", "ok");
            Sh.Cmd(Proc.Sys("netsh.exe"), "int ipv6 reset", 60000); log("  pile IPv6 réinitialisée", "ok");
            log("Réseau réinitialisé : REDÉMARRER le PC. Les IP fixes sont à ressaisir (NetSwitch).", "warn");
        }

        public static void Icons(Action<string, string> log)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer"); int n = 0;
            foreach (var pat in new[] { "iconcache_*.db", "thumbcache_*.db" }) try { foreach (var f in Directory.GetFiles(dir, pat)) try { File.Delete(f); n++; } catch { } } catch { }
            try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IconCache.db")); n++; } catch { }
            Sh.Cmd(Proc.Sys("ie4uinit.exe"), "-show", 30000);
            log(n + " fichier(s) de cache supprimé(s). Fichiers verrouillés : déconnexion / reconnexion de la session pour finir.", "ok");
        }

        public static void StartMenu(Action<string, string> log)
        {
            string o; int rc = Sh.Ps("foreach($n in 'Microsoft.Windows.StartMenuExperienceHost','Microsoft.Windows.ShellExperienceHost','MicrosoftWindows.Client.CBS'){Get-AppxPackage -Name $n -ErrorAction SilentlyContinue|%{Add-AppxPackage -DisableDevelopmentMode -Register ($_.InstallLocation+'\\AppXManifest.xml') -ErrorAction SilentlyContinue}};Get-Process StartMenuExperienceHost,ShellExperienceHost -ErrorAction SilentlyContinue|Stop-Process -Force", 300000, null, out o);
            log(rc == 0 ? "Menu Démarrer réenregistré et relancé." : "Réenregistrement partiel : " + o.Trim(), rc == 0 ? "ok" : "warn");
        }

        public static void Clock(Action<string, string> log)
        {
            bool domain = Sh.Ps("(Get-CimInstance Win32_ComputerSystem).PartOfDomain", 30000).Trim() == "True";
            Sh.Cmd(Proc.Sys("w32tm.exe"), domain ? "/config /syncfromflags:domhier /update" : "/config /syncfromflags:manual /manualpeerlist:\"time.windows.com,0x8 fr.pool.ntp.org,0x8\" /update", 30000);
            Sh.Service("w32time", false); Sh.Service("w32time", true);
            int rc = Sh.Cmd(Proc.Sys("w32tm.exe"), "/resync /force", 60000);
            log((domain ? "Poste du domaine : synchronisé sur le contrôleur de domaine. " : "Synchronisé sur time.windows.com / fr.pool.ntp.org. ") + (rc == 0 ? "Heure corrigée." : "Resynchronisation refusée (port UDP 123 bloqué ?)."), rc == 0 ? "ok" : "warn");
        }

        public static void DismSfc(Action<string, string> log)
        {
            log("  DISM /RestoreHealth (10 à 30 min)…", "muted");
            int rc = Sh.Cmd(Proc.Sys("dism.exe"), "/Online /Cleanup-Image /RestoreHealth", 3600000, l => { if (!Regex.IsMatch(l, @"\[[=\s]*\d+[.,]\d+%")) log("  " + l, "text"); });
            log("  DISM terminé (code " + rc + ")", rc == 0 ? "ok" : "warn");
            log("  SFC /scannow…", "muted");
            string o; Proc.Run(Proc.Sys("sfc.exe"), "/scannow", 3600000, Encoding.Unicode, null, out o);
            var t = (o ?? "").Replace("\0", "");
            var res = t.Contains("n'a trouvé aucune violation") || t.Contains("did not find any integrity") ? "aucune corruption" : t.Contains("a trouvé des fichiers endommagés et les a réparés") || t.Contains("successfully repaired") ? "fichiers corrompus RÉPARÉS" : t.Contains("certains d'entre eux n'ont pas pu") || t.Contains("unable to fix") ? "corruptions NON réparables (réparation de Windows conseillée)" : "voir " + Win + @"\Logs\CBS\CBS.log";
            log("SFC : " + res, res.StartsWith("aucune") || res.Contains("RÉPARÉS") ? "ok" : "bad");
        }

        // lance un programme SANS les droits admin (Outlook ne doit jamais tourner élevé)
        public static void AsUser(string exe, string args)
        {
            if (string.IsNullOrEmpty(exe)) throw new Exception("Outlook introuvable");
            Process.Start(Proc.Sys("runas.exe"), "/trustlevel:0x20000 \"\\\"" + exe + "\\\" " + args + "\"");
        }

        public static string OutlookExe()
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE")) { var p = Convert.ToString(k.GetValue("")); if (File.Exists(p)) return p; } } catch { }
            return null;
        }

        public static void License(Action<string, string> log)
        {
            string oem = ""; try { using (var s = new ManagementObjectSearcher("SELECT OA3xOriginalProductKey, OA3xOriginalProductKeyDescription FROM SoftwareLicensingService")) foreach (ManagementObject o in s.Get()) oem = Convert.ToString(o["OA3xOriginalProductKey"]) + "|" + Convert.ToString(o["OA3xOriginalProductKeyDescription"]); } catch { }
            var p = oem.Split('|');
            if (p[0].Length > 0) { log("Clé d'origine inscrite dans le BIOS (OEM) : " + p[0] + (p.Length > 1 && p[1].Length > 0 ? "  (" + p[1] + ")" : ""), "ok"); Clip.Private(p[0], 60); log("  copiée pour 60 s (hors historique Win+V)", "muted"); }
            else log("Aucune clé OEM dans le BIOS (PC assemblé, licence numérique liée au compte, ou licence en volume).", "warn");
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, Description, LicenseStatus, PartialProductKey, ApplicationID FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var app = Convert.ToString(o["ApplicationID"]).ToLowerInvariant(); var st = Convert.ToInt32(o["LicenseStatus"]);
                        var who = app == "55c92734-d682-4d71-983e-d6ec3f16059f" ? "Windows" : app == "0ff1ce15-a989-479d-af46-f275c6370663" ? "Office" : "Autre";
                        log(who + " : " + o["Name"] + " — clé installée …-" + o["PartialProductKey"] + " — " + (st == 1 ? "ACTIVÉ" : st == 0 ? "non licencié" : st == 5 ? "notification (NON activé)" : "statut " + st), st == 1 ? "ok" : "bad");
                        var desc = Convert.ToString(o["Description"]); if (desc.Length > 0) log("    " + desc, "muted");
                    }
            }
            catch (Exception ex) { log("Lecture des licences impossible : " + ex.Message, "warn"); }
        }
    }

    // ===================================================================== Test de surface (lecture seule)
    class SurfaceForm : Form
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tpl);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int toRead, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFilePointerEx(SafeFileHandle h, long dist, out long newPos, int method);

        readonly ComboBox disks; readonly ComboBox mode; readonly Panel map; readonly Label stat; readonly Chip bGo;
        const int Cells = 1200; readonly int[] cell = new int[Cells];   // 0 non lu, 1 rapide, 2 lent, 3 très lent, 4 illisible
        volatile bool stop; bool running;
        int nOk, nSlow, nVSlow, nBad; long total;

        public SurfaceForm()
        {
            Func<float, int> k = Theme.S;
            Text = "Test de surface du disque (lecture seule) · by alex.b"; Size = new Size(k(980), k(620)); StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Current.Bg; ShowInTaskbar = false; Font = new Font("Segoe UI", 9f);
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = k(46), Padding = new Padding(k(8)) };
            disks = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = k(480) };
            try { using (var s = new ManagementObjectSearcher("SELECT Index, Model, Size FROM Win32_DiskDrive")) foreach (ManagementObject o in s.Get()) disks.Items.Add("Disque " + o["Index"] + " — " + o["Model"] + " — " + AlexB.Fmt.Size(Convert.ToInt64(o["Size"] ?? 0L)) + "|" + o["Index"] + "|" + o["Size"]); } catch { }
            disks.Format += (s, e) => e.Value = ((string)e.ListItem).Split('|')[0];
            if (disks.Items.Count > 0) disks.SelectedIndex = 0;
            mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = k(240) }; mode.Items.AddRange(new object[] { "Rapide (1 zone sur 32, quelques min)", "Complet (tout le disque, long)" }); mode.SelectedIndex = 0;
            bGo = new Chip("▶ Lancer") { AccentOf = t => t.Ok };
            bGo.Click += (s, e) => { if (running) stop = true; else Go(); };
            top.Controls.AddRange(new Control[] { disks, mode, bGo });
            stat = new Label { Dock = DockStyle.Bottom, Height = k(48), Tag = "text", Padding = new Padding(k(8)), Text = "Lecture seule : aucune écriture sur le disque. Vert < 50 ms · jaune < 150 ms · orange < 500 ms · rouge = illisible ou > 500 ms. Lance-le sur un disque suspect AVANT de le cloner." };
            map = new Panel { Dock = DockStyle.Fill };
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(map, true, null);
            map.Paint += Draw;
            Controls.Add(map); Controls.Add(top); Controls.Add(stat);
            FormClosing += (s, e) => stop = true;
            CyberForm.Recolor(this);
        }

        void Draw(object sender, PaintEventArgs e)
        {
            var t = Theme.Current; var g = e.Graphics; g.Clear(t.Bg);
            int cols = 60, rows = Cells / cols; float cw = (map.Width - 20f) / cols, ch = Math.Min(22f, (map.Height - 20f) / rows);
            var cols5 = new[] { t.Surface2, t.Ok, t.Warn, Color.Orange, t.Bad };
            for (int i = 0; i < Cells; i++)
                using (var b = new SolidBrush(cols5[cell[i]])) g.FillRectangle(b, 10 + (i % cols) * cw, 10 + (i / cols) * ch, cw - 2, ch - 2);
        }

        void Go()
        {
            if (disks.SelectedItem == null) return;
            var p = ((string)disks.SelectedItem).Split('|'); int idx = int.Parse(p[1]); long size = long.Parse(p[2]);
            bool quick = mode.SelectedIndex == 0;
            Array.Clear(cell, 0, Cells); nOk = nSlow = nVSlow = nBad = 0; total = 0; stop = false; running = true; bGo.SetText("■ Arrêter"); map.Invalidate();
            Task.Run(() =>
            {
                const int Block = 1 << 20; var sw = Stopwatch.StartNew();
                var raw = Marshal.AllocHGlobal(Block + 4096); var buf = new IntPtr((raw.ToInt64() + 4095) & ~4095L);
                try
                {
                    using (var h = CreateFile(@"\\.\PhysicalDrive" + idx, 0x80000000, 0x1 | 0x2, IntPtr.Zero, 3, 0x20000000, IntPtr.Zero))
                    {
                        if (h.IsInvalid) { BeginInvoke(new Action(() => stat.Text = "Ouverture du disque impossible (droits admin ?) : erreur " + Marshal.GetLastWin32Error())); return; }
                        long blocks = size / Block; int step = quick ? 32 : 1;
                        for (long b = 0; b < blocks && !stop; b += step)
                        {
                            long np; SetFilePointerEx(h, b * Block, out np, 0);
                            var t0 = sw.ElapsedTicks; int read;
                            bool ok = ReadFile(h, buf, Block, out read, IntPtr.Zero) && read > 0;
                            double ms = (sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
                            int lvl = !ok ? 4 : ms < 50 ? 1 : ms < 150 ? 2 : ms < 500 ? 3 : 4;
                            if (lvl == 1) nOk++; else if (lvl == 2) nSlow++; else if (lvl == 3) nVSlow++; else nBad++;
                            int c = (int)(b * Cells / Math.Max(1, blocks)); if (c < Cells && lvl > cell[c]) cell[c] = lvl;
                            total++;
                            if (total % 64 == 0) { int pct = (int)(b * 100 / Math.Max(1, blocks)); BeginInvoke(new Action(() => { map.Invalidate(); stat.Text = pct + " % — " + nOk + " rapides · " + nSlow + " lents · " + nVSlow + " très lents · " + nBad + " illisibles (blocs de 1 Mo)"; })); }
                        }
                    }
                }
                catch (Exception ex) { BeginInvoke(new Action(() => stat.Text = "Erreur : " + ex.Message)); }
                finally { Marshal.FreeHGlobal(raw); }
                BeginInvoke(new Action(() =>
                {
                    running = false; bGo.SetText("▶ Lancer"); map.Invalidate();
                    var verdict = nBad > 0 ? "DISQUE DÉFAILLANT : " + nBad + " zone(s) illisible(s) ou > 500 ms → sauvegarder et remplacer" : nVSlow > 5 ? "disque fatigué : " + nVSlow + " zones très lentes → remplacement conseillé" : "surface correcte";
                    stat.Text = (stop ? "Arrêté. " : "Terminé. ") + verdict + "  (" + total + " blocs lus)";
                    stat.ForeColor = Theme.Current.ByName(nBad > 0 ? "bad" : nVSlow > 5 ? "warn" : "ok");
                    Log.Write("Test de surface disque " + idx + " : " + stat.Text);
                }));
            });
        }
    }

    // ===================================================================== Page Dépannage
    class DepannPage : HubPage
    {
        public override string Title { get { return "Dépannage"; } }

        public DepannPage() : base("DÉPANNAGE", "Réparations en un clic avec journal. Les actions lourdes demandent confirmation.", 356)
        {
            T(Theme.Icon("\uE7BA", "☠"), "Écrans bleus", "pilote en cause (WinDbg)", t => t.Bad, BsodDialog, false);
            T(Theme.Icon("\uE895", "↻"), "Windows Update bloqué", "réinitialise les composants", t => t.Warn, () => Repair.WindowsUpdate(Out), true, "Arrêter les services de mise à jour et réinitialiser Windows Update ?", "servicing");
            T(Theme.Icon("\uE749", "⎙"), "File d'impression", "vide la file, relance", t => t.Accent, () => Repair.Spooler(Out), true, "Vider TOUTES les impressions en attente ?", "spooler");
            T(Theme.Icon("\uE749", "⎙"), "Imprimantes…", "test, défaut, IP, suppression", t => t.Accent, () => new PrintersForm(Out).ShowDialog(this), false);
            T(Theme.Icon("\uE715", "✉"), "Outlook…", "sans échec, profils, OST, PST", t => t.Accent2, OutlookDialog, false);
            T(Theme.Icon("\uE721", "⌕"), "Recherche Windows", "reconstruit l'index", t => t.Accent, () => Repair.SearchIndex(Out), true, "Reconstruire l'index de recherche (Outlook et Windows) ? La recherche sera incomplète pendant quelques heures.", "search");
            T(Theme.Icon("\uE839", "⇄"), "Réseau (léger)", "DNS + renouvellement IP", t => t.Accent, () => Repair.Network(false, Out), true, "Vider le cache DNS et renouveler l'IP ? Coupure réseau de quelques secondes (ne pas lancer à distance).", "net");
            T(Theme.Icon("\uE839", "⇄"), "Réseau (complet)", "Winsock + TCP/IP", t => t.Warn, () => Repair.Network(true, Out), true, "Réinitialiser Winsock et TCP/IP ? Redémarrage nécessaire, IP fixes à ressaisir.", "net");
            T(Theme.Icon("\uE719", "▣"), "Microsoft Store", "wsreset", t => t.Muted, () => { Process.Start("explorer.exe", Proc.Sys("wsreset.exe")); Out("Cache du Store réinitialisé (la fenêtre du Store s'ouvre à la fin).", "ok"); }, false);
            T(Theme.Icon("\uE8B9", "▤"), "Icônes / miniatures", "caches reconstruits", t => t.Muted, () => Repair.Icons(Out));
            T(Theme.Icon("\uE700", "☰"), "Menu Démarrer", "réenregistre et relance", t => t.Muted, () => Repair.StartMenu(Out));
            T(Theme.Icon("\uE823", "◷"), "Horloge", "resynchronise l'heure", t => t.Muted, () => Repair.Clock(Out));
            T(Theme.Icon("\uE9F5", "✓"), "DISM + SFC", "répare les fichiers système", t => t.Warn, () => Repair.DismSfc(Out), true, "Lancer DISM /RestoreHealth puis SFC /scannow (20 à 45 min) ?", "servicing");
            T(Theme.Icon("\uE72E", "⛨"), "Démarrage & malwares", "signatures, VirusTotal, WMI", t => t.Bad, () => { var l = Startup.Scan(true, true); Ui(() => new StartupForm(l, Out, true).ShowDialog(this)); Out(l.Count + " élément(s) · " + l.Count(i => i.Suspicious) + " suspect(s)", l.Any(i => i.Suspicious) ? "warn" : "ok"); });
            T(Theme.Icon("\uE8D7", "⚿"), "Licence Windows", "clé OEM du BIOS, activation", t => t.Accent2, () => Repair.License(Out));
            T(Theme.Icon("\uEDA2", "◉"), "Test de surface", "secteurs lents / illisibles", t => t.Warn, () => new SurfaceForm().ShowDialog(this), false);
        }

        public override void Open(object arg) { if ("surface".Equals(arg)) new SurfaceForm().ShowDialog(this); }

        void BsodDialog()
        {
            var dumps = Bsod.Dumps();
            var g = new Grid { Dock = DockStyle.Fill }; g.Col("Date", 140).Col("Fichier", 220).Col("Taille", 90).Col("Code", 280);
            foreach (var d in dumps)
            {
                uint c; ulong p1; var code = PanneScan.ReadDump(d.FullName, out c, out p1) ? "0x" + c.ToString("X") + " " + PanneScan.BugName(c) : "?";
                g.Items.Add(new ListViewItem(new[] { d.LastWriteTime.ToString("dd/MM/yyyy HH:mm"), d.Name, AlexB.Fmt.Size(d.Length), code }) { Tag = d.FullName });
            }
            if (dumps.Count == 0) { Out("Aucun fichier de vidage (Minidump / MEMORY.DMP). Vérifie Paramètres système avancés > Démarrage et récupération > « Petite image mémoire ».", "warn"); return; }
            using (var f = new Form { Text = "Écrans bleus · by alex.b", Size = new Size(k(820), k(440)), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Current.Bg, ShowInTaskbar = false })
            {
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = k(44) };
                var an = new Chip("Analyser le vidage sélectionné") { AccentOf = t => t.Ok };
                an.Click += (s, e) => { var p = g.Sel as string; if (p == null) return; f.Close(); Start("Analyse " + Path.GetFileName(p), () => Bsod.Analyze(p, Out)); };
                var inst = new Chip("Installer WinDbg (winget)") { AccentOf = t => t.Muted };
                inst.Click += (s, e) => { f.Close(); Start("Installation de WinDbg", () => { int rc = Sh.Cmd("winget", "install --id Microsoft.WinDbg -e --silent --accept-package-agreements --accept-source-agreements", 900000, null, Encoding.UTF8); Out(rc == 0 ? "WinDbg installé." : "winget : code " + rc + " (winget absent ? installer « App Installer »)", rc == 0 ? "ok" : "bad"); }); };
                bar.Controls.Add(an); bar.Controls.Add(inst);
                var info = new Label { Dock = DockStyle.Top, Height = k(28), Tag = "muted", Text = Bsod.Cdb() != null ? "  Débogueur trouvé : analyse complète possible." : "  Débogueur absent : « Installer WinDbg » d'abord (gratuit, Microsoft)." };
                f.Controls.Add(g); f.Controls.Add(info); f.Controls.Add(bar); CyberForm.Recolor(f); f.ShowDialog(this);
            }
        }

        void OutlookDialog()
        {
            var exe = Repair.OutlookExe();
            using (var f = new Form { Text = "Outlook · by alex.b", Size = new Size(k(560), k(420)), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Current.Bg, ShowInTaskbar = false })
            {
                var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(k(12)) };
                Action<string, Func<Theme, Color>, Action> b = (t, a, go) => { var c = new Chip(t) { AccentOf = a, Margin = new Padding(0, 0, 0, k(8)) }; c.Click += (s, e) => { try { go(); } catch (Exception ex) { Out(ex.Message, "bad"); } }; p.Controls.Add(c); };
                p.Controls.Add(new Label { AutoSize = true, Tag = "muted", Text = exe != null ? "Outlook : " + exe : "Outlook classique introuvable sur ce PC.", Margin = new Padding(0, 0, 0, k(10)) });
                b("Démarrer Outlook en mode sans échec (sans compléments)", t => t.Accent, () => Repair.AsUser(exe, "/safe"));
                b("Réinitialiser le volet de navigation (/resetnavpane)", t => t.Accent, () => Repair.AsUser(exe, "/resetnavpane"));
                b("Nettoyer les vues (/cleanviews)", t => t.Accent, () => Repair.AsUser(exe, "/cleanviews"));
                b("Panneau Courrier : profils (créer un nouveau profil)", t => t.Accent2, () => Process.Start("control.exe", "mlcfg32.cpl"));
                b("Réparer un PST (SCANPST)", t => t.Accent2, () => { var sc = exe != null ? Path.Combine(Path.GetDirectoryName(exe), "SCANPST.EXE") : ""; if (File.Exists(sc)) Process.Start(sc); else Out("SCANPST.EXE introuvable.", "warn"); });
                b("Reconstruire l'OST (Exchange / Microsoft 365 uniquement)", t => t.Warn, () => RebuildOst(f));
                b("Reconstruire l'index de recherche", t => t.Muted, () => { f.Close(); Start("Index de recherche", () => Repair.SearchIndex(Out)); });
                f.Controls.Add(p); CyberForm.Recolor(f); f.ShowDialog(this);
            }
        }

        void RebuildOst(IWin32Window owner)
        {
            if (Process.GetProcessesByName("OUTLOOK").Length > 0) { CyberForm.Warn(owner, "Ferme Outlook d'abord."); return; }
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Outlook");
            var osts = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ost") : new string[0];
            if (osts.Length == 0) { CyberForm.Info(owner, "Aucun fichier OST dans " + dir); return; }
            if (!CyberForm.Ask(owner, "Renommer " + osts.Length + " fichier(s) OST en .old (Outlook les retéléchargera) ?\n\nUNIQUEMENT pour un compte Exchange / Microsoft 365.\nCompte IMAP : les éléments « Cet ordinateur uniquement » (calendrier, contacts locaux) seraient perdus.\nCompte POP : utilise un PST, pas concerné.")) return;
            foreach (var o in osts) { var dst = o + ".old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"); File.Move(o, dst); Out("OST renommé : " + Path.GetFileName(dst), "ok"); }
            Out("Relance Outlook : la boîte se resynchronise (long sur une grosse boîte).", "ok");
        }
    }

    // ===================================================================== Imprimantes
    class PrintersForm : Form
    {
        readonly Grid g; readonly Action<string, string> log;
        public PrintersForm(Action<string, string> logger)
        {
            log = logger; Func<float, int> k = Theme.S;
            Text = "Imprimantes · by alex.b"; Size = new Size(k(1100), k(560)); StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Current.Bg; ShowInTaskbar = false; Font = new Font("Segoe UI", 9f);
            g = new Grid { Dock = DockStyle.Fill }; g.Col("Imprimante", 260).Col("Défaut", 60).Col("Pilote", 250).Col("Port", 160).Col("Adresse", 120).Col("État", 110).Col("En file", 70);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = k(44), Padding = new Padding(k(6)) };
            Action<string, Func<Theme, Color>, Action> b = (t, a, go) => { var c = new Chip(t) { AccentOf = a, Margin = new Padding(0, 0, k(6), 0) }; c.Click += (s, e) => { try { go(); } catch (Exception ex) { log(ex.Message, "bad"); } Fill(); }; bar.Controls.Add(c); };
            b("Page de test", t => t.Ok, () => Invoke_("PrintTestPage"));
            b("Par défaut", t => t.Accent, () => Invoke_("SetDefaultPrinter"));
            b("Page web", t => t.Accent, () => { var ip = SelIp(); if (ip.Length > 0) Process.Start("http://" + ip); });
            b("Ping", t => t.Accent, () => { var ip = SelIp(); if (ip.Length == 0) return; using (var p = new System.Net.NetworkInformation.Ping()) { var r = p.Send(ip, 1500); log("Ping " + ip + " : " + (r.Status == System.Net.NetworkInformation.IPStatus.Success ? r.RoundtripTime + " ms" : "pas de réponse"), r.Status == System.Net.NetworkInformation.IPStatus.Success ? "ok" : "bad"); } });
            b("Vider la file (toutes)", t => t.Warn, () => { if (CyberForm.Ask(this, "Vider toutes les files d'impression ?")) Repair.Spooler(log); });
            b("＋ Ajouter par IP…", t => t.Ok, AddIp);
            b("Supprimer", t => t.Bad, () => { var n = g.Sel as string; if (n != null && CyberForm.Ask(this, "Supprimer l'imprimante « " + n + " » ?")) { Sh.Ps("Remove-Printer -Name " + Sh.Q(n), 60000); log("Imprimante supprimée : " + n, "ok"); } });
            Controls.Add(g); Controls.Add(bar); Fill(); CyberForm.Recolor(this);
        }

        Dictionary<string, string> ports = new Dictionary<string, string>();
        string SelIp() { var n = g.Sel as string; if (n == null) return ""; var it = g.SelectedItems[0]; return it.SubItems[4].Text; }

        void Fill()
        {
            ports.Clear();
            try { using (var s = new ManagementObjectSearcher("SELECT Name, HostAddress FROM Win32_TCPIPPrinterPort")) foreach (ManagementObject o in s.Get()) ports[Convert.ToString(o["Name"])] = Convert.ToString(o["HostAddress"]); } catch { }
            var jobs = new Dictionary<string, int>();
            try { using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_PrintJob")) foreach (ManagementObject o in s.Get()) { var n = Convert.ToString(o["Name"]).Split(',')[0]; int c; jobs.TryGetValue(n, out c); jobs[n] = c + 1; } } catch { }
            g.BeginUpdate(); g.Items.Clear(); var t = Theme.Current;
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, DriverName, PortName, Default, WorkOffline, PrinterStatus FROM Win32_Printer"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var n = Convert.ToString(o["Name"]); var port = Convert.ToString(o["PortName"]); string ip; ports.TryGetValue(port, out ip);
                        bool off = Convert.ToBoolean(o["WorkOffline"]); int st = Convert.ToInt32(o["PrinterStatus"] ?? 0); int j; jobs.TryGetValue(n, out j);
                        var state = off ? "HORS CONNEXION" : st == 3 ? "prête" : st == 4 ? "impression" : st == 7 ? "hors ligne" : st == 1 ? "autre" : "état " + st;
                        g.Items.Add(new ListViewItem(new[] { n, Convert.ToBoolean(o["Default"]) ? "✓" : "", Convert.ToString(o["DriverName"]), port, ip ?? "", state, j > 0 ? j.ToString() : "" }) { Tag = n, ForeColor = off || st == 7 ? t.Bad : j > 0 ? t.Warn : t.Text });
                    }
            }
            catch (Exception ex) { log("Lecture des imprimantes : " + ex.Message, "bad"); }
            g.EndUpdate();
        }

        void Invoke_(string method)
        {
            var n = g.Sel as string; if (n == null) return;
            using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_Printer WHERE Name = '" + n.Replace("\\", "\\\\").Replace("'", "\\'") + "'"))
                foreach (ManagementObject o in s.Get()) { var r = o.InvokeMethod(method, null); log(method + " « " + n + " » : " + (Convert.ToInt32(r ?? 0) == 0 ? "OK" : "code " + r), "ok"); }
        }

        void AddIp()
        {
            var drivers = new List<string>();
            try { using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_PrinterDriver")) foreach (ManagementObject o in s.Get()) drivers.Add(Convert.ToString(o["Name"]).Split(',')[0]); } catch { }
            drivers = drivers.Distinct().OrderBy(x => x).ToList();
            var fi = new F("Adresse IP de l'imprimante", "") { Required = true }; var fn = new F("Nom affiché", "") { Required = true };
            var fd = new F("Pilote installé (installer d'abord le pilote du fabricant s'il manque)", drivers.FirstOrDefault(d => d.Contains("Universal")) ?? drivers.FirstOrDefault() ?? "") { Options = drivers.ToArray(), Editable = false };
            if (!Dlg.Edit(this, "Ajouter une imprimante réseau", fi, fn, fd)) return;
            if (!Regex.IsMatch(fi.Value, @"^\d{1,3}(\.\d{1,3}){3}$")) { CyberForm.Warn(this, "Adresse IP invalide."); return; }
            string outp; int rc = Sh.Ps("$p='IP_'+" + Sh.Q(fi.Value) + ";if(-not (Get-PrinterPort -Name $p -ErrorAction SilentlyContinue)){Add-PrinterPort -Name $p -PrinterHostAddress " + Sh.Q(fi.Value) + "};Add-Printer -Name " + Sh.Q(fn.Value) + " -DriverName " + Sh.Q(fd.Value) + " -PortName $p", 120000, null, out outp);
            log(rc == 0 ? "Imprimante ajoutée : " + fn.Value + " (" + fi.Value + ")" : "Ajout impossible : " + outp.Trim(), rc == 0 ? "ok" : "bad");
        }
    }
}
