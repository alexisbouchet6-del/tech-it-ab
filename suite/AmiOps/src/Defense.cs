// Tech IT AB - renforts cyber défensifs : Sysmon (journalisation fine + lecture des événements à risque),
// Autoruns (démarrage complet, signatures, VirusTotal), audit de durcissement CIS / ANSSI.
// Compilé uniquement dans Tech IT AB (exclu de PC Santé AB, comme la page Cyber).
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AlexB;

namespace AmiOps
{
    // ===================================================================== Sysmon (Sysinternals) : télémétrie détaillée
    static class Sysmon
    {
        public const string Pkg = "Microsoft.Sysinternals.Sysmon";
        static string F(string s) { return (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", " "); }

        // service Sysmon / Sysmon64 présent et en service ?
        public static bool Installed()
        {
            var o = Sh.Ps("$s=Get-Service -Name Sysmon,Sysmon64 -EA SilentlyContinue|select -First 1;if($s){'OUI|'+$s.Status}else{'NON'}", 30000).Trim();
            return o.StartsWith("OUI");
        }

        // configuration compacte, écrite par alex.b (haut signal, faible bruit) : ProcessCreate, NetworkConnect,
        // CreateRemoteThread, ProcessAccess (lsass), FileCreate (démarrage), RegistryValueSet (Run), DnsQuery
        public const string Config =
            "<Sysmon schemaversion=\"4.90\">\n <HashAlgorithms>SHA256</HashAlgorithms>\n <EventFiltering>\n" +
            "  <ProcessCreate onmatch=\"exclude\"><Image condition=\"end with\">\\MsMpEng.exe</Image></ProcessCreate>\n" +
            "  <NetworkConnect onmatch=\"include\"><Image condition=\"contains\">\\Temp\\</Image><Image condition=\"contains\">\\AppData\\</Image></NetworkConnect>\n" +
            "  <CreateRemoteThread onmatch=\"include\"><SourceImage condition=\"is not\">x</SourceImage></CreateRemoteThread>\n" +
            "  <ProcessAccess onmatch=\"include\"><TargetImage condition=\"end with\">\\lsass.exe</TargetImage></ProcessAccess>\n" +
            "  <FileCreate onmatch=\"include\"><TargetFilename condition=\"contains\">\\Start Menu\\Programs\\Startup\\</TargetFilename></FileCreate>\n" +
            "  <RegistryEvent onmatch=\"include\"><TargetObject condition=\"contains\">\\CurrentVersion\\Run</TargetObject></RegistryEvent>\n" +
            "  <DnsQuery onmatch=\"exclude\"><QueryName condition=\"end with\">.microsoft.com</QueryName></DnsQuery>\n" +
            " </EventFiltering>\n</Sysmon>\n";

        public static void Install(bool withConfig, Action<string, string> log)
        {
            log("Installation de Sysmon (Microsoft Sysinternals) via winget…", "muted");
            int rc = Sh.Cmd("winget", "install --id " + Pkg + " -e --silent --source winget --accept-package-agreements --accept-source-agreements", 900000, null, Encoding.UTF8);
            if (rc != 0 && rc != unchecked((int)0x8A15002B)) { log("winget : code 0x" + rc.ToString("X8") + " (winget absent ? installer « Programme d'installation d'application »)", "bad"); return; }
            log("Sysmon installé.", "ok");
            if (!withConfig) return;
            var cfg = Path.Combine(Hardening.SecureTempDir(Ed.Id + "-sysmon"), "sysmon-techitab.xml");
            File.WriteAllText(cfg, Config, new UTF8Encoding(false));
            var o = Sh.Ps("$e=(Get-Command sysmon64.exe,sysmon.exe -EA SilentlyContinue|select -First 1).Source;if(-not $e){'ABSENT'}else{& $e -accepteula -i " + Sh.Q(cfg) + " 2>&1|Out-String;'OK'}", 120000);
            log(o.Contains("OK") ? "Configuration appliquée (journal : Applications et services > Microsoft > Windows > Sysmon)." : "Sysmon installé mais configuration à appliquer manuellement : " + o.Trim(), o.Contains("OK") ? "ok" : "warn");
        }

        // lecture des événements Sysmon à fort signal (champs nommés via XML : robuste entre versions)
        public const string ReadScript =
            "function F($s){([string]$s) -replace '[\\r\\n|]',' '};$d=(Get-Date).AddDays(-3);" +
            "function Q($i,$n){try{Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Sysmon/Operational';Id=$i;StartTime=$d} -MaxEvents $n -EA Stop}catch{@()}};" +
            "function D($ev){$h=@{};([xml]$ev.ToXml()).Event.EventData.Data|%{$h[$_.Name]=[string]$_.'#text'};$h};" +
            "Q 8 300|%{$e=D $_;'S|8|'+(F $e['SourceImage'])+'|'+(F $e['TargetImage'])+'|'+$_.TimeCreated.ToString('s')};" +
            "Q 10 1000|?{$e=D $_;$e['TargetImage'] -like '*\\lsass.exe' -and $e['GrantedAccess'] -match '0x1010|0x1410|0x1438|0x143a|0x1fffff'}|%{$e=D $_;'S|10|'+(F $e['SourceImage'])+'|'+(F $e['GrantedAccess'])+'|'+$_.TimeCreated.ToString('s')};" +
            "Q 1 2000|?{$e=D $_;$e['CommandLine'] -match '(?i)-e(nc|ncodedcommand)?\\s|-w(indowstyle)?\\s+h|downloadstring|downloadfile|iex\\s*\\(|frombase64string|certutil.+-urlcache|bitsadmin.+/transfer|regsvr32.+/i:http|mshta.+http|rundll32.+javascript'}|select -First 40|%{$e=D $_;'S|1|'+(F $e['Image'])+'|'+(F $e['CommandLine'].Substring(0,[Math]::Min(150,$e['CommandLine'].Length)))+'|'+$_.TimeCreated.ToString('s')};" +
            "Q 11 500|%{$e=D $_;'S|11|'+(F $e['Image'])+'|'+(F $e['TargetFilename'])+'|'+$_.TimeCreated.ToString('s')};" +
            "Q 13 500|%{$e=D $_;'S|13|'+(F $e['Image'])+'|'+(F $e['TargetObject'])+' = '+(F $e['Details'])+'|'+$_.TimeCreated.ToString('s')};" +
            "'S|END|||'";

        static readonly Dictionary<string, string[]> Map = new Dictionary<string, string[]>
        {   // id Sysmon → niveau, titre, MITRE, recommandation
            { "8", new[] { "bad", "Injection de thread distant (CreateRemoteThread)", "T1055 Injection de processus", "Technique d'injection : identifier le processus source, analyser, isoler si malveillant." } },
            { "10", new[] { "bad", "Accès à la mémoire de lsass.exe", "T1003.001 Vol d'identifiants (LSASS)", "Vol de mots de passe probable (Mimikatz…) : isoler le poste, changer les mots de passe, activer RunAsPPL." } },
            { "1", new[] { "bad", "Processus suspect (interpréteur détourné)", "T1059 / T1218 Exécution", "Ligne de commande typique d'un malware : analyser le script, isoler si confirmé." } },
            { "11", new[] { "warn", "Fichier déposé dans le Démarrage", "T1547.001 Persistance au démarrage", "Vérifier le fichier et son éditeur ; supprimer s'il est inconnu." } },
            { "13", new[] { "warn", "Clé Run modifiée", "T1547.001 Persistance (registre)", "Vérifier la valeur ajoutée à la clé Run." } },
        };

        public static List<CyFinding> Eval(string output)
        {
            var l = new List<CyFinding>();
            foreach (var r in (output ?? "").Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.StartsWith("S|")).Select(x => x.Split('|')).Where(p => p.Length >= 5 && p[1] != "END"))
            {
                string[] d; if (!Map.TryGetValue(r[1], out d)) continue;
                string a = r[2], b = r[3];
                l.Add(new CyFinding { Level = d[0], Host = Environment.MachineName, Item = "Sysmon " + r[1] + (r[4].Length > 0 ? " · " + r[4].Replace("T", " ") : ""), Title = d[1] + " : " + Cut(r[1] == "10" || r[1] == "8" ? a + " → " + b : a.Length > 0 && b.Length > 0 ? a + " · " + b : a + b), Fix = d[3], Ref = "MITRE " + d[2] });
            }
            return l.OrderBy(x => x.Level == "bad" ? 0 : x.Level == "warn" ? 1 : 2).ToList();
        }
        static string Cut(string s) { s = (s ?? "").Trim(); return s.Length > 150 ? s.Substring(0, 150) + "…" : s; }
    }

