// Tech IT AB - Atelier labo (équipe violette) : apprendre la détection sur SES PROPRES machines de labo autorisées.
//   - Simulations : rejoue des techniques MITRE ATT&CK INOFFENSIVES et auto-nettoyantes pour générer la télémétrie,
//     puis on les détecte avec Sysmon / Chasse aux menaces et on applique la remédiation.
//   - Catalogue Kali : fiches des vraies attaques (Responder, NetExec, Hydra, Nmap vuln, Metasploit, Impacket) —
//     commande à lancer DEPUIS Kali sur le périmètre labo, détection, remédiation. L'application ne lance rien d'offensif.
// Compilé uniquement dans Tech IT AB (exclu de PC Santé AB, comme la page Cyber).
// Développé by alex.b
//
// Garde-fous : périmètre LABO uniquement (RFC1918), confirmation « ce sont mes machines », journal d'audit.
// Offensif autorisé seulement sur ses labs / plateformes autorisées (HackTheBox, TryHackMe, Root-Me).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using AlexB;

namespace AmiOps
{
    static class Lab
    {
        // ---------------- périmètre labo (RFC1918 obligatoire)
        public static string ScopeText { get { return Prefs.Ini.Get("LabScope", ""); } }
        public static string Name { get { return Prefs.Ini.Get("LabName", "Labo"); } }
        public static bool Confirmed { get { return Prefs.Ini.GetBool("LabConfirmed", false); } }
        public static bool Ready { get { return Confirmed && Authenticity.Trusted && Ranges().Count > 0; } }

        public static List<Scope.Range> Ranges() { try { return Scope.ParseList(ScopeText); } catch { return new List<Scope.Range>(); } }

        // null = valide ; sinon la raison du refus. Refuse tout ce qui n'est pas privé (RFC1918/local).
        public static string Validate(string text)
        {
            List<Scope.Range> l;
            try { l = Scope.ParseList(text); } catch (Exception ex) { return ex.Message; }
            if (l.Count == 0) return "périmètre vide";
            if (l.Any(r => !Scope.Private(r))) return "le labo doit être un réseau privé (10.x, 172.16–31.x, 192.168.x) ou local : adresse publique refusée";
            if (l.Any(r => r.Count > 65536)) return "réseau plus grand qu'un /16 refusé";
            return null;
        }

        public static void SetScope(string text, string name, bool confirmed)
        {
            Prefs.Ini.Set("LabScope", (text ?? "").Trim()); Prefs.Ini.Set("LabName", string.IsNullOrWhiteSpace(name) ? "Labo" : name.Trim());
            Prefs.Ini.Set("LabConfirmed", confirmed); Prefs.Save();
        }

        static string Dotted(uint v) { return ((v >> 24) & 255) + "." + ((v >> 16) & 255) + "." + ((v >> 8) & 255) + "." + (v & 255); }

        // une IP cible d'exemple dans le périmètre (pour les commandes du catalogue)
        public static string Target()
        {
            var r = Ranges().FirstOrDefault(); if (r == null) return "<ip-cible-labo>";
            if (r.Count == 1) return Dotted(r.A);
            uint host = r.A + (uint)Math.Min(100, r.Count - 1);
            return Dotted(host);
        }
        public static string Subnet() { var r = Ranges().FirstOrDefault(); return r == null ? "<reseau-labo>" : r.Src; }

        public static void Audit(string action)
        {
            try { File.AppendAllText(Path.Combine(Store.Dir, "lab-audit.log"), DateTime.Now.ToString("s") + " | " + Environment.UserName + " | " + Name + " | " + action + Environment.NewLine, new UTF8Encoding(false)); } catch { }
        }

        // ---------------- simulations inoffensives (auto-nettoyantes)
        public class Atomic
        {
            public string Id, Mitre, Title, What, Detect, Run, Clean;
            public bool Cmd;   // true = exécuté par cmd/outil, false = PowerShell
        }

