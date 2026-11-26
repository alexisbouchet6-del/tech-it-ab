// Tech IT AB - mode secours : réparer un Windows qui ne démarre plus (depuis la clé WinPE ou un disque branché en USB)
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

namespace AmiOps
{
    class Target { public string Drive = "", Version = "", Build = ""; public long Size, Free; public string Label { get { return Drive + "  —  " + (Version.Length > 0 ? Version : "Windows") + "  —  " + AlexB.Fmt.Size(Size) + " (" + AlexB.Fmt.Size(Free) + " libres)"; } } }
    class OffDriver { public string Oem = "", Original = "", Class = "", Provider = "", Date = "", Version = ""; }
    class OffService { public string Name = "", Display = "", File = "", Company = "", Group = ""; public int Start; public bool Blocked; }

    static class Rescue
    {
        public static string Reg { get { return Proc.Sys("reg.exe"); } }

        public static List<Target> Find()
        {
            var l = new List<Target>(); var self = Proc.SysDrive.TrimEnd('\\').ToUpperInvariant();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
                    var root = d.RootDirectory.FullName; if (root.TrimEnd('\\').ToUpperInvariant() == self) continue;   // jamais le Windows en cours d'exécution
                    if (!File.Exists(Path.Combine(root, @"Windows\System32\config\SYSTEM"))) continue;
                    var t = new Target { Drive = root.TrimEnd('\\'), Size = d.TotalSize, Free = d.AvailableFreeSpace };
                    try
                    {
                        WithHive(Path.Combine(root, @"Windows\System32\config\SOFTWARE"), "AMIOPS_SOFT", false, k =>
                        {
                            using (var v = k.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion"))
                            {
                                int build; int.TryParse(Convert.ToString(v.GetValue("CurrentBuildNumber", "0")), out build);
                                var name = Convert.ToString(v.GetValue("ProductName", "Windows")); if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");
                                t.Version = name + " " + v.GetValue("DisplayVersion", ""); t.Build = build.ToString();
                            }
                            return true;
                        });
                    }
                    catch { }
                    l.Add(t);
                }
                catch { }
            }
            return l;
        }

        // Charge une ruche hors ligne, exécute, décharge (toujours)
        public static T WithHive<T>(string file, string mount, bool write, Func<RegistryKey, T> f)
        {
            string o;
            Proc.Run(Reg, "unload HKLM\\" + mount, 15000, out o);   // reste d'un essai précédent
            if (Proc.Run(Reg, "load HKLM\\" + mount + " \"" + file + "\"", 30000, out o) != 0) throw new Exception("ruche illisible : " + Runner(o));
            try { using (var k = Registry.LocalMachine.OpenSubKey(mount, write)) return f(k); }
            finally { GC.Collect(); GC.WaitForPendingFinalizers(); Proc.Run(Reg, "unload HKLM\\" + mount, 30000, out o); }
        }

        static string Runner(string o) { var l = (o ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(); return l.Count > 0 ? l.Last() : "?"; }

        static string Ps(string script, out int rc)
        {
            var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script));
            string o; rc = Proc.Run(Proc.Sys(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64, 120000, Encoding.UTF8, null, out o);
            return o ?? "";
        }