    // ===================================================================== Autoruns (Sysinternals) : tout ce qui démarre
    static class Autoruns
    {
        public const string Pkg = "Microsoft.Sysinternals.Autoruns";

        public static string Exe()
        {
            foreach (var n in new[] { "autorunsc64.exe", "autorunsc.exe" })
            {
                var o = Sh.Ps("(Get-Command " + n + " -EA SilentlyContinue).Source", 15000).Trim();
                if (o.Length > 0 && File.Exists(o)) return o;
            }
            return null;
        }

        public static string Run(Action<string, string> log)
        {
            var exe = Exe();
            if (exe == null)
            {
                log("Installation d'Autoruns (Microsoft Sysinternals) via winget…", "muted");
                int rc = Sh.Cmd("winget", "install --id " + Pkg + " -e --silent --source winget --accept-package-agreements --accept-source-agreements", 900000, null, Encoding.UTF8);
                if (rc != 0 && rc != unchecked((int)0x8A15002B)) throw new Exception("winget : code 0x" + rc.ToString("X8"));
                exe = Exe(); if (exe == null) throw new Exception("autorunsc introuvable après installation");
            }
            log("Analyse du démarrage (autorunsc, signatures)…", "muted");
            // -a * tout, -c CSV, -h hash, -s vérifie les signatures, -t heure, -nobanner ; sortie OEM
            return Sh.CmdOut(exe, "-accepteula -nobanner -a * -c -h -s -t *", 300000, Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage));
        }