        public static readonly List<Atomic> Sims = new List<Atomic>
        {
            new Atomic { Id = "runkey", Mitre = "T1547.001", Title = "Persistance par clé Run", What = "Ajoute une valeur HKCU\\...\\Run (vers la calculatrice), puis la retire.",
                Detect = "Sysmon event 13 (clé Run modifiée) · Autoruns · Persistance",
                Run = "New-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' -Name 'ATB_Sim' -Value \"$env:SystemRoot\\System32\\calc.exe\" -PropertyType String -Force|Out-Null;'ok'",
                Clean = "Remove-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' -Name 'ATB_Sim' -EA SilentlyContinue;'ok'" },
            new Atomic { Id = "encps", Mitre = "T1059.001", Title = "PowerShell encodé", What = "Lance une commande PowerShell encodée en Base64 (affiche un texte, sans effet).", Cmd = true,
                Detect = "Sysmon event 1 (ligne de commande -enc) · journal PowerShell 4104 · Chasse aux menaces",
                Run = "powershell.exe -NoProfile -EncodedCommand VwByAGkAdABlAC0ASABvAHMAdAAgACcAUwBpAG0AdQBsAGEAdABpAG8AbgAgAEEAVABUACYAQwBLACAALQAgAGIAZQBuAGkAZwBuACcA",
                Clean = "" },
            new Atomic { Id = "schtask", Mitre = "T1053.005", Title = "Tâche planifiée", What = "Crée une tâche planifiée (cmd /c exit), puis la supprime.", Cmd = true,
                Detect = "Événement 4698 (tâche créée) · Sysmon · Persistance",
                Run = "schtasks /create /tn ATB_Sim /tr \"cmd /c exit\" /sc once /st 23:59 /f",
                Clean = "schtasks /delete /tn ATB_Sim /f" },
            new Atomic { Id = "lolbin", Mitre = "T1105 / T1218", Title = "LOLBin certutil (localhost)", What = "Utilise certutil pour « télécharger » depuis 127.0.0.1 (échoue sans réseau sortant).", Cmd = true,
                Detect = "Sysmon event 1 (certutil -urlcache) · Chasse aux menaces",
                Run = "certutil.exe -urlcache -split -f http://127.0.0.1:9/none %TEMP%\\atb_sim.bin",
                Clean = "del /q %TEMP%\\atb_sim.bin 2>nul & certutil -urlcache * delete >nul 2>nul & exit /b 0" },
            new Atomic { Id = "discovery", Mitre = "T1087 / T1033", Title = "Reconnaissance de comptes", What = "Liste les comptes et groupes locaux (lecture seule).", Cmd = true,
                Detect = "Sysmon event 1 (net.exe, whoami) · corrélation Wazuh",
                Run = "whoami /groups & net user & net localgroup administrators",
                Clean = "" },
            new Atomic { Id = "startup", Mitre = "T1547.001", Title = "Fichier dans le Démarrage", What = "Dépose un .cmd inoffensif dans le dossier Démarrage, puis le retire.",
                Detect = "Sysmon event 11 (fichier créé dans Startup) · Autoruns · Persistance",
                Run = "$p=[Environment]::GetFolderPath('Startup')+'\\ATB_Sim.cmd';Set-Content -LiteralPath $p -Value 'rem simulation benign' -Force;'ok'",
                Clean = "Remove-Item -LiteralPath ([Environment]::GetFolderPath('Startup')+'\\ATB_Sim.cmd') -EA SilentlyContinue;'ok'" },
        };

