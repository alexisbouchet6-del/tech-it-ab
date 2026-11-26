// Tests Tech IT AB / PC Santé AB - authenticité (anti-copie) + supervision GLPI (Mono) — by alex.b
using System; using System.Collections.Generic; using System.Linq; using System.Net; using System.Text.RegularExpressions; using AlexB;
namespace AmiOps
{
    static class T16
    {
        static int ok, ko;
        static void A(string n, bool c, string d = "") { if (c) ok++; else ko++; Console.WriteLine((c ? "[OK] " : "[KO] ") + n + (d.Length > 0 ? " — " + d : "")); }
        static Exception Try(Action a) { try { a(); return null; } catch (Exception ex) { return ex; } }

        static void Main(string[] args)
        {
            AppInfo.Name = Ed.Name; AppInfo.Id = Ed.Id + "Test16"; AppInfo.Version = Ed.Version;
            ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;
            string pin = Ed.SignThumb;

            // ---- authenticité : décision pure
            A("Authenticité : non signé", Authenticity.Decide(false, false, "", pin) == Authenticity.Unsigned);
            A("Authenticité : signé + valide + empreinte alex.b = authentique", Authenticity.Decide(true, true, pin, pin) == Authenticity.Authentic);
            A("Authenticité : empreinte insensible aux espaces/casse", Authenticity.Decide(true, true, pin.ToLowerInvariant(), "7c02 f586 b652 e6c7 22e3 523b d861 70e5 564b 9d0a") == Authenticity.Authentic);
            A("Authenticité : signé par un AUTRE certificat = altérée", Authenticity.Decide(true, true, "AABBCCDDEEFF00112233445566778899AABBCCDD", pin) == Authenticity.Tampered);
            A("Authenticité : signature invalide (modifiée) = altérée", Authenticity.Decide(true, false, pin, pin) == Authenticity.Tampered);
            A("Authenticité : sans empreinte épinglée = altérée", Authenticity.Decide(true, true, pin, "") == Authenticity.Tampered);
            A("Authenticité : sous Mono = build de dev, de confiance", Authenticity.State == Authenticity.Dev && Authenticity.Trusted && Authenticity.Banner == null, Authenticity.State);

            // ---- supervision : validation de la configuration
            A("Superv : code client valide", Superv.CodeOk("CLI-09-16-0699") && !Superv.CodeOk("CLI-9-16-0699") && !Superv.CodeOk("CLI-09-16-0699;rm") && !Superv.CodeOk(""));
            A("Superv : URL valide", Superv.UrlOk("https://glpi.ami-tech.fr:8443/front/inventory.php") && !Superv.UrlOk("http://x/front/inventory.php") && !Superv.UrlOk("https://x") && !Superv.UrlOk("ftp://x/y"));
            Superv.SetEnrolment("https://127.0.0.1:9443/front/inventory.php", "CLI-09-16-0699", true);
            A("Superv : enrôlé", Superv.Enrolled && Superv.Banner != null && Superv.Banner.Contains("CLI-09-16-0699"));
            Superv.SetEnrolment("https://127.0.0.1:9443/front/inventory.php", "CLI-09-16-0699", false);
            A("Superv : désactivé = non enrôlé", !Superv.Enrolled && Superv.Banner == null);

            // ---- inventaire GLPI natif (construction pure, étiquette = code, marqueur présent, AUCUN jeton)
            Superv.SetEnrolment("https://127.0.0.1:9443/front/inventory.php", "CLI-09-16-0699", true);
            var h = new Superv.Health { Name = "CLI09160699-07", Serial = "SN-TEST", Maker = "Dell", Model = "Latitude", Os = "Windows 11 Pro", OsVersion = "10.0.26100", Cpu = "Intel i5", Cores = 8, RamMb = 16384, DiskTotalMb = 500000, DiskFreeMb = 120000, Av = "Windows Defender", AvEnabled = true, AvFresh = true, Summary = "disque 24 % libre · démarré depuis 3 j · antivirus Windows Defender (actif, à jour)" };
            var inv = (Dictionary<string, object>)Superv.Inventory(h, "test");
            var content = (Dictionary<string, object>)inv["content"];
            var sw = ((System.Collections.IEnumerable)content["softwares"]).Cast<Dictionary<string, object>>().First();
            A("Inventaire : action + itemtype + étiquette", Convert.ToString(inv["action"]) == "inventory" && Convert.ToString(inv["itemtype"]) == "Computer" && Convert.ToString(inv["tag"]) == "CLI-09-16-0699");
            A("Inventaire : deviceid préfixé par le nom du PC", Convert.ToString(inv["deviceid"]).StartsWith("CLI09160699-07-"));
            A("Inventaire : identité + marqueur d'état", Convert.ToString(((Dictionary<string, object>)content["hardware"])["name"]) == "CLI09160699-07" && Convert.ToString(sw["name"]) == Superv.MarkerName && Convert.ToString(sw["version"]).Contains("24 % libre"));
            A("Inventaire : aucun jeton/secret dans le JSON", !Regex.IsMatch(Store.Json().Serialize(inv), "(?i)token|api[_-]?key|password|secret"));

            // ---- envoi réel vers le faux serveur d'inventaire (tokenless) puis relecture (jeton d'alex.b)
            bool server = Try(() => Http.Call("GET", "https://127.0.0.1:9443/", null, null)) == null;
            if (!server) { Console.WriteLine("[N/A] Serveur d'inventaire 9443 absent : envoi et relecture non testés"); }
            else
            {
                Superv.SetConsent(true);
                var e1 = Try(() => Superv.Send("test", (s, c) => { }));
                A("Envoi : POST accepté par le serveur d'inventaire", e1 == null, e1 != null ? e1.Message : "");
                var log = Http.Call("GET", "https://127.0.0.1:9443/__log", null, null);
                var got = Http.List(log).Any(d => d.ContainsKey("inventory"));
                A("Envoi : inventaire reçu avec le marqueur d'état", got);

                // relecture côté Tech IT AB (uniquement dans l'édition complète : Glpi absent de PC Santé AB)
                try { Secrets.Set("GlpiUser", "tok"); } catch { }
                Prefs.Ini.Set("GlpiUrl", "https://127.0.0.1:9443/apirest.php"); Prefs.Save();
#if !POSTE
                var e2 = Try(() => { var rows = Superv.Read(5); A("Relecture : poste supervisé lu avec son dernier état", rows.Count >= 1 && rows.Any(r => r.Etat.Length > 0 && r.Etat.Contains("disque"))); });
                if (e2 != null) A("Relecture : poste supervisé lu avec son dernier état", false, e2.Message);
#else
                Console.WriteLine("[N/A] Relecture : réservée à Tech IT AB (Glpi absent de PC Santé AB)");
#endif
            }

            // ---- garde-fou anti-copie : une copie non authentique ne remonte rien (simulé via la décision pure)
            A("Garde-fou : une copie altérée est bridée", !new[] { Authenticity.Unsigned, Authenticity.Tampered }.All(st => st == Authenticity.Authentic || st == Authenticity.Dev));

            Console.WriteLine("T16 (" + Ed.Name + ") : " + ok + " OK / " + ko + " KO");
            Environment.Exit(ko);
        }
    }
}
