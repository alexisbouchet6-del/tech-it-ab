// Tests Tech IT AB - renforts cyber (Sysmon, Autoruns, durcissement CIS/ANSSI), logique pure sous Mono — by alex.b
using System; using System.Collections.Generic; using System.Linq; using AlexB;
namespace AmiOps
{
    static class T17
    {
        static int ok, ko;
        static void A(string n, bool c, string d = "") { if (c) ok++; else ko++; Console.WriteLine((c ? "[OK] " : "[KO] ") + n + (d.Length > 0 ? " — " + d : "")); }

        static void Main(string[] args)
        {
            AppInfo.Name = Ed.Name; AppInfo.Id = Ed.Id + "Test17"; AppInfo.Version = Ed.Version;

            // ---- découpe CSV (guillemets, virgules internes, guillemets doublés)
            var r = Autoruns.Csv("\"HKLM\\...\\Run\",\"Teams, Inc.\",\"C:\\a.exe\",\"(Verified) Microsoft\",\"0/72\"");
            A("CSV : virgule interne protégée", r.Count == 5 && r[1] == "Teams, Inc." && r[3].StartsWith("(Verified)"), string.Join(" | ", r));
            A("CSV : guillemets doublés", Autoruns.Csv("\"dit \"\"bonjour\"\"\",x").SequenceEqual(new[] { "dit \"bonjour\"", "x" }));

            // ---- Autoruns : évaluation
            var head = "Time,Entry Location,Entry,Enabled,Category,Profile,Description,Company,Image Path,Version,Launch String,VirusTotal,VT Permalink,MD5,SHA-1,SHA-256,Signer";
            Func<string, string, string, string, string, string, string> Row = (loc, entry, company, img, vt, signer) => "2026-11-26 10:00:00," + string.Join(",", loc, entry, "enabled", "Logon", "System", "d", company, img.Replace(",", " "), "1.0", img, vt, "", "", "", "", signer);
            var csv = head + "\n" +
                Row("HKLM\\...\\Run", "Updater", "Microsoft", "C:\\Program Files\\x\\upd.exe", "0 / 72", "(Verified) Microsoft Corporation") + "\n" +
                Row("HKLM\\...\\Run", "evil", "", "C:\\Users\\a\\AppData\\Local\\Temp\\evil.exe", "12 / 72", "(Not verified) ") + "\n" +
                Row("Task", "weird", "", "C:\\Users\\a\\AppData\\Roaming\\weird.exe", "0 / 72", "(Not verified) ") + "\n" +
                Row("HKLM\\...\\Run", "ghost", "", "File not found", "", "") + "\n" +
                Row("HKLM\\...\\Run", "legit", "Nvidia", "C:\\Windows\\System32\\nv.exe", "0 / 72", "(Not verified) ");
            int total; var f = Autoruns.Eval(csv, out total);
            A("Autoruns : total compté", total == 5, "total=" + total);
            A("Autoruns : entrée signée et propre ignorée", !f.Any(x => x.Item.Contains("Updater")));
            A("Autoruns : VirusTotal ≥ 3 = critique", f.Any(x => x.Item.Contains("evil") && x.Level == "bad" && x.Title.Contains("VirusTotal")));
            A("Autoruns : non signé en dossier utilisateur = critique", f.Any(x => x.Item.Contains("weird") && x.Level == "bad"));
            A("Autoruns : fichier absent = info (orpheline)", f.Any(x => x.Item.Contains("ghost") && x.Level == "info"));
            A("Autoruns : non signé hors dossier utilisateur = à corriger", f.Any(x => x.Item.Contains("legit") && x.Level == "warn"));

            // ---- durcissement CIS/ANSSI : évaluation + score
            var good = string.Join("\n", "K|smb1=False", "K|uac=1", "K|fw=3", "K|wdigest=0", "K|defrt=True", "K|autologon=ok", "K|aie=ok", "K|END=");
            int score; var g = Cis.Eval(good, out score);
            A("CIS : tout conforme = 100", score == 100 && g.All(x => x.Level == "ok"), "score=" + score);
            var bad = string.Join("\n", "K|smb1=True", "K|uac=0", "K|fw=1", "K|wdigest=1", "K|defrt=False", "K|autologon=non", "K|aie=non", "K|END=");
            var b = Cis.Eval(bad, out score);
            A("CIS : tout non conforme = 0", score == 0, "score=" + score);
            A("CIS : SMBv1 activé = critique avec remédiation", b.Any(x => x.Item.Contains("SMBv1 désactivé") && x.Level == "bad" && x.Fix.Length > 0));
            A("CIS : UAC désactivé = critique", b.Any(x => x.Item.Contains("UAC") && x.Level == "bad"));
            A("CIS : WDigest en clair = critique", b.Any(x => x.Item.Contains("WDigest") && x.Level == "bad"));
            A("CIS : référence CIS + ANSSI présente", b.All(x => x.Ref.Contains("CIS") && x.Ref.Contains("ANSSI")));
            var mixed = string.Join("\n", "K|smb1=False", "K|uac=1", "K|fw=1", "K|END=");
            Cis.Eval(mixed, out score); A("CIS : score partiel (2/3)", score == 66, "score=" + score);
            A("CIS : contrôle non lu (absent) ignoré", Cis.Eval("K|smb1=False\nK|END=", out score).Count == 1 && score == 100);
            A("CIS : BitLocker ProtectionStatus 1 = On conforme", Cis.Eval("K|bitlocker=1\nK|END=", out score)[0].Level == "ok");

            // ---- Sysmon : mapping des événements à risque
            var sm = string.Join("\n",
                "S|8|C:\\evil.exe|C:\\Windows\\explorer.exe|2026-11-26T10:00:00",
                "S|10|C:\\Temp\\mimi.exe|0x1410|2026-11-26T10:01:00",
                "S|1|powershell.exe|powershell -enc ZQBj|2026-11-26T10:02:00",
                "S|11|setup.exe|C:\\...\\Startup\\x.lnk|2026-11-26T10:03:00",
                "S|99|x|y|2026-11-26T10:04:00",
                "S|END|||");
            var e = Sysmon.Eval(sm);
            A("Sysmon : injection (8) = critique T1055", e.Any(x => x.Item.StartsWith("Sysmon 8") && x.Level == "bad" && x.Ref.Contains("T1055")));
            A("Sysmon : accès lsass (10) = critique T1003", e.Any(x => x.Item.StartsWith("Sysmon 10") && x.Level == "bad" && x.Ref.Contains("T1003")));
            A("Sysmon : PowerShell encodé (1) = critique", e.Any(x => x.Item.StartsWith("Sysmon 1 ") || x.Item.StartsWith("Sysmon 1·") || (x.Item.StartsWith("Sysmon 1") && x.Ref.Contains("T1059"))));
            A("Sysmon : dépôt Démarrage (11) = à vérifier", e.Any(x => x.Item.StartsWith("Sysmon 11") && x.Level == "warn"));
            A("Sysmon : événement inconnu (99) ignoré", !e.Any(x => x.Item.Contains("99")));
            A("Sysmon : critiques triés en premier", e.Count >= 2 && e[0].Level == "bad");

            Console.WriteLine("T17 (" + Ed.Name + ") : " + ok + " OK / " + ko + " KO");
            Environment.Exit(ko);
        }
    }
}