        // exécute une simulation (génère la télémétrie) puis nettoie immédiatement. Réservé au périmètre labo confirmé.
        public static void RunSim(Atomic a, Action<string, string> log)
        {
            if (!Ready) { log("Atelier labo non prêt : déclare ton périmètre labo et confirme que ce sont tes machines.", "bad"); return; }
            Audit("SIMULATION " + a.Id + " (" + a.Mitre + ")");
            log("▶ Simulation " + a.Mitre + " — " + a.Title, "accent");
            log("  " + a.What, "muted");
            try
            {
                if (a.Cmd) Sh.Cmd(Env(), "/c " + a.Run, 60000, null, Oem()); else Sh.Ps(a.Run, 60000);
                log("  exécutée. À détecter : " + a.Detect, "ok");
            }
            catch (Exception ex) { log("  erreur : " + ex.Message, "warn"); }
            finally
            {
                if (a.Clean.Length > 0)
                {
                    try { if (a.Cmd) Sh.Cmd(Env(), "/c " + a.Clean, 30000, null, Oem()); else Sh.Ps(a.Clean, 30000); log("  nettoyé (aucune trace laissée).", "muted"); }
                    catch { log("  nettoyage à vérifier manuellement : " + a.Id, "warn"); }
                }
            }
        }
        static string Env() { return Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\cmd.exe"; }
        static Encoding Oem() { try { return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { return Encoding.UTF8; } }

        // ---------------- catalogue des vraies attaques (depuis Kali, sur le périmètre labo) — AFFICHAGE SEULEMENT
        public class Play { public string Name, Tool, Mitre, Cmd, Detect, Remediate; }

        public static List<Play> Catalog()
        {
            var t = Target(); var net = Subnet();
            return new List<Play>
            {
                new Play { Name = "Empoisonnement LLMNR/NBT-NS", Tool = "Responder (Kali)", Mitre = "T1557.001",
                    Cmd = "sudo responder -I eth0 -wv",
                    Detect = "Durcissement : LLMNR/NetBIOS doivent être désactivés · Sysmon · requêtes de noms anormales.",
                    Remediate = "Désactiver LLMNR et NetBIOS (bouton « Durcir anti-scan » et audit CIS) ; exiger la signature SMB." },
                new Play { Name = "Énumération SMB / spray", Tool = "NetExec / nxc (Kali)", Mitre = "T1110.003 / T1087",
                    Cmd = "nxc smb " + net + " -u users.txt -p 'Hiver2026!' --continue-on-success",
                    Detect = "Rafale d'événements 4625 (Chasse aux menaces) · verrouillages de comptes.",
                    Remediate = "Politique de verrouillage, mots de passe forts + MFA, signature SMB, désactiver SMBv1 (audit CIS)." },
                new Play { Name = "Force brute d'un service", Tool = "Hydra (Kali)", Mitre = "T1110",
                    Cmd = "hydra -l admin -P rockyou.txt " + t + " rdp",
                    Detect = "4625 en rafale depuis une même source (Chasse aux menaces) ; journal du service.",
                    Remediate = "Verrouillage de compte, MFA, limiter l'exposition RDP (NLA, VPN), fail2ban côté Linux." },
                new Play { Name = "Scan de vulnérabilités", Tool = "Nmap --script vuln (Kali)", Mitre = "T1595.002",
                    Cmd = "nmap -sV --script vuln " + net,
                    Detect = "Journal des paquets bloqués du pare-feu (« Scans reçus », « Surveiller les scans »).",
                    Remediate = "Fermer les ports inutiles, corriger les CVE, pare-feu en blocage par défaut (durcissement)." },
                new Play { Name = "Exploitation SMB (lab Windows non à jour)", Tool = "Metasploit (Kali)", Mitre = "T1210",
                    Cmd = "msfconsole -q -x 'use exploit/windows/smb/ms17_010_eternalblue; set RHOSTS " + t + "; run'",
                    Detect = "Defender/Sysmon (injection, process anormal) · event 7045 (service).",
                    Remediate = "Appliquer les mises à jour, désactiver SMBv1 (audit CIS), segmenter le réseau." },
                new Play { Name = "Vol d'identifiants / mouvement latéral", Tool = "Impacket (Kali)", Mitre = "T1003 / T1021.002",
                    Cmd = "impacket-secretsdump LAB/administrateur@" + t + "   ·   impacket-psexec LAB/administrateur@" + t,
                    Detect = "Sysmon event 10 (accès à lsass) · 4624 type 3 · 7045 (service créé).",
                    Remediate = "RunAsPPL (protège lsass), LAPS, comptes d'admin séparés, restreindre l'admin distant." },
            };
        }
    }

    // ===================================================================== fenêtre Atelier labo
    class LabDialog : Form
    {
        static int k(int v) { return Theme.S(v); }

        public LabDialog()
        {
            Text = "Atelier labo (équipe violette) · by alex.b"; Size = new Size(k(1000), k(620)); StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Current.Bg; ShowInTaskbar = false; Font = new Font("Segoe UI", 9.5f);
            var warn = new Label { Dock = DockStyle.Top, Height = k(30), Tag = "warn", Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Text = "   Entraînement sur TES machines de labo autorisées uniquement (RFC1918, HackTheBox, TryHackMe, Root-Me). Jamais sur un tiers." };
            var tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10f) };
            tabs.TabPages.Add(ScopeTab());
            tabs.TabPages.Add(SimTab());
            tabs.TabPages.Add(CatalogTab());
            Controls.Add(tabs); Controls.Add(warn);
            CyberForm.Recolor(this);
        }

