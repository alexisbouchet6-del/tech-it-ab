// PrépaPoste - préparation d'un PC neuf en un clic
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

[assembly: AssemblyTitle("PrepaPoste")]
[assembly: AssemblyDescription("Préparation d'un PC neuf")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("PrépaPoste")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace PrepaPoste
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            AppInfo.Name = "PrépaPoste"; AppInfo.Id = "PrepaPoste"; AppInfo.Version = "1.0.0"; AppInfo.MachineWide = true;
            Boot.Run(() => new MainForm(), true, "PrépaPoste modifie les réglages système et installe des logiciels.");
        }
    }

    class Step
    {
        public string Id, Group, Title, Hint;
        public bool Default;
        public Func<Runner, bool> Run;
        public CheckBox Box;
    }

    // ===================================================================== Exécution
    class Runner
    {
        public readonly LogBox Log;
        public string NewName = "";
        public bool NeedReboot;
        public readonly List<string> Report = new List<string>();
        public Runner(LogBox l) { Log = l; }

        public void Say(string s, string c) { Log.Line("    " + s, c); }

        public int Ps(string script, int timeoutMs, out string output)
        {
            // -EncodedCommand : aucun problème de guillemets ni d'injection via la ligne de commande
            var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Stop';" + script));
            return Proc.Run(Proc.Sys(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64, timeoutMs, Encoding.UTF8, null, out output);
        }

        public bool PsOk(string script, int timeoutMs)
        {
            string o; int rc = Ps("[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script, timeoutMs, out o);
            if (rc != 0) Say(Last(o), "warn");
            return rc == 0;
        }

        public static string Last(string o)
        {
            var l = (o ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith("Au caractère") && !x.StartsWith("At line")).ToList();
            return l.Count == 0 ? "(aucun détail)" : string.Join(" | ", l.Take(2));
        }

        public bool Reg(RegistryKey root, string path, string name, object value, RegistryValueKind kind)
        {
            try { using (var k = root.CreateSubKey(path)) k.SetValue(name, value, kind); return true; }
            catch (Exception ex) { Say("registre : " + ex.Message, "warn"); return false; }
        }

        public bool Cmd(string exe, string args, int timeoutMs)
        {
            string o; int rc = Proc.Run(exe, args, timeoutMs, out o);
            if (rc != 0) Say(Last(o), "warn");
            return rc == 0;
        }

        public bool WingetOk()
        {
            string o; return Proc.Run("winget", "--version", 20000, Encoding.UTF8, null, out o) == 0;
        }

        public bool Winget(string id, string name)
        {
            string o;
            int rc = Proc.Run("winget", "install --id " + id + " -e --silent --disable-interactivity --accept-package-agreements --accept-source-agreements", 900000, Encoding.UTF8, null, out o);
            bool already = Regex.IsMatch(o ?? "", "déjà installé|already installed|Aucune mise à niveau|No available upgrade|No newer package", RegexOptions.IgnoreCase);
            if (rc == 0 || already) { Say(name + (already ? " (déjà installé)" : " installé"), "ok"); return true; }
            Say(name + " : échec (code " + rc + ") " + Last(o), "bad");
            return false;
        }
    }

    static class Catalog
    {
        public static string AppsFile { get { return Path.Combine(AppInfo.DataDir, "logiciels.txt"); } }
        public static string BloatFile { get { return Path.Combine(AppInfo.DataDir, "applis-a-supprimer.txt"); } }

        static List<string[]> Lines(string file, string content)
        {
            if (!File.Exists(file)) File.WriteAllText(file, content, new UTF8Encoding(false));
            return File.ReadAllLines(file, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#"))
                .Select(l => l.Split('|').Select(x => x.Trim()).ToArray()).Where(p => p.Length >= 2 && p[1].Length > 0).ToList();
        }

        // Nom | ID winget | 1 = coché par défaut
        public static List<string[]> Apps()
        {
            return Lines(AppsFile,
                "# PrépaPoste (by alex.b) - logiciels proposés : Nom | ID winget | 1 = coché par défaut\r\n" +
                "# Trouver un ID : winget search <nom>   (ex. winget search acrobat)\r\n" +
                "7-Zip | 7zip.7zip | 1\r\n" +
                "Google Chrome | Google.Chrome | 1\r\n" +
                "Mozilla Firefox | Mozilla.Firefox | 0\r\n" +
                "Adobe Acrobat Reader | Adobe.Acrobat.Reader.64-bit | 1\r\n" +
                "VLC | VideoLAN.VLC | 1\r\n" +
                "AnyDesk | AnyDesk.AnyDesk | 0\r\n" +
                "TeamViewer | TeamViewer.TeamViewer | 0\r\n" +
                "Notepad++ | Notepad++.Notepad++ | 0\r\n" +
                "LibreOffice | TheDocumentFoundation.LibreOffice | 0\r\n" +
                "Zoom | Zoom.Zoom | 0\r\n");
        }

        // Nom affiché | nom du paquet Appx (joker * accepté) | 1 = coché par défaut
        public static List<string[]> Bloat()
        {
            return Lines(BloatFile,
                "# PrépaPoste (by alex.b) - applis préinstallées à supprimer : Nom | paquet Appx (joker *) | 1 = coché\r\n" +
                "# Lister les paquets d'un PC : Get-AppxPackage | Select Name\r\n" +
                "Actualités (Bing News) | Microsoft.BingNews | 1\r\n" +
                "Solitaire Collection | Microsoft.MicrosoftSolitaireCollection | 1\r\n" +
                "Clipchamp | Clipchamp.Clipchamp | 1\r\n" +
                "Hub de commentaires | Microsoft.WindowsFeedbackHub | 1\r\n" +
                "Xbox (application) | Microsoft.GamingApp | 1\r\n" +
                "Microsoft 365 (raccourci Office Hub) | Microsoft.MicrosoftOfficeHub | 0\r\n" +
                "Contacts | Microsoft.People | 1\r\n" +
                "Spotify | SpotifyAB.SpotifyMusic | 1\r\n" +
                "Candy Crush | king.com.* | 1\r\n" +
                "Disney+ | Disney.* | 1\r\n");
        }

        public static List<Step> Build()
        {
            var l = new List<Step>();
            Action<string, string, string, bool, Func<Runner, bool>, string> add = (id, g, t, d, f, h) => l.Add(new Step { Id = id, Group = g, Title = t, Default = d, Run = f, Hint = h });

            // ------------------------------------------------ Système
            add("rename", "SYSTÈME", "Renommer le PC (champ en haut)", true, r =>
            {
                if (r.NewName.Length == 0 || r.NewName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) { r.Say("nom inchangé (" + Environment.MachineName + ")", "muted"); return true; }
                bool ok = r.PsOk("Rename-Computer -NewName '" + r.NewName + "' -Force", 60000);
                if (ok) { r.NeedReboot = true; r.Say("nouveau nom : " + r.NewName + " (après redémarrage)", "ok"); }
                return ok;
            }, "Nom validé : 15 caractères max, lettres / chiffres / tirets.");
            add("tz", "SYSTÈME", "Fuseau horaire Paris + synchro de l'heure", true, r =>
                r.Cmd(Proc.Sys("tzutil.exe"), "/s \"Romance Standard Time\"", 15000) & (r.Cmd(Proc.Sys("w32tm.exe"), "/resync /force", 30000) || true), null);
            add("power", "SYSTÈME", "Alimentation : pas de veille sur secteur, écran éteint après 15 min", true, r =>
                r.Cmd(Proc.Sys("powercfg.exe"), "/change standby-timeout-ac 0", 15000) & r.Cmd(Proc.Sys("powercfg.exe"), "/change hibernate-timeout-ac 0", 15000) & r.Cmd(Proc.Sys("powercfg.exe"), "/change monitor-timeout-ac 15", 15000),
                "Sur batterie, les réglages Windows restent inchangés.");
            add("fastboot", "SYSTÈME", "Désactiver le démarrage rapide (évite les mises à jour bloquées)", true, r =>
                r.Reg(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, RegistryValueKind.DWord), null);
            add("restore", "SYSTÈME", "Activer la restauration système + point « PC préparé »", true, r =>
                r.PsOk("Enable-ComputerRestore -Drive \"$env:SystemDrive\\\";" +
                       "New-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' -Name SystemRestorePointCreationFrequency -Value 0 -PropertyType DWord -Force | Out-Null;" +
                       "Checkpoint-Computer -Description 'PrepaPoste - PC prepare' -RestorePointType MODIFY_SETTINGS", 300000), null);
            add("rdp", "SYSTÈME", "Activer le Bureau à distance (+ règle pare-feu)", false, r =>
                r.Reg(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", 0, RegistryValueKind.DWord) &
                r.PsOk("Get-NetFirewallRule -Name 'RemoteDesktop*' | Enable-NetFirewallRule", 60000), "Uniquement sur réseau d'entreprise : jamais exposé sur Internet.");

            // ------------------------------------------------ Explorateur / confort (compte qui lance l'outil)
            const string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            add("ext", "EXPLORATEUR (compte actuel)", "Afficher les extensions de fichiers (.pdf, .exe…)", true, r => r.Reg(Registry.CurrentUser, adv, "HideFileExt", 0, RegistryValueKind.DWord), "Sécurité : repère les faux « facture.pdf.exe ».");
            add("thispc", "EXPLORATEUR (compte actuel)", "Explorateur ouvert sur « Ce PC »", true, r => r.Reg(Registry.CurrentUser, adv, "LaunchTo", 1, RegistryValueKind.DWord), null);
            add("deskicon", "EXPLORATEUR (compte actuel)", "Icône « Ce PC » sur le bureau", true, r => r.Reg(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", 0, RegistryValueKind.DWord), null);
            add("ctx", "EXPLORATEUR (compte actuel)", "Windows 11 : menu clic droit complet (classique)", false, r => r.Reg(Registry.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "", RegistryValueKind.String), null);
            add("left", "EXPLORATEUR (compte actuel)", "Windows 11 : barre des tâches alignée à gauche", false, r => r.Reg(Registry.CurrentUser, adv, "TaskbarAl", 0, RegistryValueKind.DWord), null);
            add("ads", "EXPLORATEUR (compte actuel)", "Couper les suggestions / pubs du menu Démarrer", true, r =>
            {
                const string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
                bool ok = true;
                foreach (var v in new[] { "SilentInstalledAppsEnabled", "SystemPaneSuggestionsEnabled", "SubscribedContent-338388Enabled", "SubscribedContent-338389Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled" })
                    ok &= r.Reg(Registry.CurrentUser, cdm, v, 0, RegistryValueKind.DWord);
                ok &= r.Reg(Registry.CurrentUser, adv, "Start_IrisRecommendations", 0, RegistryValueKind.DWord);
                return ok;
            }, null);

            // ------------------------------------------------ Logiciels
            foreach (var a in Apps())
            {
                var app = a;
                add("app:" + app[1], "LOGICIELS (winget)", app[0], app.Length > 2 && app[2] == "1", r => r.Winget(app[1], app[0]), "winget " + app[1]);
            }
            // ------------------------------------------------ Nettoyage
            foreach (var b in Bloat())
            {
                var bl = b;
                if (!Regex.IsMatch(bl[1], @"^[A-Za-z0-9.\-_*]+$")) continue;   // nom de paquet seulement (le fichier ne doit pas injecter de commande)
                add("bloat:" + bl[1], "APPLIS PRÉINSTALLÉES À SUPPRIMER", bl[0], bl.Length > 2 && bl[2] == "1", r =>
                {
                    string o;
                    int rc = r.Ps("$n='" + bl[1] + "';$p=Get-AppxPackage -AllUsers -Name $n -ErrorAction SilentlyContinue;" +
                                  "if($p){$p|Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue};" +
                                  "Get-AppxProvisionedPackage -Online|?{$_.DisplayName -like $n}|Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue|Out-Null;" +
                                  "if($p){'supprime'}else{'absent'}", 180000, out o);
                    r.Say(bl[0] + (o.Contains("supprime") ? " supprimé" : " absent de ce PC"), o.Contains("supprime") ? "ok" : "muted");
                    return rc == 0;
                }, bl[1]);
            }
            // ------------------------------------------------ Mises à jour
            add("wu", "MISES À JOUR", "Lancer la recherche Windows Update (+ ouvrir la page)", true, r =>
            {
                r.Cmd(Proc.Sys("UsoClient.exe"), "StartInteractiveScan", 30000);
                try { Process.Start("ms-settings:windowsupdate"); } catch { }
                r.Say("recherche lancée : suivre l'avancement dans Paramètres > Windows Update", "ok");
                return true;
            }, null);
            add("upg", "MISES À JOUR", "Mettre à jour tous les logiciels (winget upgrade --all)", true, r =>
            {
                string o; int rc = Proc.Run("winget", "upgrade --all --silent --disable-interactivity --accept-package-agreements --accept-source-agreements", 1800000, Encoding.UTF8, null, out o);
                r.Say(rc == 0 ? "logiciels à jour" : "terminé avec des erreurs (code " + rc + ") : " + Runner.Last(o), rc == 0 ? "ok" : "warn");
                return rc == 0;
            }, null);

            // ------------------------------------------------ Contrôles (lecture seule)
            add("check", "CONTRÔLES (lecture seule)", "BitLocker, activation Windows, antivirus, disque, RAM", true, r =>
            {
                string o;
                r.Ps("[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                     "$b=Get-CimInstance -Namespace root\\cimv2\\Security\\MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -Filter \"DriveLetter='$env:SystemDrive'\" -ErrorAction SilentlyContinue;" +
                     "'BITLOCKER='+$(if($b){$b.ProtectionStatus}else{'?'});" +
                     "$l=Get-CimInstance SoftwareLicensingProduct -Filter \"PartialProductKey IS NOT NULL AND ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f'\"|Select -First 1;" +
                     "'ACTIVATION='+$l.LicenseStatus;" +
                     "Get-CimInstance -Namespace root\\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue|%{'AV='+$_.displayName+'|'+$_.productState};" +
                     "$d=Get-PSDrive ($env:SystemDrive.TrimEnd(':'));'DISK='+[math]::Round($d.Free/1GB)+'|'+[math]::Round(($d.Used+$d.Free)/1GB);" +
                     "'RAM='+[math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB)", 120000, out o);
                Func<string, string> g = key => { var m = Regex.Match(o ?? "", "^" + key + "=(.*)$", RegexOptions.Multiline); return m.Success ? m.Groups[1].Value.Trim() : "?"; };
                var bl = g("BITLOCKER");
                r.Say("BitLocker " + Proc.SysDrive.TrimEnd('\\') + " : " + (bl == "1" ? "ACTIF" : bl == "0" ? "inactif (à activer si portable / données sensibles, clé à sauvegarder)" : "indéterminé (édition Famille ?)"), bl == "1" ? "ok" : "warn");
                var act = g("ACTIVATION");
                r.Say("Activation Windows : " + (act == "1" ? "ACTIVÉ" : "NON ACTIVÉ (code " + act + ")"), act == "1" ? "ok" : "bad");
                var avs = Regex.Matches(o ?? "", "^AV=(.*)$", RegexOptions.Multiline).Cast<Match>().Select(m => m.Groups[1].Value.Trim()).ToList();
                if (avs.Count == 0) r.Say("Antivirus : aucun déclaré dans le Centre de sécurité", "bad");
                foreach (var av in avs)
                {
                    var p = av.Split('|'); int st; int.TryParse(p.Length > 1 ? p[1] : "", out st);
                    bool on = ((st >> 12) & 0xF) == 1;   // productState : bits 12-15 = 1 si la protection en temps réel est active
                    r.Say("Antivirus : " + p[0] + (on ? " (actif)" : " (inactif)"), on ? "ok" : "warn");
                }
                var dk = g("DISK").Split('|');
                if (dk.Length == 2) { int free, tot; int.TryParse(dk[0], out free); int.TryParse(dk[1], out tot); r.Say("Disque système : " + free + " Go libres / " + tot + " Go", free < 30 ? "warn" : "ok"); }
                int ram; int.TryParse(g("RAM"), out ram);
                r.Say("Mémoire : " + ram + " Go", ram > 0 && ram < 8 ? "warn" : "ok");
                r.Report.Add("Contrôles : BitLocker " + (bl == "1" ? "actif" : "inactif") + ", Windows " + (act == "1" ? "activé" : "NON activé") + ", antivirus " + (avs.Count > 0 ? string.Join(" / ", avs.Select(a => a.Split('|')[0])) : "aucun") + ", " + ram + " Go RAM");
                return true;
            }, null);
            return l;
        }
    }

    // ===================================================================== Fenêtre
    class MainForm : CyberForm
    {
        readonly TextBox tName;
        readonly Chip bGo, bRec, bNone, bCopy, bEdit;
        readonly Panel list;
        readonly LogBox box;
        readonly NeonBar bar;
        readonly List<Step> steps;
        bool busy;

        public MainForm() : base("PREPA_POSTE", 1100, 780)
        {
            SetSubtitle("Préparation d'un PC neuf  //  " + Environment.MachineName + "  //  by alex.b");
            Func<float, int> k = Theme.S;
            var ln = new Label { Text = "Nom du PC :", AutoSize = true, Location = new Point(k(16), k(16)), Tag = "muted" };
            tName = new TextBox { Text = Environment.MachineName, Location = new Point(k(100), k(12)), Width = k(170), Font = new Font("Consolas", 10f), CharacterCasing = CharacterCasing.Upper, MaxLength = 15 };
            bGo = new Chip("▶  PRÉPARER LE POSTE") { Primary = true, AccentOf = t => t.Ok, Location = new Point(k(284), k(8)) };
            bRec = new Chip("Recommandé") { AccentOf = t => t.Accent, Location = new Point(bGo.Right + k(8), k(8)) };
            bNone = new Chip("Tout décocher") { AccentOf = t => t.Muted, Location = new Point(bRec.Right + k(8), k(8)) };
            bCopy = new Chip("Copier le rapport") { AccentOf = t => t.Accent2, Location = new Point(bNone.Right + k(8), k(8)) };
            bEdit = new Chip("Listes logiciels / applis…") { AccentOf = t => t.Muted, Location = new Point(bCopy.Right + k(8), k(8)) };
            bar = new NeonBar { Location = new Point(k(16), k(48)), Size = new Size(Body.Width - k(32), k(6)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            list = new Panel { AutoScroll = true, Location = new Point(k(16), k(64)), Size = new Size(k(470), Body.Height - k(76)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom };
            box = new LogBox { Location = new Point(k(500), k(64)), Size = new Size(Body.Width - k(516), Body.Height - k(76)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Body.Controls.AddRange(new Control[] { ln, tName, bGo, bRec, bNone, bCopy, bEdit, bar, list, box });

            steps = Catalog.Build();
            int y = 0; string grp = null; var tip = new ToolTip();
            foreach (var s in steps)
            {
                if (s.Group != grp)
                {
                    grp = s.Group;
                    list.Controls.Add(new Label { Text = "// " + grp, AutoSize = true, Font = new Font("Consolas", 9f, FontStyle.Bold), Location = new Point(0, y + (y > 0 ? k(8) : 0)), Tag = "accent" });
                    y += k(y > 0 ? 30 : 22);
                }
                s.Box = new CheckBox { Text = s.Title, AutoSize = true, Location = new Point(k(8), y), Checked = Prefs.Ini.GetBool("Sel." + s.Id, s.Default) };
                if (s.Hint != null) tip.SetToolTip(s.Box, s.Hint);
                list.Controls.Add(s.Box); y += k(24);
            }
            bRec.Click += (s, e) => { foreach (var st in steps) st.Box.Checked = st.Default; };
            bNone.Click += (s, e) => { foreach (var st in steps) st.Box.Checked = false; };
            bGo.Click += (s, e) => Go();
            bCopy.Click += (s, e) => { if (box.TextLength > 0) { Clipboard.SetText(box.Text.Replace("\n", "\r\n")); bCopy.SetText("✓ Copié"); } };
            bEdit.Click += (s, e) =>
            {
                Catalog.Apps(); Catalog.Bloat();
                Process.Start("notepad.exe", "\"" + Catalog.AppsFile + "\""); Process.Start("notepad.exe", "\"" + Catalog.BloatFile + "\"");
                Info(this, "Modifie les listes puis relance PrépaPoste pour les voir.");
            };
            box.Line("PrépaPoste — coche les actions puis « PRÉPARER LE POSTE ».", "text", true);
            box.Line("Les réglages « Explorateur » s'appliquent au compte qui lance l'outil : ouvre la session de l'utilisateur final avant.", "muted");
            box.Line("Fichiers modifiables : " + Catalog.AppsFile, "muted");
        }

        void Go()
        {
            if (busy) return;
            var sel = steps.Where(s => s.Box.Checked).ToList();
            if (sel.Count == 0) { Info(this, "Aucune action cochée."); return; }
            var name = tName.Text.Trim().ToUpperInvariant();
            if (sel.Any(s => s.Id == "rename") && !Regex.IsMatch(name, @"^(?![0-9]+$)[A-Z0-9](?:[A-Z0-9\-]{0,13}[A-Z0-9])?$"))
            { Warn(this, "Nom de PC invalide : 1 à 15 caractères, lettres, chiffres et tirets (pas uniquement des chiffres, pas de tiret au début / à la fin)."); return; }
            if (!Ask(this, sel.Count + " action(s) vont être exécutées sur " + Environment.MachineName + ".\n\nUn point de restauration est créé si l'action est cochée.\nContinuer ?")) return;
            foreach (var s in steps) Prefs.Ini.Set("Sel." + s.Id, s.Box.Checked);
            Prefs.Save();
            busy = true; UseWaitCursor = true; bGo.Enabled = false; list.Enabled = false; box.Clear(); bar.Value = 0; bCopy.SetText("Copier le rapport");
            var r = new Runner(box) { NewName = name };
            var t0 = DateTime.Now;
            // le point de restauration passe en premier : filet de sécurité avant les autres changements
            var order = sel.OrderBy(s => s.Id == "restore" ? 0 : s.Id == "check" ? 2 : 1).ToList();
            Task.Run(() =>
            {
                box.Line("PRÉPARATION — " + Environment.MachineName + " — " + t0.ToString("dd/MM/yyyy HH:mm") + " (PrépaPoste, by alex.b)", "text", true);
                bool wingetChecked = false, wingetOk = false; int ok = 0, ko = 0, i = 0;
                foreach (var s in order)
                {
                    box.Line("", "text"); box.Line("▶ " + s.Title, "accent", true);
                    bool res;
                    if ((s.Id.StartsWith("app:") || s.Id == "upg") && !(wingetChecked ? wingetOk : (wingetChecked = true) && (wingetOk = r.WingetOk())))
                    { r.Say("winget absent : installer « Programme d'installation d'application » (App Installer) depuis le Microsoft Store", "bad"); res = false; }
                    else
                    {
                        try { res = s.Run(r); } catch (Exception ex) { r.Say("erreur : " + ex.Message, "bad"); AlexB.Log.Write(ex.ToString()); res = false; }
                    }
                    if (res) ok++; else ko++;
                    r.Report.Add((res ? "[OK] " : "[ÉCHEC] ") + s.Title);
                    i++; int done = i; BeginInvoke(new Action(() => bar.Value = (double)done / order.Count));
                }
                box.Line("", "text");
                box.Line("TERMINÉ en " + (int)(DateTime.Now - t0).TotalMinutes + " min : " + ok + " OK, " + ko + " échec(s)" + (r.NeedReboot ? " — REDÉMARRAGE NÉCESSAIRE" : ""), ko == 0 ? "ok" : "warn", true);
                try
                {
                    var dir = Path.Combine(AppInfo.DataDir, "rapports"); Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, "PrepaPoste_" + Environment.MachineName + "_" + t0.ToString("yyyy-MM-dd_HHmm") + ".txt");
                    File.WriteAllText(file, string.Join("\r\n", r.Report), new UTF8Encoding(true));
                    box.Line("Rapport : " + file, "muted");
                }
                catch { }
                BeginInvoke(new Action(() =>
                {
                    busy = false; UseWaitCursor = false; bGo.Enabled = true; list.Enabled = true;
                    if (r.NeedReboot && Ask(this, "Le PC doit redémarrer pour prendre son nouveau nom.\nRedémarrer maintenant ?"))
                        Proc.Run(Proc.Sys("shutdown.exe"), "/r /t 5 /c \"PrepaPoste : redemarrage pour renommer le PC\"", 10000, out _dummy);
                }));
            });
        }
        static string _dummy;
    }
}