        // découpe une ligne CSV (gère les champs entre guillemets et les virgules internes)
        public static List<string> Csv(string line)
        {
            var l = new List<string>(); var sb = new StringBuilder(); bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(c); }
                else if (c == '"') q = true;
                else if (c == ',') { l.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            l.Add(sb.ToString()); return l;
        }

        static readonly Regex UserDir = new Regex(@"\\(AppData|Temp|Downloads|Téléchargements|Users\\Public|Windows\\Temp)\\", RegexOptions.IgnoreCase);

        public static List<CyFinding> Eval(string csv, out int total)
        {
            total = 0; var l = new List<CyFinding>();
            var lines = (csv ?? "").Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Length > 0).ToList();
            if (lines.Count == 0) return l;
            var head = Csv(lines[0]).Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();
            Func<List<string>, string, string> col = (row, name) => { int i = head.FindIndex(h => h == name); if (i < 0) i = head.FindIndex(h => h.Contains(name)); return i >= 0 && i < row.Count ? row[i].Trim() : ""; };
            foreach (var line in lines.Skip(1))
            {
                var row = Csv(line); if (row.Count < 3) continue; total++;
                string loc = col(row, "entry location"), entry = col(row, "entry"), signer = col(row, "signer"), img = col(row, "image path"), vt = col(row, "virus"), company = col(row, "company");
                if (entry.Length == 0 || img.Equals("File not found", StringComparison.OrdinalIgnoreCase) && loc.ToLowerInvariant().Contains("task")) continue;
                bool verified = signer.StartsWith("(Verified)", StringComparison.OrdinalIgnoreCase);
                int vtMal = 0; var mv = Regex.Match(vt, @"^(\d+)\s*/\s*\d+"); if (mv.Success) int.TryParse(mv.Groups[1].Value, out vtMal);
                var where = loc + " · " + entry;
                if (vtMal >= 3) l.Add(new CyFinding { Level = "bad", Host = Environment.MachineName, Item = "Autoruns · " + where, Title = vtMal + " moteurs VirusTotal : " + Cut(img), Fix = "Supprimer l'entrée (Autoruns) et le fichier ; analyser le poste.", Ref = "MITRE T1547 · VirusTotal" });
                else if (img.Equals("File not found", StringComparison.OrdinalIgnoreCase)) l.Add(new CyFinding { Level = "info", Host = Environment.MachineName, Item = "Autoruns · " + where, Title = "Entrée orpheline (fichier absent)", Fix = "Nettoyer l'entrée si le logiciel a été désinstallé.", Ref = "" });
                else if (!verified && img.Length > 0)
                {
                    bool user = UserDir.IsMatch(img);
                    l.Add(new CyFinding { Level = user ? "bad" : "warn", Host = Environment.MachineName, Item = "Autoruns · " + where, Title = "Non signé" + (user ? " dans un dossier utilisateur" : "") + " : " + Cut(img) + (company.Length > 0 ? " (" + company + ")" : ""), Fix = user ? "Emplacement typique d'un malware : vérifier sur VirusTotal (« Fichier suspect »), supprimer si inconnu." : "Vérifier l'éditeur et l'utilité ; désactiver dans Autoruns si inutile.", Ref = "MITRE T1547.001" });
                }
            }
            return l.OrderBy(x => x.Level == "bad" ? 0 : x.Level == "warn" ? 1 : 2).ToList();
        }
        static string Cut(string s) { s = (s ?? "").Trim(); return s.Length > 120 ? s.Substring(0, 120) + "…" : s; }
    }

    // ===================================================================== Audit de durcissement (CIS Benchmark / ANSSI)
    static class Cis
    {
        public class Check { public string Id, Title, Fix, Cis, Anssi, Level; public Func<string, bool> Ok; }

        // chaque contrôle lit une valeur (clé 'id = valeur' émise par Script) et dit si la configuration est conforme
        public static readonly List<Check> Checks = new List<Check>
        {
            C("smb1", "SMBv1 désactivé (serveur)", v => v == "False" || v == "", "bad", "Désactiver SMBv1 : Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol", "CIS 18.3.1", "ANSSI R23"),
            C("smbsign", "Signature SMB exigée (serveur)", v => v == "True" || v == "1", "warn", "Set-SmbServerConfiguration -RequireSecuritySignature $true", "CIS 2.3.9.2", "ANSSI R24"),
            C("llmnr", "LLMNR désactivé", v => v == "0", "warn", "Stratégie : EnableMulticastName Resolution=0 (empoisonnement Responder)", "CIS 18.6.4.1", "ANSSI R14"),
            C("nbt", "NetBIOS sur TCP/IP désactivé", v => v == "ok", "warn", "Désactiver NetBIOS sur chaque carte (TcpipNetbiosOptions=2)", "CIS 18.6.4", "ANSSI R14"),
            C("wdigest", "WDigest ne stocke pas les mots de passe en clair", v => v == "0" || v == "", "bad", "UseLogonCredential=0 (HKLM\\...\\SecurityProviders\\WDigest)", "CIS 18.4.x", "ANSSI R36"),
            C("uac", "Contrôle de compte (UAC) activé", v => v == "1", "bad", "EnableLUA=1 (ne jamais désactiver l'UAC)", "CIS 2.3.17", "ANSSI R34"),
            C("ppl", "LSASS protégé (RunAsPPL)", v => v == "1" || v == "2", "warn", "RunAsPPL=1 : empêche le vol d'identifiants par Mimikatz", "CIS 2.3.x", "ANSSI R36"),
            C("fw", "Pare-feu actif sur les 3 profils", v => v == "3", "bad", "Set-NetFirewallProfile -All -Enabled True", "CIS 9.x", "ANSSI R31"),
            C("psblock", "Journalisation des blocs PowerShell", v => v == "1", "warn", "Activer ScriptBlockLogging (stratégie)", "CIS 18.9.100", "ANSSI R8"),
            C("defrt", "Defender : protection en temps réel", v => v == "True", "bad", "Réactiver la protection en temps réel", "CIS 18.9.47", "ANSSI R1"),
            C("deftamper", "Defender : protection contre la falsification", v => v == "True", "warn", "Activer la protection contre la falsification (Sécurité Windows)", "CIS 18.9.47", "ANSSI R1"),
            C("guest", "Compte Invité désactivé", v => v == "False", "warn", "Désactiver le compte Invité", "CIS 2.3.1.3", "ANSSI R30"),
            C("autologon", "Aucune ouverture de session automatique", v => v == "ok", "bad", "Supprimer DefaultPassword / AutoAdminLogon", "CIS 18.x", "ANSSI R33"),
            C("aie", "AlwaysInstallElevated désactivé", v => v == "ok", "bad", "Mettre AlwaysInstallElevated=0 (HKLM et HKCU)", "CIS 18.9.90", "ANSSI R37"),
            C("rdpnla", "RDP : authentification NLA exigée (si activé)", v => v == "1" || v == "off", "warn", "UserAuthentication=1 si le Bureau à distance est utilisé", "CIS 18.9.65", "ANSSI R39"),
            C("anon", "Énumération anonyme restreinte", v => v == "1", "warn", "RestrictAnonymous=1", "CIS 2.3.10", "ANSSI R28"),
            C("cached", "Peu d'identifiants mis en cache", v => { int n; return int.TryParse(v, out n) && n <= 4; }, "info", "CachedLogonsCount ≤ 4", "CIS 2.3.7.4", "ANSSI R23"),
            C("bitlocker", "BitLocker actif sur le disque système", v => v == "On", "warn", "Activer BitLocker (recette de livraison)", "CIS 18.10.9", "ANSSI R25"),
            C("smbv1cli", "SMBv1 client désactivé", v => v != "Running", "warn", "sc.exe config mrxsmb10 start=disabled", "CIS 18.3.1", "ANSSI R23"),
            C("psv2", "PowerShell v2 désactivé", v => v != "Enabled", "warn", "Disable-WindowsOptionalFeature -Online -FeatureName MicrosoftWindowsPowerShellV2", "CIS 18.9.100", "ANSSI R8"),
        };
        static Check C(string id, string title, Func<string, bool> ok, string level, string fix, string cis, string anssi) { return new Check { Id = id, Title = title, Ok = ok, Level = level, Fix = fix, Cis = cis, Anssi = anssi }; }

        public const string Script =
            "function V($p,$n){try{(Get-ItemProperty -Path $p -Name $n -EA Stop).$n}catch{''}};function L($s){'K|'+$s};" +
            "L('smb1='+[string](Get-SmbServerConfiguration -EA SilentlyContinue).EnableSMB1Protocol);" +
            "L('smbsign='+[string](Get-SmbServerConfiguration -EA SilentlyContinue).RequireSecuritySignature);" +
            "L('llmnr='+[string](V 'HKLM:\\Software\\Policies\\Microsoft\\Windows NT\\DNSClient' 'EnableMulticast'));" +
            "L('nbt='+$(if((Get-ChildItem 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\NetBT\\Parameters\\Interfaces' -EA SilentlyContinue|%{(Get-ItemProperty $_.PSPath -EA SilentlyContinue).NetbiosOptions}|?{$_ -ne 2}).Count -eq 0){'ok'}else{'non'}));" +
            "L('wdigest='+[string](V 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\SecurityProviders\\WDigest' 'UseLogonCredential'));" +
            "L('uac='+[string](V 'HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\System' 'EnableLUA'));" +
            "L('ppl='+[string](V 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Lsa' 'RunAsPPL'));" +
            "L('fw='+[string]((Get-NetFirewallProfile -EA SilentlyContinue|?{$_.Enabled}).Count));" +
            "L('psblock='+[string](V 'HKLM:\\Software\\Policies\\Microsoft\\Windows\\PowerShell\\ScriptBlockLogging' 'EnableScriptBlockLogging'));" +
            "L('defrt='+[string](-not (Get-MpPreference -EA SilentlyContinue).DisableRealtimeMonitoring));" +
            "L('deftamper='+[string]((Get-MpComputerStatus -EA SilentlyContinue).IsTamperProtected));" +
            "L('guest='+[string](Get-LocalUser -Name 'Guest' -EA SilentlyContinue).Enabled);" +
            "L('autologon='+$(if((V 'HKLM:\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon' 'DefaultPassword')){'non'}elseif((V 'HKLM:\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon' 'AutoAdminLogon') -eq '1'){'non'}else{'ok'}));" +
            "L('aie='+$(if((V 'HKLM:\\Software\\Policies\\Microsoft\\Windows\\Installer' 'AlwaysInstallElevated') -eq 1 -and (V 'HKCU:\\Software\\Policies\\Microsoft\\Windows\\Installer' 'AlwaysInstallElevated') -eq 1){'non'}else{'ok'}));" +
            "L('rdpnla='+$(if((V 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server' 'fDenyTSConnections') -eq 1){'off'}else{[string](V 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\\WinStations\\RDP-Tcp' 'UserAuthentication')}));" +
            "L('anon='+[string](V 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Lsa' 'RestrictAnonymous'));" +
            "L('cached='+[string](V 'HKLM:\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon' 'CachedLogonsCount'));" +
            "L('bitlocker='+[string](Get-BitLockerVolume -MountPoint $env:SystemDrive -EA SilentlyContinue).ProtectionStatus);" +
            "L('smbv1cli='+[string](Get-Service mrxsmb10 -EA SilentlyContinue).Status);" +
            "L('psv2='+[string](Get-WindowsOptionalFeature -Online -FeatureName MicrosoftWindowsPowerShellV2Root -EA SilentlyContinue).State);" +
            "'K|END='";

        public static List<CyFinding> Eval(string output, out int score)
        {
            var vals = new Dictionary<string, string>();
            foreach (var line in (output ?? "").Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.StartsWith("K|")))
            {
                var kv = line.Substring(2).Split(new[] { '=' }, 2); if (kv.Length == 2) vals[kv[0].Trim()] = kv[1].Trim();
            }
            var l = new List<CyFinding>(); int ok = 0, n = 0;
            foreach (var c in Checks)
            {
                string v; if (!vals.TryGetValue(c.Id, out v)) continue;   // non lu (version de Windows) : ignoré
                n++;
                // bitlocker : ProtectionStatus 1 = On
                if (c.Id == "bitlocker") v = v == "1" ? "On" : v == "0" ? "Off" : v;
                bool pass = c.Ok(v);
                if (pass) { ok++; l.Add(new CyFinding { Level = "ok", Host = Environment.MachineName, Item = "CIS · " + c.Title, Title = "Conforme (" + v + ")", Fix = "", Ref = c.Cis + " · " + c.Anssi }); }
                else l.Add(new CyFinding { Level = c.Level, Host = Environment.MachineName, Item = "CIS · " + c.Title, Title = "Non conforme" + (v.Length > 0 ? " (" + v + ")" : ""), Fix = c.Fix, Ref = c.Cis + " · " + c.Anssi });
            }
            score = n > 0 ? ok * 100 / n : 0;
            return l.OrderBy(x => x.Level == "bad" ? 0 : x.Level == "warn" ? 1 : x.Level == "info" ? 2 : 3).ToList();
        }
    }
}