        TabPage ScopeTab()
        {
            var p = new TabPage("Périmètre labo") { BackColor = Theme.Current.Bg };
            var st = new Label { Location = new Point(k(16), k(16)), Size = new Size(k(940), k(40)), Tag = "muted",
                Text = Lab.Ready ? "Périmètre labo : " + Lab.Name + " — " + Lab.Subnet() + " · cible d'exemple " + Lab.Target() : "Aucun périmètre labo confirmé : les simulations sont désactivées." };
            var b = new Chip("Déclarer / modifier le périmètre labo…") { AccentOf = t => t.Warn, Location = new Point(k(16), k(64)), Size = new Size(k(320), k(34)) };
            b.Click += (s, e) =>
            {
                var fn = new F("Nom du labo", Lab.Name);
                var fs = new F("Périmètre labo — un réseau privé par ligne (ex. 192.168.56.0/24, 10.10.0.0/16). Adresse publique refusée.", Lab.ScopeText.Length > 0 ? Lab.ScopeText : string.Join("\r\n", Scope.LocalNets())) { Multi = true, Lines = 4, Required = true };
                var fc = new F("Je confirme que ce sont MES machines de labo (ou une plateforme autorisée)", Lab.Confirmed ? "Oui" : "Non") { Options = new[] { "Oui", "Non" }, Editable = false };
                if (!Dlg.Edit(this, "Périmètre labo", fn, fs, fc)) return;
                var err = Lab.Validate(fs.Value); if (err != null) { CyberForm.Warn(this, "Périmètre refusé : " + err + "."); return; }
                if (fc.Value != "Oui") { CyberForm.Warn(this, "Sans confirmation que ce sont tes machines, l'atelier reste désactivé."); Lab.SetScope(fs.Value, fn.Value, false); return; }
                Lab.SetScope(fs.Value, fn.Value, true); Lab.Audit("périmètre confirmé : " + fs.Value.Replace("\r", "").Replace("\n", ", "));
                st.Text = "Périmètre labo : " + Lab.Name + " — " + Lab.Subnet() + " · cible d'exemple " + Lab.Target(); st.ForeColor = Theme.Current.Muted;
                CyberForm.Info(this, "Périmètre enregistré. Onglets Simulations et Catalogue prêts.");
            };
            p.Controls.Add(st); p.Controls.Add(b);
            return p;
        }