        // Monte la partition de démarrage (EFI en GPT, active en MBR) du disque qui porte ce Windows
        public static string MountBoot(string drive, out bool uefi, Action<string, string> log)
        {
            uefi = false; int rc;
            var letter = drive.TrimEnd(':', '\\');
            var o = Ps("$p=Get-Partition -DriveLetter '" + letter + "';$d=Get-Disk -Number $p.DiskNumber;" +
                       "$free=[char[]](83..90)|?{-not (Test-Path ([string]$_+':\\'))}|select -First 1;" +
                       "if($d.PartitionStyle -eq 'GPT'){$e=Get-Partition -DiskNumber $d.Number|?{$_.GptType -eq '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'}|select -First 1;" +
                       "if(-not $e){throw 'partition EFI introuvable sur ce disque'};" +
                       "if($e.DriveLetter -and [int][char]$e.DriveLetter -ne 0){'BOOT='+$e.DriveLetter}else{Add-PartitionAccessPath -DiskNumber $d.Number -PartitionNumber $e.PartitionNumber -AccessPath ([string]$free+':\\');'BOOT='+$free;'MOUNTED=1'};'MODE=UEFI'}" +
                       "else{$s=Get-Partition -DiskNumber $d.Number|?{$_.IsActive}|select -First 1;if(-not $s){$s=$p};" +
                       "if($s.DriveLetter -and [int][char]$s.DriveLetter -ne 0){'BOOT='+$s.DriveLetter}else{Add-PartitionAccessPath -DiskNumber $d.Number -PartitionNumber $s.PartitionNumber -AccessPath ([string]$free+':\\');'BOOT='+$free;'MOUNTED=1'};'MODE=BIOS'}", out rc);
            var b = Regex.Match(o, @"BOOT=([A-Z])"); var m = Regex.Match(o, @"MODE=(UEFI|BIOS)");
            if (rc != 0 || !b.Success || !m.Success) throw new Exception("partition de démarrage introuvable : " + Runner(o));
            uefi = m.Groups[1].Value == "UEFI";
            log("Disque " + (uefi ? "GPT / UEFI" : "MBR / BIOS") + " — partition de démarrage montée en " + b.Groups[1].Value + ":", "muted");
            return b.Groups[1].Value + ":";
        }

        public static void Unmount(string letter) { string o; Proc.Run(Proc.Sys("mountvol.exe"), letter + "\\ /d", 15000, out o); }

        public static bool RepairBoot(string drive, Action<string, string> log)
        {
            bool uefi; var boot = MountBoot(drive, out uefi, log);
            try
            {
                var store = Path.Combine(boot + "\\", uefi ? @"EFI\Microsoft\Boot\BCD" : @"Boot\BCD");
                if (File.Exists(store)) try { File.Copy(store, store + ".amiops-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak", true); log("Ancien BCD sauvegardé : " + store + ".amiops-….bak", "muted"); } catch { }
                string o;
                int rc = Proc.Run(Proc.Sys("bcdboot.exe"), drive + "\\Windows /s " + boot + " /f " + (uefi ? "UEFI" : "BIOS") + " /l fr-fr", 120000, null, l => log("  " + l, "text"), out o);
                log(rc == 0 ? "Démarrage reconstruit (bcdboot)." : "bcdboot a échoué (code " + rc + ").", rc == 0 ? "ok" : "bad");
                return rc == 0;
            }
            finally { Unmount(boot); }
        }

        public static bool SafeMode(string drive, bool on, Action<string, string> log)
        {
            bool uefi; var boot = MountBoot(drive, out uefi, log);
            try
            {
                var store = Path.Combine(boot + "\\", uefi ? @"EFI\Microsoft\Boot\BCD" : @"Boot\BCD");
                if (!File.Exists(store)) { log("Magasin BCD introuvable : lance d'abord « Réparer le démarrage ».", "bad"); return false; }
                string o;
                int rc = Proc.Run(Proc.Sys("bcdedit.exe"), "/store \"" + store + "\" " + (on ? "/set {default} safeboot minimal" : "/deletevalue {default} safeboot"), 30000, out o);
                log(rc == 0 ? (on ? "Prochain démarrage en MODE SANS ÉCHEC. Penser à le retirer ensuite (bouton « Retirer le mode sans échec » ou msconfig)." : "Mode sans échec retiré : démarrage normal.") : "bcdedit : " + Runner(o), rc == 0 ? "ok" : (on ? "bad" : "warn"));
                return rc == 0;
            }
            finally { Unmount(boot); }
        }

