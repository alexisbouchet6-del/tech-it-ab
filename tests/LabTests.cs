// Tests Tech IT AB - Atelier labo (équipe violette) : périmètre RFC1918, simulations, catalogue Kali — by alex.b
using System; using System.Linq; using AlexB;
namespace AmiOps
{
    static class T18
    {
        static int ok, ko;
        static void A(string n, bool c, string d = "") { if (c) ok++; else ko++; Console.WriteLine((c ? "[OK] " : "[KO] ") + n + (d.Length > 0 ? " — " + d : "")); }

        static void Main(string[] args)
        {
            AppInfo.Name = Ed.Name; AppInfo.Id = Ed.Id + "Test18"; AppInfo.Version = Ed.Version;

            // ---- périmètre labo : RFC1918 obligatoire
            A("Périmètre : réseau privé accepté", Lab.Validate("192.168.56.0/24") == null && Lab.Validate("10.10.0.0/16") == null && Lab.Validate("172.16.5.10") == null);
            A("Périmètre : adresse publique refusée", Lab.Validate("8.8.8.8") != null && Lab.Validate("51.75.10.0/24") != null);
            A("Périmètre : mélange privé+public refusé", Lab.Validate("192.168.1.0/24\n1.1.1.1") != null);
            A("Périmètre : vide refusé", Lab.Validate("") != null && Lab.Validate("   ") != null);
            A("Périmètre : trop large (>/16) refusé", Lab.Validate("10.0.0.0/8") != null);
            A("Périmètre : adresse invalide refusée", Lab.Validate("999.1.1.1") != null && Lab.Validate("pas-une-ip") != null);

            // ---- cible et sous-réseau
            Lab.SetScope("192.168.56.0/24", "LabAD", true);
            A("Enrôlement labo confirmé", Lab.Confirmed && Lab.Ranges().Count == 1 && Lab.Name == "LabAD");
            A("Cible d'exemple dans le périmètre", Lab.Target() == "192.168.56.100", Lab.Target());
            A("Sous-réseau conservé", Lab.Subnet() == "192.168.56.0/24");
            Lab.SetScope("10.0.0.5", "Hote", true);
            A("Cible = l'hôte unique", Lab.Target() == "10.0.0.5", Lab.Target());
            // non confirmé = atelier non prêt (hors signature, testée ailleurs)
            Lab.SetScope("192.168.1.0/24", "x", false);
            A("Sans confirmation : périmètre présent mais non confirmé", Lab.Ranges().Count == 1 && !Lab.Confirmed);

            // ---- simulations : inoffensives et auto-nettoyantes
            A("Simulations : au moins 6", Lab.Sims.Count >= 6);
            A("Simulations : chacune a MITRE, action, détection, script", Lab.Sims.All(s => s.Mitre.Length > 0 && s.What.Length > 0 && s.Detect.Length > 0 && s.Run.Length > 0));
            A("Simulations : celles qui persistent se nettoient", Lab.Sims.Where(s => new[] { "runkey", "schtask", "startup" }.Contains(s.Id)).All(s => s.Clean.Length > 0));
            A("Simulations : aucune commande destructrice", Lab.Sims.All(s => !System.Text.RegularExpressions.Regex.IsMatch(s.Run + " " + s.Clean, @"(?i)\bformat |rmdir /s|rd /s|del /s|cipher /w|Remove-Item.*-Recurse.*C:\\|shutdown|diskpart|clear-eventlog|wevtutil cl|vssadmin delete")));
            A("Simulations : certutil pointe vers localhost", Lab.Sims.First(s => s.Id == "lolbin").Run.Contains("127.0.0.1"));
            A("Simulations : clé Run lance la calculatrice (bénin)", Lab.Sims.First(s => s.Id == "runkey").Run.Contains("calc.exe"));

            // ---- catalogue Kali : affichage, pas d'exécution
            var cat = Lab.Catalog();
            A("Catalogue : au moins 6 attaques", cat.Count >= 6);
            A("Catalogue : chacune a outil, MITRE, commande, détection, remédiation", cat.All(p => p.Tool.Contains("Kali") && p.Mitre.Length > 0 && p.Cmd.Length > 0 && p.Detect.Length > 0 && p.Remediate.Length > 0));
            A("Catalogue : la cible labo est injectée dans les commandes", cat.Any(p => p.Cmd.Contains("192.168.1.")) || cat.Any(p => p.Cmd.Contains(Lab.Target())) || cat.Any(p => p.Cmd.Contains(Lab.Subnet())));
            A("Catalogue : Responder, Hydra, Nmap, Metasploit, Impacket présents", new[] { "Responder", "Hydra", "Nmap", "Metasploit", "Impacket" }.All(t => cat.Any(p => p.Tool.Contains(t) || p.Name.Contains(t) || p.Cmd.Contains(t.ToLowerInvariant()))));
            A("Catalogue : remédiation renvoie au durcissement", cat.Any(p => p.Remediate.ToLowerInvariant().Contains("smbv1") || p.Remediate.ToLowerInvariant().Contains("durcir") || p.Remediate.Contains("CIS") || p.Remediate.Contains("RunAsPPL")));

            Console.WriteLine("T18 (" + Ed.Name + ") : " + ok + " OK / " + ko + " KO");
            Environment.Exit(ko);
        }
    }
}