        TabPage SimTab()
        {
            var p = new TabPage("Simulations (sûres)") { BackColor = Theme.Current.Bg };
            var info = new Label { Dock = DockStyle.Top, Height = k(42), Tag = "muted",
                Text = "  Rejoue une technique ATT&CK INOFFENSIVE et auto-nettoyante pour générer la télémétrie, puis détecte-la (Sysmon / Chasse aux menaces) et applique la remédiation." };
            var g = new Grid { Dock = DockStyle.Fill }; g.Col("MITRE", 110).Col("Technique", 230).Col("Ce que ça fait", 360).Col("À détecter", 260);
            foreach (var a in Lab.Sims) g.Items.Add(new ListViewItem(new[] { a.Mitre, a.Title, a.What, a.Detect }) { Tag = a });
            var bar = new Panel { Dock = DockStyle.Bottom, Height = k(48) };
            var run = new Chip("▶ Lancer la simulation sélectionnée") { AccentOf = t => t.Accent2, Location = new Point(k(8), k(8)), Size = new Size(k(300), k(32)), Primary = true };
            var log = new LogBox { Dock = DockStyle.Right, Width = k(380) };
            run.Click += (s, e) =>
            {
                var a = g.Sel as Lab.Atomic; if (a == null) { CyberForm.Info(this, "Choisis une simulation."); return; }
                if (!Lab.Ready) { CyberForm.Warn(this, "Déclare d'abord ton périmètre labo (onglet Périmètre labo)."); return; }
                if (!CyberForm.Ask(this, "Lancer la simulation « " + a.Title + " » (" + a.Mitre + ") sur CE poste de labo (" + Environment.MachineName + ") ?\n\n" + a.What + "\nInoffensive et auto-nettoyante.")) return;
                log.Clear(); System.Threading.Tasks.Task.Run(() => Lab.RunSim(a, (t, c) => { try { if (log.IsHandleCreated) log.BeginInvoke(new Action(() => log.Line(t, c))); } catch { } }));
            };
            bar.Controls.Add(run);
            p.Controls.Add(g); p.Controls.Add(log); p.Controls.Add(bar); p.Controls.Add(info);
            return p;
        }

        TabPage CatalogTab()
        {
            var p = new TabPage("Catalogue Kali (guidé)") { BackColor = Theme.Current.Bg };
            var info = new Label { Dock = DockStyle.Top, Height = k(42), Tag = "muted",
                Text = "  Vraies attaques à lancer DEPUIS Kali sur ton périmètre labo. " + Ed.Name + " ne les exécute pas : il te donne la commande, la détection et la remédiation." };
            var g = new Grid { Dock = DockStyle.Fill }; g.Col("Attaque", 230).Col("Outil (Kali)", 150).Col("MITRE", 120).Col("Commande", 440);
            foreach (var pl in Lab.Catalog()) g.Items.Add(new ListViewItem(new[] { pl.Name, pl.Tool, pl.Mitre, pl.Cmd }) { Tag = pl });
            var detail = new LogBox { Dock = DockStyle.Bottom, Height = k(150) };
            g.SelectedIndexChanged += (s, e) =>
            {
                var pl = g.Sel as Lab.Play; if (pl == null) return; detail.Clear();
                detail.Line("// " + pl.Name + " · " + pl.Mitre, "accent", true);
                detail.Line("Commande (Kali, cible labo " + Lab.Target() + ") : " + pl.Cmd, "text");
                detail.Line("Détection : " + pl.Detect, "warn");
                detail.Line("Remédiation : " + pl.Remediate, "ok");
            };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = k(40) };
            var copy = new Chip("Copier la commande") { AccentOf = t => t.Accent, Location = new Point(k(8), k(4)), Size = new Size(k(200), k(30)) };
            copy.Click += (s, e) => { var pl = g.Sel as Lab.Play; if (pl != null) try { Clipboard.SetText(pl.Cmd); CyberForm.Info(this, "Commande copiée."); } catch { } };
            bar.Controls.Add(copy);
            p.Controls.Add(g); p.Controls.Add(bar); p.Controls.Add(detail); p.Controls.Add(info);
            return p;
        }
    }
}