        public static List<OffDriver> Drivers(string drive)
        {
            string o; Proc.Run(Proc.Sys("dism.exe"), "/English /Image:" + drive + "\\ /Get-Drivers /Format:List", 300000, out o);
            var l = new List<OffDriver>(); OffDriver cur = null;
            foreach (var raw in (o ?? "").Split('\n'))
            {
                var m = Regex.Match(raw.Trim(), @"^([^:]+?)\s*:\s*(.*)$"); if (!m.Success) continue;
                var key = m.Groups[1].Value.Trim().ToLowerInvariant(); var val = m.Groups[2].Value.Trim();
                if (key == "published name") { cur = new OffDriver { Oem = val }; l.Add(cur); }
                else if (cur == null) continue;
                else if (key == "original file name") cur.Original = val;
                else if (key == "class name") cur.Class = val;
                else if (key == "provider name") cur.Provider = val;
                else if (key == "date") cur.Date = val;
                else if (key == "version") cur.Version = val;
            }
            return l;
        }

        public static bool RemoveDriver(string drive, string oem, Action<string, string> log)
        {
            if (!Regex.IsMatch(oem, @"^oem\d+\.inf$", RegexOptions.IgnoreCase)) { log("Nom de pilote refusé : " + oem, "bad"); return false; }
            string o; int rc = Proc.Run(Proc.Sys("dism.exe"), "/Image:" + drive + "\\ /Remove-Driver /Driver:" + oem, 300000, out o);
            log(rc == 0 ? "Pilote " + oem + " retiré de l'installation." : "Échec du retrait de " + oem + " : " + Runner(o), rc == 0 ? "ok" : "bad");
            return rc == 0;
        }

        static readonly string[] CriticalGroups = { "boot bus extender", "system bus extender", "scsi miniport", "boot file system", "scsi class", "primary disk", "file system", "base", "keyboard port", "pointer port", "video init", "core" };

        static string Resolve(string drive, string img)
        {
            img = Environment.ExpandEnvironmentVariables((img ?? "").Trim().Trim('"'));
            if (img.Length == 0) return "";
            img = Regex.Replace(img, @"^\\SystemRoot\\", "Windows\\", RegexOptions.IgnoreCase);
            img = Regex.Replace(img, @"^%SystemRoot%\\", "Windows\\", RegexOptions.IgnoreCase);
            img = Regex.Replace(img, @"^\\\?\?\\", "");
            if (Regex.IsMatch(img, @"^[A-Za-z]:\\")) img = img.Substring(3);
            if (img.StartsWith("System32\\", StringComparison.OrdinalIgnoreCase)) img = "Windows\\" + img;
            return Path.Combine(drive + "\\", img);
        }

