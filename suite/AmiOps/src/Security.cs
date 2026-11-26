// Tech IT AB - Audit de sécurité du poste (/100, corrections) et santé Active Directory (compte verrouillé)
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
    class Check { public string Name = "", State = "", Reco = "", FixLabel = ""; public int Points, Max; public bool Ok, Safe; public Func<string> Fix; }

    static class Audit
    {
        static int Reg(string path, string name, int def)
        {
            try { using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path)) return k == null ? def : Convert.ToInt32(k.GetValue(name, def)); } catch { return def; }
        }
        static string SetReg(string path, string name, int v) { using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).CreateSubKey(path)) k.SetValue(name, v, RegistryValueKind.DWord); return "ok"; }

        public static List<Check> Run(Action<string> step)
        {
            var l = new List<Check>();
            Action<string, int, bool, string, string, string, Func<string>, bool> add = (n, max, ok, st, reco, fl, fix, safe) => l.Add(new Check { Name = n, Max = max, Points = ok ? max : 0, Ok = ok, State = st, Reco = ok ? "" : reco, FixLabel = fl, Fix = ok ? null : fix, Safe = safe });

            step("collecte (30 s)");
            Dictionary<string, object> f = null;
            try
            {
                f = PanneScan.PowerShellJson("$o=[ordered]@{};" +
                    "$o.Bl=try{[string](Get-CimInstance -Namespace root\\cimv2\\Security\\MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -Filter \"DriveLetter='$env:SystemDrive'\" -ErrorAction Stop).ProtectionStatus}catch{'?'};" +
                    "$o.Fw=((Get-NetFirewallProfile|?{-not $_.Enabled}).Name -join ',');" +
                    "$o.Smb1=try{[string](Get-SmbServerConfiguration -ErrorAction Stop).EnableSMB1Protocol}catch{'?'};" +
                    "$o.Adm=try{((Get-LocalGroupMember -SID S-1-5-32-544 -ErrorAction Stop).Name -join ', ')}catch{'?'};" +
                    "$o.Bi=try{[string](Get-LocalUser|?{$_.SID -like '*-500'}).Enabled}catch{'?'};" +
                    "$o.Gu=try{[string](Get-LocalUser|?{$_.SID -like '*-501'}).Enabled}catch{'?'};" +
                    "$o.Hf=try{(Get-HotFix|?{$_.InstalledOn}|sort InstalledOn -Descending|select -First 1).InstalledOn.ToString('yyyy-MM-dd')}catch{''};" +
                    "$o.Ps2=try{[string](Get-WindowsOptionalFeature -Online -FeatureName MicrosoftWindowsPowerShellV2Root -ErrorAction Stop).State}catch{'?'};" +
                    "$o.Sb=try{[string](Confirm-SecureBootUEFI -ErrorAction Stop)}catch{'?'};" +
                    "$o.Tpm=try{[string](Get-CimInstance -Namespace root\\cimv2\\Security\\MicrosoftTpm -ClassName Win32_Tpm -ErrorAction Stop).SpecVersion}catch{''};" +
                    "$o.Lst=((Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue|?{$_.LocalAddress -ne '127.0.0.1' -and $_.LocalAddress -ne '::1'}|%{[string]$_.LocalPort+':'+(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}|sort -Unique) -join ' ');" +
                    "$o.Na=(net accounts) -join '|';" +
                    "[pscustomobject]$o|ConvertTo-Json -Compress").FirstOrDefault();
            }
            catch (Exception ex) { Log.Write("Audit : " + ex.Message); }
            Func<string, string> g = key => f != null && f.ContainsKey(key) && f[key] != null ? Convert.ToString(f[key]) : "?";

            var bl = g("Bl");
            add("Chiffrement BitLocker du disque système", 12, bl == "1", bl == "1" ? "actif" : bl == "0" ? "INACTIF" : "indisponible (édition Famille ?)", "activer BitLocker et sauvegarder la clé de récupération (compte Microsoft / AD / imprimée)", "Ouvrir BitLocker", () => { Process.Start("control.exe", "/name Microsoft.BitLockerDriveEncryption"); return "panneau BitLocker ouvert"; }, false);

            var avs = Optim.OtherAv(); bool avOn = false, avUp = false;
            foreach (var a in avs) { var p = a.Split('|'); int s; if (int.TryParse(p.Length > 1 ? p[1] : "", out s)) { avOn |= ((s >> 12) & 0xF) == 1; avUp |= ((s >> 4) & 0xF) == 0; } }
            add("Antivirus actif et à jour", 14, avOn && avUp, avs.Count == 0 ? "aucun antivirus" : string.Join(", ", avs.Select(a => a.Split('|')[0])) + (avOn ? " actif" : " INACTIF") + (avUp ? ", à jour" : ", DÉFINITIONS ANCIENNES"), "activer / mettre à jour l'antivirus", "Sécurité Windows", () => { Process.Start("windowsdefender:"); return "Sécurité Windows ouvert"; }, false);

            var fw = g("Fw");
            add("Pare-feu Windows (3 profils)", 10, fw.Length == 0, fw.Length == 0 ? "actif partout" : "DÉSACTIVÉ : " + fw, "réactiver le pare-feu", "Activer", () => { Sh.Cmd(Proc.Sys("netsh.exe"), "advfirewall set allprofiles state on", 30000); return "pare-feu activé"; }, true);

            int build = 0; try { using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) int.TryParse(Convert.ToString(k.GetValue("CurrentBuildNumber")), out build); } catch { }
            add("Version de Windows à jour (26H2)", 10, build >= 26300, Report.WinVersion(), build < 22000 ? "Windows 10 hors support depuis le 14/10/2025 : migrer vers Windows 11 ou ESU" : "installer Windows 11 26H2 (paquet d'activation via Windows Update)", "Windows Update", () => { Process.Start("ms-settings:windowsupdate"); return "Windows Update ouvert"; }, false);

            DateTime hf; bool recent = DateTime.TryParse(g("Hf"), out hf) && (DateTime.Now - hf).TotalDays <= 45;
            add("Mises à jour récentes (≤ 45 jours)", 8, recent, hf.Year > 2000 ? "dernière le " + hf.ToString("dd/MM/yyyy") : "inconnue", "installer les mises à jour (Optimisation > Mises à jour)", "Windows Update", () => { Process.Start("ms-settings:windowsupdate"); return "Windows Update ouvert"; }, false);

            int lua = Reg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 1), cp = Reg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin", 5);
            add("Contrôle de compte (UAC)", 6, lua == 1 && cp != 0, lua == 0 ? "DÉSACTIVÉ" : cp == 0 ? "élévation SANS confirmation" : "actif", "réactiver l'UAC (redémarrage)", "Activer", () => { SetReg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 1); SetReg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin", 5); return "UAC réactivé (effectif après redémarrage)"; }, true);

            var smb1 = g("Smb1");
            add("SMBv1 désactivé (WannaCry)", 8, smb1 == "False", smb1 == "True" ? "ACTIVÉ" : smb1 == "False" ? "désactivé" : "?", "désactiver SMBv1 (attention : vieux NAS / copieurs qui scannent en SMB1)", "Désactiver", () => { Sh.Ps("Set-SmbServerConfiguration -EnableSMB1Protocol $false -Force;Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart -ErrorAction SilentlyContinue|Out-Null", 300000); return "SMBv1 désactivé (redémarrage)"; }, false);

            var adm = g("Adm"); int nAdm = adm == "?" ? 0 : adm.Split(',').Length;
            add("Administrateurs locaux limités (≤ 2)", 6, nAdm > 0 && nAdm <= 2, adm, "l'utilisateur ne doit pas être administrateur au quotidien : compte standard + compte admin séparé", "Utilisateurs", () => { Process.Start("lusrmgr.msc"); return "gestion des utilisateurs ouverte"; }, false);

            add("Compte Invité désactivé", 4, g("Gu") != "True", g("Gu") == "True" ? "ACTIVÉ" : "désactivé", "désactiver le compte Invité", "Désactiver", () => { Sh.Ps("Get-LocalUser|?{$_.SID -like '*-501'}|Disable-LocalUser", 30000); return "Invité désactivé"; }, true);
            add("Compte Administrateur intégré désactivé", 4, g("Bi") != "True", g("Bi") == "True" ? "ACTIVÉ" : "désactivé", "désactiver l'Administrateur intégré (cible n°1 des attaques), après avoir vérifié qu'un autre compte admin existe", "Désactiver", () => { if (nAdm < 2) return "refusé : aucun autre administrateur détecté"; Sh.Ps("Get-LocalUser|?{$_.SID -like '*-500'}|Disable-LocalUser", 30000); return "Administrateur intégré désactivé"; }, false);

            int rdp = Reg(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", 1), nla = Reg(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication", 1);
            add("Bureau à distance sécurisé (NLA)", 6, rdp == 1 || nla == 1, rdp == 1 ? "Bureau à distance désactivé" : nla == 1 ? "activé avec NLA" : "activé SANS NLA", "activer l'authentification réseau (NLA)", "Activer NLA", () => { SetReg(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication", 1); return "NLA activé"; }, true);

            var ps2 = g("Ps2");
            add("PowerShell 2.0 supprimé", 3, ps2 != "Enabled", ps2 == "Enabled" ? "PRÉSENT (contourne la journalisation)" : "absent", "désactiver PowerShell 2.0", "Désactiver", () => { Sh.Ps("Disable-WindowsOptionalFeature -Online -FeatureName MicrosoftWindowsPowerShellV2Root -NoRestart|Out-Null", 300000); return "PowerShell 2.0 désactivé"; }, true);

            int llmnr = Reg(@"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast", 1);
            add("LLMNR désactivé (recommandation ANSSI)", 3, llmnr == 0, llmnr == 0 ? "désactivé" : "actif (vol d'empreintes réseau possible)", "désactiver LLMNR", "Désactiver", () => { SetReg(@"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast", 0); return "LLMNR désactivé"; }, true);

            int ar = Reg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun", 0x91);
            add("Exécution automatique des clés USB bloquée", 3, ar == 255, ar == 255 ? "bloquée" : "partielle", "bloquer l'exécution automatique sur tous les lecteurs", "Bloquer", () => { SetReg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun", 255); return "exécution automatique bloquée"; }, true);

            add("Secure Boot + TPM 2.0", 5, g("Sb") == "True" && g("Tpm").StartsWith("2.0"), "Secure Boot " + (g("Sb") == "True" ? "actif" : g("Sb") == "False" ? "INACTIF" : "non supporté (BIOS hérité)") + " · TPM " + (g("Tpm").Length > 0 ? g("Tpm").Split(',')[0] : "absent"), "activer le Secure Boot (et le TPM / PTT / fTPM) dans le BIOS", "", null, false);

            var na = g("Na"); var mlen = Regex.Match(na, @"(Longueur minimale du mot de passe|Minimum password length)\s*:\s*(\d+)"); var lck = Regex.Match(na, @"(Seuil de verrouillage|Lockout threshold)\s*:\s*(\w+)");
            int ml = mlen.Success ? int.Parse(mlen.Groups[2].Value) : 0; int lt; bool hasLock = lck.Success && int.TryParse(lck.Groups[2].Value, out lt) && lt > 0;
            add("Politique de mots de passe locale", 4, ml >= 12 && hasLock, "longueur minimale " + ml + " · verrouillage " + (hasLock ? lck.Groups[2].Value + " essais" : "AUCUN"), "12 caractères minimum, verrouillage après 10 essais (comptes locaux)", "Appliquer", () => { Sh.Cmd(Proc.Sys("net.exe"), "accounts /minpwlen:12 /lockoutthreshold:10 /lockoutwindow:15 /lockoutduration:15", 30000); return "politique appliquée (comptes locaux)"; }, false);

            var lst = g("Lst"); var risky = new Dictionary<string, string> { { "21", "FTP" }, { "23", "Telnet" }, { "3389", "Bureau à distance" }, { "5900", "VNC" }, { "5938", "TeamViewer" }, { "7070", "AnyDesk" }, { "445", "partage SMB" } };
            var open = lst.Split(' ').Where(x => x.Contains(":") && risky.ContainsKey(x.Split(':')[0])).Select(x => risky[x.Split(':')[0]] + " (" + x + ")").Distinct().ToList();
            add("Services exposés sur le réseau", 4, open.Count(o => !o.StartsWith("partage SMB")) == 0, open.Count == 0 ? "rien de sensible" : string.Join(", ", open), "fermer / désinstaller les accès à distance inutiles ; ne jamais exposer RDP ou VNC sur Internet", "", null, false);

            step("programmes au démarrage (signatures)");
            var stl = Startup.Scan(false, true); var sus = stl.Where(i => i.Suspicious && i.Sig != "Absent").ToList();
            add("Programmes au démarrage signés", 5, sus.Count == 0, sus.Count == 0 ? "tous signés" : sus.Count + " non signé(s) / suspect(s) : " + string.Join(", ", sus.Take(4).Select(i => i.Name)), "vérifier dans Dépannage > Démarrage & malwares (VirusTotal)", "", null, false);
            return l;
        }
    }

    class SecuPage : Page
    {
        readonly Grid grid; readonly Label score, scoreSub; readonly LogBox box; List<Check> last;
        public override string Title { get { return "Sécurité"; } }

        public SecuPage()
        {
            Lbl("AUDIT DE SÉCURITÉ", 0, k(2), "accent", 13f, true);
            Lbl("Note /100 inspirée des recommandations ANSSI / CIS pour un poste de travail. Corrections en un clic, réversibles par les réglages Windows.", k(230), k(10), "muted");
            int x = 0;
            x = Btn("▶  AUDITER CE POSTE", t => t.Ok, x, k(36), (s, e) => Go(), true).Right + k(8);
            x = Btn("Corriger la sélection", t => t.Warn, x, k(36), (s, e) => FixSel(false)).Right + k(6);
            x = Btn("Tout corriger (corrections sûres)", t => t.Accent, x, k(36), (s, e) => FixSel(true)).Right + k(6);
            x = Btn("Copier le rapport", t => t.Muted, x, k(36), (s, e) => { if (last != null) Clip.Text(Text_()); }).Right + k(6);
            Btn(Ed.Full ? "＋ Intervention avec cet audit" : "Enregistrer cet audit", t => t.Muted, x, k(36), (s, e) => { if (last != null) Shell.NewIntervention(null, Text_()); });
            score = new Label { Text = "—", AutoSize = false, Location = new Point(0, k(80)), Size = new Size(k(180), k(70)), TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Consolas", 32f, FontStyle.Bold), Tag = "muted" };
            scoreSub = new Label { Text = "/ 100", AutoSize = false, Location = new Point(0, k(150)), Size = new Size(k(180), k(40)), TextAlign = ContentAlignment.TopCenter, Tag = "muted" };
            grid = new Grid { CheckBoxes = true }; grid.Col("Contrôle", 270).Col("Points", 60).Col("État", 330).Col("Recommandation", 380);
            grid.Location = new Point(k(190), k(80)); grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            box = new LogBox { Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Controls.AddRange(new Control[] { score, scoreSub, grid, box });
            Resize += (s, e) => { int h = (ClientSize.Height - k(80)) * 70 / 100; grid.Size = new Size(ClientSize.Width - k(190), h); box.SetBounds(0, k(88) + h, ClientSize.Width, ClientSize.Height - k(88) - h); };
        }

        string Text_()
        {
            var sb = new StringBuilder("AUDIT DE SÉCURITÉ " + Environment.MachineName + " : " + score.Text + "/100 (" + Ed.Name + ")\r\n");
            foreach (var c in last) sb.AppendLine((c.Ok ? "[OK] " : "[À CORRIGER] ") + c.Name + " — " + c.State + (c.Reco.Length > 0 ? " → " + c.Reco : ""));
            return sb.ToString();
        }

        void Go()
        {
            if (Jobs.Busy("Audit de sécurité")) return; score.Text = "…";
            box.Line("", "text"); box.Line("▶ Audit — " + DateTime.Now.ToString("HH:mm:ss"), "accent", true);
            Jobs.Run("Audit de sécurité", null, j =>
            {
                var r = Audit.Run(s => { box.Line("  " + s + "…", "muted"); j.Record(s, "muted"); });
                j.Record(r.Sum(c => c.Points) * 100 / Math.Max(1, r.Sum(c => c.Max)) + "/100", "ok");
                BeginInvoke(new Action(() => Show(r)));
            });
        }

        void Show(List<Check> r)
        {
            last = r; var t = Theme.Current; int pts = r.Sum(c => c.Points), max = r.Sum(c => c.Max);
            int pct = max == 0 ? 0 : pts * 100 / max;
            score.Text = pct.ToString(); var col = pct >= 80 ? "ok" : pct >= 55 ? "warn" : "bad"; score.Tag = col; score.ForeColor = t.ByName(col);
            scoreSub.Text = "/ 100\r\n" + (pct >= 80 ? "poste bien protégé" : pct >= 55 ? "à renforcer" : "poste exposé");
            grid.BeginUpdate(); grid.Items.Clear();
            foreach (var c in r.OrderBy(c => c.Ok).ThenByDescending(c => c.Max))
                grid.Items.Add(new ListViewItem(new[] { c.Name, c.Points + "/" + c.Max, c.State, c.Reco + (c.Fix != null ? "  [" + c.FixLabel + (c.Safe ? ", sûr" : "") + "]" : "") }) { Tag = c, ForeColor = c.Ok ? t.Ok : c.Max >= 8 ? t.Bad : t.Warn });
            grid.EndUpdate();
            box.Line("Audit terminé : " + pct + "/100 — " + r.Count(c => !c.Ok) + " point(s) à corriger.", col);
        }

        void FixSel(bool safeAll)
        {
            if (last == null) return;
            var sel = safeAll ? last.Where(c => c.Fix != null && c.Safe).ToList() : grid.CheckedItems.Cast<ListViewItem>().Select(i => (Check)i.Tag).Where(c => c.Fix != null).ToList();
            if (sel.Count == 0) { CyberForm.Info(this, safeAll ? "Aucune correction sûre en attente." : "Coche les lignes à corriger (celles avec [ ])."); return; }
            if (!CyberForm.Ask(this, "Corriger :\n\n" + string.Join("\n", sel.Select(c => "• " + c.Name + " → " + c.FixLabel)) + "\n\nContinuer ?")) return;
            Jobs.Run("Corrections de sécurité (" + sel.Count + ")", "servicing", j =>
            {
                foreach (var c in sel) { j.Check(); try { var m = c.Name + " : " + c.Fix(); box.Line("  " + m, "ok"); j.Record(m, "ok"); } catch (Exception ex) { box.Line("  " + c.Name + " : " + ex.Message, "bad"); j.Record(c.Name + " : " + ex.Message, "bad"); } }
                BeginInvoke(new Action(Go));
            });
        }
    }

    // ===================================================================== Active Directory
#if !POSTE
    class AdPage : HubPage
    {
        public override string Title { get { return "Serveur AD"; } }

        public AdPage() : base("ACTIVE DIRECTORY", "Santé du domaine (à lancer sur un contrôleur de domaine) et comptes verrouillés (depuis n'importe quel poste du domaine).", 120)
        {
            T(Theme.Icon("", "♥"), "Santé du domaine", "dcdiag, réplication, FSMO, heure, DNS", t => t.Ok, Health);
            T(Theme.Icon("", "🔒"), "Compte verrouillé", "quel PC le bloque ?", t => t.Bad, LockSource, false);
            T(Theme.Icon("", "☺"), "Comptes verrouillés", "liste + déverrouiller", t => t.Warn, LockedList, false);
            T(Theme.Icon("", "▤"), "Stratégies appliquées", "gpresult de ce poste", t => t.Accent, () => Sh.Cmd(Proc.Sys("gpresult.exe"), "/r", 120000, l => Out("  " + l, "text")));
            T(Theme.Icon("", "↻"), "Forcer les GPO", "gpupdate /force", t => t.Accent, () => { Sh.Cmd(Proc.Sys("gpupdate.exe"), "/force", 300000, l => Out("  " + l, "text")); Out("Stratégies réappliquées.", "ok"); });
        }

        static int Role() { try { using (var s = new ManagementObjectSearcher("SELECT DomainRole FROM Win32_ComputerSystem")) foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["DomainRole"]); } catch { } return 0; }

        void Health()
        {
            int role = Role();
            if (role < 1 || role == 2) { Out("Ce poste n'est pas membre d'un domaine.", "warn"); return; }
            if (role < 4) Out("Ce poste n'est pas un contrôleur de domaine : seuls les contrôles DNS / heure sont faits ici. Lance " + Ed.Name + " sur un DC pour le bilan complet.", "warn");
            var dom = Sh.Ps("[System.DirectoryServices.ActiveDirectory.Domain]::GetCurrentDomain().Name", 60000).Trim();
            Out("Domaine : " + dom, "text");
            var srv = Sh.Ps("Resolve-DnsName -Type SRV -Name ('_ldap._tcp.dc._msdcs.'+" + Sh.Q(dom) + ") -ErrorAction SilentlyContinue|?{$_.NameTarget}|%{$_.NameTarget+':'+$_.Port}", 60000).Trim();
            Out(srv.Length > 0 ? "  DNS : contrôleurs annoncés → " + srv.Replace("\r", "").Replace("\n", ", ") : "  DNS : AUCUN enregistrement SRV des contrôleurs (DNS du poste mal configuré ?)", srv.Length > 0 ? "ok" : "bad");
            var tm = Sh.CmdOut(Proc.Sys("w32tm.exe"), "/query /status", 30000);
            var src = Regex.Match(tm, @"(Source|Source)\s*:\s*(.+)"); Out("  Heure : source " + (src.Success ? src.Groups[2].Value.Trim() : "?") + (Regex.IsMatch(tm, "Local CMOS|Free-running") ? " → NON SYNCHRONISÉ (Kerberos échoue au-delà de 5 min d'écart)" : ""), Regex.IsMatch(tm, "Local CMOS|Free-running") ? "bad" : "ok");
            if (role < 4) return;
            Out("", "text"); Out("// DCDIAG (erreurs uniquement)", "accent");
            int errs = 0;
            Sh.Cmd(Proc.Sys("dcdiag.exe"), "/q", 600000, l => { errs++; Out("  " + l, "bad"); });
            if (errs == 0) Out("  aucun test en échec", "ok");
            Out("", "text"); Out("// RÉPLICATION", "accent");
            var rep = Sh.CmdOut(Proc.Sys("repadmin.exe"), "/replsummary", 300000);
            foreach (var l in rep.Split('\n').Select(x => x.TrimEnd()).Where(x => x.Trim().Length > 0)) Out("  " + l, Regex.IsMatch(l, @"\s[1-9]\d*\s*/\s*\d+\s") ? "bad" : "text");
            Out("", "text"); Out("// RÔLES FSMO", "accent");
            foreach (var l in Sh.CmdOut(Proc.Sys("netdom.exe"), "query fsmo", 60000).Split('\n').Where(x => x.Trim().Length > 0)) Out("  " + l.TrimEnd(), "text");
            var shares = Sh.CmdOut(Proc.Sys("net.exe"), "share", 30000);
            bool sys = shares.Contains("SYSVOL"), net = shares.Contains("NETLOGON");
            Out("  Partages SYSVOL " + (sys ? "✓" : "ABSENT") + " · NETLOGON " + (net ? "✓" : "ABSENT") + (sys && net ? "" : " → GPO et scripts de connexion cassés (réplication DFSR ?)"), sys && net ? "ok" : "bad");
            Out("Bilan AD terminé.", errs == 0 ? "ok" : "warn");
        }

        void LockSource()
        {
            var u = Dlg.Ask(this, "Compte verrouillé", "Identifiant (sAMAccountName, ex. j.dupont)", "", false);
            if (u == null) return;
            if (!Regex.IsMatch(u, @"^[\w.\-$]{1,64}$")) { CyberForm.Warn(this, "Identifiant invalide."); return; }
            Start("Origine du verrouillage de " + u, () =>
            {
                var o = Sh.Ps("$u=" + Sh.Q(u) + ";$pdc=[System.DirectoryServices.ActiveDirectory.Domain]::GetCurrentDomain().PdcRoleOwner.Name;'PDC|'+$pdc;" +
                    "$e=([adsisearcher]\"(sAMAccountName=$u)\").FindOne();if($e){'LT|'+[string]$e.Properties['lockouttime'][0]+'|'+[string]$e.Properties['badpwdcount'][0]}else{'INCONNU'};" +
                    "try{Get-WinEvent -ComputerName $pdc -FilterHashtable @{LogName='Security';Id=4740} -MaxEvents 500 -ErrorAction Stop|?{$_.Properties[0].Value -eq $u}|select -First 15|%{'EV|'+$_.TimeCreated.ToString('dd/MM HH:mm:ss')+'|'+$_.Properties[1].Value}}catch{'ERR|'+$_.Exception.Message}", 300000);
                foreach (var l in o.Split('\n').Select(x => x.Trim()))
                {
                    var p = l.Split('|');
                    if (p[0] == "PDC") Out("  Contrôleur PDC interrogé : " + p[1], "muted");
                    else if (l == "INCONNU") Out("  Compte introuvable dans l'annuaire.", "bad");
                    else if (p[0] == "LT") { long lt; long.TryParse(p[1], out lt); Out("  " + (lt > 0 ? "Compte VERROUILLÉ depuis " + DateTime.FromFileTime(lt).ToString("dd/MM/yyyy HH:mm") : "Compte non verrouillé actuellement") + " · mots de passe erronés : " + p[2], lt > 0 ? "bad" : "ok"); }
                    else if (p[0] == "EV") Out("  " + p[1] + " verrouillé par l'ordinateur : " + p[2], "warn");
                    else if (p[0] == "ERR") Out("  Lecture du journal de sécurité du PDC impossible : " + p[1] + " (droits admin du domaine nécessaires, journal distant autorisé dans le pare-feu).", "bad");
                }
                Out("→ Sur le PC trouvé : mots de passe mémorisés (Gestionnaire d'identification), lecteurs réseau, téléphone (messagerie), tâches planifiées ou services avec l'ancien mot de passe.", "ok");
            });
        }

        void LockedList()
        {
            Start("Comptes verrouillés", () =>
            {
                var o = Sh.Ps("([adsisearcher]'(&(objectCategory=person)(objectClass=user)(lockoutTime>=1))').FindAll()|%{'U|'+$_.Properties['samaccountname'][0]+'|'+$_.Properties['displayname'][0]+'|'+[string]$_.Properties['lockouttime'][0]}", 120000);
                var users = o.Split('\n').Select(x => x.Trim().Split('|')).Where(p => p.Length >= 4 && p[0] == "U").ToList();
                if (users.Count == 0) { Out("Aucun compte verrouillé.", "ok"); return; }
                foreach (var p in users) { long lt; long.TryParse(p[3], out lt); Out("  " + p[1] + " (" + p[2] + ") depuis " + (lt > 0 ? DateTime.FromFileTime(lt).ToString("dd/MM HH:mm") : "?"), "warn"); }
                var name = (string)Invoke(new Func<string>(() => Dlg.Ask(this, "Déverrouiller", "Identifiant à déverrouiller (vide = aucun)", users[0][1], false)));
                if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, @"^[\w.\-$]{1,64}$")) return;
                var r = Sh.Ps("$e=([adsisearcher]\"(sAMAccountName=" + name.Replace("'", "") + ")\").FindOne().GetDirectoryEntry();$e.Properties['lockoutTime'].Value=0;$e.CommitChanges();'OK'", 60000);
                Out(r.Contains("OK") ? name + " déverrouillé." : "Déverrouillage impossible : " + r.Trim(), r.Contains("OK") ? "ok" : "bad");
            });
        }
    }
#endif
}