        public static List<OffService> BootDrivers(string drive)
        {
            return WithHive(Path.Combine(drive + "\\", @"Windows\System32\config\SYSTEM"), "AMIOPS_SYS", false, root =>
            {
                var l = new List<OffService>();
                int cur = 1; using (var sel = root.OpenSubKey("Select")) if (sel != null) cur = Convert.ToInt32(sel.GetValue("Current", 1));
                using (var svcs = root.OpenSubKey("ControlSet" + cur.ToString("000") + "\\Services"))
                    foreach (var n in svcs.GetSubKeyNames())
                        using (var s = svcs.OpenSubKey(n))
                        {
                            if (s == null) continue;
                            int type = Convert.ToInt32(s.GetValue("Type", 0)), start = Convert.ToInt32(s.GetValue("Start", 4));
                            if ((type & 0x3) == 0 || start > 2) continue;   // pilotes noyau / système de fichiers, démarrage 0 à 2
                            var file = Resolve(drive, Convert.ToString(s.GetValue("ImagePath", "")));
                            if (file.Length == 0) file = Path.Combine(drive + "\\", @"Windows\System32\drivers\" + n + ".sys");
                            string company = ""; try { if (File.Exists(file)) company = FileVersionInfo.GetVersionInfo(file).CompanyName ?? ""; } catch { }
                            if (company.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            var group = Convert.ToString(s.GetValue("Group", ""));
                            var disp = Convert.ToString(s.GetValue("DisplayName", n)); if (disp.StartsWith("@")) disp = n;
                            l.Add(new OffService { Name = n, Display = disp, File = file, Company = company.Length > 0 ? company : File.Exists(file) ? "(non signé / inconnu)" : "(fichier absent)", Group = group, Start = start, Blocked = CriticalGroups.Contains(group.ToLowerInvariant()) });
                        }
                return l.OrderBy(x => x.Blocked).ThenBy(x => x.Company).ToList();
            });
        }

        static string Journal(string drive) { var d = Path.Combine(drive + "\\", "AmiOps-secours"); Directory.CreateDirectory(d); return Path.Combine(d, "pilotes-desactives.txt"); }

        public static bool SetStart(string drive, OffService s, bool disable, Action<string, string> log)
        {
            if (disable && s.Blocked) { log(s.Name + " : pilote de démarrage critique (stockage / bus) — désactivation refusée.", "bad"); return false; }
            int restore = s.Start;
            if (!disable)
            {
                var line = File.Exists(Journal(drive)) ? File.ReadAllLines(Journal(drive)).LastOrDefault(x => x.StartsWith(s.Name + "|")) : null;
                if (line == null) { log("Aucune valeur d'origine notée pour " + s.Name + ".", "warn"); return false; }
                restore = int.Parse(line.Split('|')[1]);
            }
            return WithHive(Path.Combine(drive + "\\", @"Windows\System32\config\SYSTEM"), "AMIOPS_SYS", true, root =>
            {
                int cur = 1; using (var sel = root.OpenSubKey("Select")) if (sel != null) cur = Convert.ToInt32(sel.GetValue("Current", 1));
                using (var k = root.OpenSubKey("ControlSet" + cur.ToString("000") + "\\Services\\" + s.Name, true))
                {
                    if (k == null) { log("Service introuvable : " + s.Name, "bad"); return false; }
                    if (disable) File.AppendAllText(Journal(drive), s.Name + "|" + Convert.ToInt32(k.GetValue("Start", 3)) + "|" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n");
                    k.SetValue("Start", disable ? 4 : restore, RegistryValueKind.DWord);
                }
                log(s.Name + (disable ? " désactivé (Start=4). Journal : " + Journal(drive) : " réactivé (Start=" + restore + ")."), "ok");
                return true;
            });
        }

        public static int Stream(string exe, string args, int timeoutMin, Encoding enc, Action<string, string> log)
        {
            string o;
            int rc = Proc.Run(exe, args, timeoutMin * 60000, enc, l => { if (!Regex.IsMatch(l, @"^\s*\d+\s*%|\[=*\s*\d+[.,]\d+%")) log("  " + l, "text"); }, out o);
            log(Path.GetFileName(exe) + " terminé (code " + rc + ").", rc == 0 ? "ok" : "warn");
            return rc;
        }

        public static List<string> Profiles(string drive)
        {
            var root = Path.Combine(drive + "\\", "Users"); if (!Directory.Exists(root)) return new List<string>();
            var skip = new[] { "public", "default", "default user", "all users", "defaultapppool", "wdagutilityaccount" };
            return Directory.GetDirectories(root).Select(Path.GetFileName).Where(n => !skip.Contains(n.ToLowerInvariant())).ToList();
        }

        public static void Backup(string drive, List<string> users, string dest, Action<string, string> log)
        {
            var parts = new[] { "Desktop", "Documents", "Pictures", "Downloads", "Videos", "Music", "Favorites", "Contacts", @"AppData\Local\Microsoft\Outlook", @"AppData\Roaming\Microsoft\Signatures", @"AppData\Roaming\Microsoft\Sticky Notes",
                @"AppData\Local\Google\Chrome\User Data\Default\Bookmarks", @"AppData\Local\Microsoft\Edge\User Data\Default\Bookmarks", @"AppData\Roaming\Mozilla\Firefox\Profiles" };
            foreach (var u in users)
            {
                var src = Path.Combine(drive + "\\", "Users", u);
                var all = parts.ToList();
                try { all.AddRange(Directory.GetDirectories(src, "OneDrive*").Select(Path.GetFileName)); } catch { }
                foreach (var p in all)
                {
                    var s = Path.Combine(src, p); var d = Path.Combine(dest, u, p);
                    if (File.Exists(s)) { try { Directory.CreateDirectory(Path.GetDirectoryName(d)); File.Copy(s, d, true); log("  " + u + "\\" + p, "text"); } catch (Exception ex) { log("  " + p + " : " + ex.Message, "warn"); } continue; }
                    if (!Directory.Exists(s)) continue;
                    string o;
                    int rc = Proc.Run(Proc.Sys("robocopy.exe"), "\"" + s + "\" \"" + d + "\" /E /XJ /R:1 /W:1 /NP /NFL /NDL /MT:8 /XF desktop.ini thumbs.db", 6 * 3600 * 1000, out o);
                    var bytes = Regex.Match(o ?? "", @"(Bytes|Octets)\s*:\s*([\d.,]+\s*[kmgt]?)", RegexOptions.IgnoreCase);
                    log("  " + u + "\\" + p + (bytes.Success ? " — " + bytes.Groups[2].Value.Trim() : "") + (rc >= 8 ? "  (erreurs : code " + rc + ")" : ""), rc >= 8 ? "warn" : "ok");
                }
            }
            log("Sauvegarde terminée dans " + dest, "ok");
        }
    }

    class SecoursPage : Page
    {
        readonly ComboBox target; readonly Label info; readonly LogBox box; readonly FlowLayoutPanel tiles; bool busy;
        List<Target> found = new List<Target>();
        public override string Title { get { return "Secours"; } }

        public SecoursPage()
        {
            Lbl("SECOURS", 0, k(2), "accent", 13f, true);
            Lbl(Program.IsWinPE ? "Démarré depuis la clé " + Ed.Name + " (WinPE) : le Windows du PC est réparé hors ligne." : "Répare un Windows qui ne démarre plus : disque du PC en panne branché sur ce poste (boîtier / dock USB), ou depuis la clé " + Ed.Name + ".", k(120), k(10), "muted");
            target = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(0, k(40)), Width = k(560) };
            Controls.Add(target);
            Btn("Actualiser", t => t.Muted, k(570), k(36), (s, e) => Scan());
            info = Lbl("", 0, k(72), "muted");
            tiles = new FlowLayoutPanel { Location = new Point(0, k(94)), Height = k(256), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(tiles);
            T(Theme.Icon("\uE895", "↻"), "Réparer le démarrage", "bcdboot (UEFI ou BIOS)", t => t.Ok, d => { if (Confirm("Reconstruire les fichiers de démarrage de " + d + " ?\nL'ancien BCD est sauvegardé.")) Rescue.RepairBoot(d, Out); });
            T(Theme.Icon("\uE7A7", "⟲"), "Annuler les MAJ en attente", "DISM /RevertPendingActions", t => t.Warn, d => { if (Confirm("Annuler les mises à jour en attente sur " + d + " ?\n(PC bloqué sur « Préparation de Windows » / redémarrage en boucle)")) Rescue.Stream(Proc.Sys("dism.exe"), "/Image:" + d + "\\ /Cleanup-Image /RevertPendingActions", 60, null, Out); });
            T(Theme.Icon("\uE7EF", "⚙"), "Pilotes ajoutés", "lister / retirer un pilote", t => t.Accent, d => Ui(() => DriversDialog(d)), true);
            T(Theme.Icon("\uE7BA", "⚠"), "Pilotes au démarrage", "désactiver un pilote qui bloque", t => t.Warn, d => Ui(() => ServicesDialog(d)), true);
            T(Theme.Icon("\uE72E", "⛨"), "Mode sans échec", "au prochain démarrage", t => t.Accent2, d => { if (Confirm("Forcer le MODE SANS ÉCHEC au prochain démarrage de " + d + " ?")) Rescue.SafeMode(d, true, Out); });
            T(Theme.Icon("\uE72E", "⛨"), "Retirer le sans échec", "démarrage normal", t => t.Muted, d => Rescue.SafeMode(d, false, Out));
            T(Theme.Icon("\uE9F5", "✓"), "Fichiers système", "SFC hors ligne", t => t.Accent, d => Rescue.Stream(Proc.Sys("sfc.exe"), "/scannow /offbootdir=" + d + "\\ /offwindir=" + d + "\\Windows", 90, Encoding.Unicode, Out));
            T(Theme.Icon("\uEDA2", "◉"), "Vérifier le disque", "CHKDSK /F", t => t.Accent, d => { if (Confirm("Lancer CHKDSK /F /X sur " + d + " ?\nPeut durer longtemps sur un disque abîmé : sauvegarde d'abord si le disque est suspect.")) Rescue.Stream(Proc.Sys("chkdsk.exe"), d + " /F /X", 240, null, Out); });
            T(Theme.Icon("\uE8B7", "⇩"), "Sauvegarder les données", "profils → clé / disque", t => t.Ok, d => Ui(() => BackupDialog(d)), true);
            T(Theme.Icon("\uE9D9", "♥"), "Santé des disques", "SMART, usure, erreurs", t => t.Accent, d => DiskHealth(), false, true);
            T(Theme.Icon("\uE756", ">"), "Invite de commandes", "diskpart, bootrec…", t => t.Muted, d => Process.Start(Proc.Sys("cmd.exe")), false, true);
            T(Theme.Icon("\uE8A5", "✎"), "Explorateur (Bloc-notes)", "Fichier > Ouvrir = parcourir", t => t.Muted, d => Process.Start(Proc.Sys("notepad.exe")), false, true);
            T(Theme.Icon("\uE7E8", "⏻"), Program.IsWinPE ? "Redémarrer" : "Retirer le disque", Program.IsWinPE ? "wpeutil reboot" : "éjecter avant débranchement", t => t.Bad, d => { if (Program.IsWinPE) Process.Start(Proc.Sys("wpeutil.exe"), "reboot"); else Out("Éjecte le disque via l'icône « Retirer le périphérique en toute sécurité » avant de le débrancher.", "warn"); }, false, true);
            box = new LogBox { Location = new Point(0, k(360)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Controls.Add(box);
            Resize += (s, e) => { tiles.Width = ClientSize.Width; box.Size = new Size(ClientSize.Width, ClientSize.Height - box.Top); };
            target.SelectedIndexChanged += (s, e) => { var t = Sel(); info.Text = t == null ? "" : "Build " + t.Build + " · profils : " + string.Join(", ", Rescue.Profiles(t.Drive)); };
        }

        void Ui(Action a) { BeginInvoke(a); }
        void Out(string s, string c) { box.Line(s, c); }
        bool Confirm(string q) { bool r = false; Invoke(new Action(() => r = CyberForm.Ask(this, q))); return r; }
        Target Sel() { int i = target.SelectedIndex; return i >= 0 && i < found.Count ? found[i] : null; }

        void T(string glyph, string title, string hint, Func<Theme, Color> acc, Action<string> go, bool uiOnly = false, bool noTarget = false)
        {
            var t = new Tile { Glyph = glyph, Title = title, Hint = hint, AccentOf = acc, Size = new Size(k(158), k(118)) };
            t.Click += (s, e) =>
            {
                if (busy) { CyberForm.Info(this, "Une opération est en cours."); return; }
                var tg = Sel();
                if (tg == null && !noTarget) { CyberForm.Warn(this, "Aucun Windows hors ligne trouvé : branche le disque du PC en panne (ou démarre sur la clé " + Ed.Name + "), puis « Actualiser »."); return; }
                var d = tg != null ? tg.Drive : "";
                if (uiOnly || (noTarget && title != "Santé des disques")) { try { go(d); } catch (Exception ex) { Out("Erreur : " + ex.Message, "bad"); } return; }
                busy = true; UseWaitCursor = true;
                box.Line("", "text"); box.Line("▶ " + title + (d.Length > 0 ? " — " + d : "") + " — " + DateTime.Now.ToString("HH:mm:ss"), "accent", true);
                Task.Run(() =>
                {
                    try { go(d); } catch (Exception ex) { Out("Erreur : " + ex.Message, "bad"); Log.Write(ex.ToString()); }
                    BeginInvoke(new Action(() => { busy = false; UseWaitCursor = false; }));
                });
            };
            tiles.Controls.Add(t);
        }

        public override void OnShow() { if (found.Count == 0) Scan(); }

        void Scan()
        {
            target.Items.Clear(); info.Text = "Recherche des installations Windows…";
            Task.Run(() =>
            {
                List<Target> l; try { l = Rescue.Find(); } catch (Exception ex) { Log.Write(ex.ToString()); l = new List<Target>(); }
                BeginInvoke(new Action(() =>
                {
                    found = l; target.Items.Clear();
                    foreach (var t in l) target.Items.Add(t.Label);
                    if (l.Count > 0) target.SelectedIndex = 0;
                    else info.Text = "Aucun Windows hors ligne : branche le disque du PC en panne sur ce poste, ou démarre le PC sur la clé " + Ed.Name + ".";
                }));
            });
        }

        void DiskHealth()
        {
            try
            {
                foreach (var d in PanneScan.PowerShellJson("Get-PhysicalDisk | % { $r = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; [pscustomobject]@{Name=$_.FriendlyName;Media=[string]$_.MediaType;Health=[string]$_.HealthStatus;Size=[math]::Round($_.Size/1GB);Temp=$r.Temperature;Wear=$r.Wear;Rerr=$r.ReadErrorsUncorrected;Hours=$r.PowerOnHours} } | ConvertTo-Json -Compress"))
                {
                    Func<string, string> g = key => d.ContainsKey(key) && d[key] != null ? Convert.ToString(d[key]) : "";
                    var ok = g("Health") == "Healthy" || g("Health") == "0";
                    Out("  " + g("Name") + " (" + g("Media") + ", " + g("Size") + " Go) : " + (ok ? "sain" : g("Health")) + (g("Temp").Length > 0 && g("Temp") != "0" ? " · " + g("Temp") + " °C" : "") + (g("Wear").Length > 0 && g("Wear") != "0" ? " · usure " + g("Wear") + " %" : "") + (g("Rerr").Length > 0 && g("Rerr") != "0" ? " · " + g("Rerr") + " erreurs de lecture" : "") + (g("Hours").Length > 0 ? " · " + g("Hours") + " h" : ""), ok ? "ok" : "bad");
                }
            }
            catch (Exception ex) { Out("Santé des disques indisponible : " + ex.Message, "warn"); }
        }

        Form Dialog(string title, Grid g, int w, int h)
        {
            var f = new Form { Text = title + " · by alex.b", Size = new Size(k(w), k(h)), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Current.Bg, ShowInTaskbar = false };
            g.Dock = DockStyle.Fill; f.Controls.Add(g); return f;
        }

        void DriversDialog(string d)
        {
            box.Line("Lecture des pilotes ajoutés à " + d + "…", "muted"); UseWaitCursor = true;
            Task.Run(() =>
            {
                List<OffDriver> l; try { l = Rescue.Drivers(d); } catch (Exception ex) { l = new List<OffDriver>(); Out(ex.Message, "bad"); }
                BeginInvoke(new Action(() =>
                {
                    UseWaitCursor = false;
                    var g = new Grid().Col("Nom", 90).Col("Fichier d'origine", 170).Col("Classe", 120).Col("Fournisseur", 170).Col("Date", 90).Col("Version", 140);
                    foreach (var x in l.OrderByDescending(x => { DateTime dt; return DateTime.TryParse(x.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt) ? dt : DateTime.MinValue; }))
                        g.Items.Add(new ListViewItem(new[] { x.Oem, x.Original, x.Class, x.Provider, x.Date, x.Version }) { Tag = x });
                    using (var f = Dialog("Pilotes ajoutés — " + d + " (" + l.Count + ")", g, 900, 600))
                    {
                        var rm = new Chip("Retirer le pilote sélectionné (le plus récent est souvent le coupable)") { AccentOf = t => t.Bad, Dock = DockStyle.Bottom };
                        rm.Click += (s, e) =>
                        {
                            var x = g.Sel as OffDriver; if (x == null) return;
                            if (!CyberForm.Ask(f, "Retirer " + x.Oem + " (" + x.Original + ", " + x.Provider + ") de " + d + " ?\nWindows réutilisera un pilote générique au démarrage.")) return;
                            if (Rescue.RemoveDriver(d, x.Oem, Out)) g.Items.Remove(g.SelectedItems[0]);
                        };
                        f.Controls.Add(rm); CyberForm.Recolor(f); f.ShowDialog(this);
                    }
                }));
            });
        }

        void ServicesDialog(string d)
        {
            List<OffService> l;
            try { l = Rescue.BootDrivers(d); } catch (Exception ex) { Out("Lecture du registre hors ligne impossible : " + ex.Message, "bad"); return; }
            var g = new Grid().Col("Pilote", 130).Col("Nom", 200).Col("Éditeur", 170).Col("Démarrage", 80).Col("Groupe", 130).Col("Fichier", 260);
            Action fill = () =>
            {
                g.Items.Clear(); var t = Theme.Current;
                foreach (var s in l) g.Items.Add(new ListViewItem(new[] { s.Name, s.Display, s.Company, s.Start == 0 ? "boot" : s.Start == 1 ? "système" : s.Start == 2 ? "auto" : s.Start == 4 ? "DÉSACTIVÉ" : s.Start.ToString(), s.Group + (s.Blocked ? " (critique)" : ""), s.File }) { Tag = s, ForeColor = s.Blocked ? t.Muted : s.Start == 4 ? t.Bad : t.Text });
            };
            fill();
            using (var f = Dialog("Pilotes tiers chargés au démarrage — " + d, g, 1000, 620))
            {
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = k(42) };
                var dis = new Chip("Désactiver (antivirus, filtre, VPN… qui bloque le démarrage)") { AccentOf = t => t.Bad };
                var en = new Chip("Réactiver") { AccentOf = t => t.Ok };
                dis.Click += (s, e) => { var x = g.Sel as OffService; if (x == null) return; if (CyberForm.Ask(f, "Désactiver le pilote « " + x.Name + " » (" + x.Company + ") sur " + d + " ?\nRéversible avec « Réactiver ».") && Rescue.SetStart(d, x, true, Out)) { x.Start = 4; fill(); } };
                en.Click += (s, e) => { var x = g.Sel as OffService; if (x != null && Rescue.SetStart(d, x, false, Out)) { l = Rescue.BootDrivers(d); fill(); } };
                bar.Controls.Add(dis); bar.Controls.Add(en); f.Controls.Add(bar);
                CyberForm.Recolor(f); f.ShowDialog(this);
            }
        }

        void BackupDialog(string d)
        {
            var users = Rescue.Profiles(d);
            if (users.Count == 0) { Out("Aucun profil utilisateur sur " + d, "warn"); return; }
            var usb = DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Removable && !x.RootDirectory.FullName.StartsWith(d, StringComparison.OrdinalIgnoreCase)).Select(x => x.RootDirectory.FullName).FirstOrDefault()
                      ?? DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed && !x.RootDirectory.FullName.StartsWith(d, StringComparison.OrdinalIgnoreCase) && x.RootDirectory.FullName.TrimEnd('\\') != Proc.SysDrive.TrimEnd('\\') && x.RootDirectory.FullName != "X:\\").Select(x => x.RootDirectory.FullName).FirstOrDefault() ?? "D:\\";
            var fu = new F("Profils à sauvegarder (séparés par des virgules)", string.Join(", ", users)) { Required = true };
            var fd = new F("Destination (clé USB, disque externe, partage réseau)", Path.Combine(usb, "Sauvegarde_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm"))) { Required = true };
            if (!Dlg.Edit(this, "Sauvegarder les données", fu, fd)) return;
            var sel = fu.Value.Split(',').Select(x => x.Trim()).Where(x => users.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
            var dest = fd.Value;
            if (dest.StartsWith(d, StringComparison.OrdinalIgnoreCase)) { CyberForm.Warn(this, "La destination ne doit pas être sur le disque à sauver."); return; }
            busy = true; UseWaitCursor = true;
            box.Line("", "text"); box.Line("▶ Sauvegarde de " + string.Join(", ", sel) + " vers " + dest, "accent", true);
            Task.Run(() =>
            {
                try { Directory.CreateDirectory(dest); Rescue.Backup(d, sel, dest, Out); } catch (Exception ex) { Out("Erreur : " + ex.Message, "bad"); }
                BeginInvoke(new Action(() => { busy = false; UseWaitCursor = false; }));
            });
        }
    }
}
