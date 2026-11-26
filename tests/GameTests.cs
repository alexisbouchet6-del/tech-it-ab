// Tests Tech IT AB / PC Santé AB - page Jeu (Mono, sans Windows) — by alex.b
using System; using System.Collections.Generic; using System.Linq; using System.IO; using System.Text; using Microsoft.Win32; using AlexB;
namespace AmiOps
{
    static class T15
    {
        static int ok, ko;
        static void A(string n, bool c, string d = "") { if (c) ok++; else ko++; Console.WriteLine((c ? "[OK] " : "[KO] ") + n + (d.Length > 0 ? " — " + d : "")); }
        static Exception Try(Action a) { try { a(); return null; } catch (Exception ex) { return ex; } }
        static string Oc(string cpu, string board, bool laptop) { string v, h; return GameRules.CpuOc(cpu, board, laptop, out v, out h) + "|" + v; }

        static void Main(string[] args)
        {
            AppInfo.Name = Ed.Name; AppInfo.Id = Ed.Id + "Test15"; AppInfo.Version = Ed.Version;

            // ---- cartes graphiques
            A("Kind : NVIDIA dédiée", GameRules.Kind("NVIDIA GeForce RTX 4070") == "dgpu" && GameRules.Kind("NVIDIA GeForce GTX 1650") == "dgpu");
            A("Kind : AMD dédiée / intégrée", GameRules.Kind("AMD Radeon RX 7800 XT") == "dgpu" && GameRules.Kind("AMD Radeon(TM) RX 6600") == "dgpu" && GameRules.Kind("AMD Radeon(TM) Graphics") == "igpu" && GameRules.Kind("AMD Radeon 780M") == "igpu" && GameRules.Kind("AMD Radeon Pro W7600") == "dgpu");
            A("Kind : Intel Arc dédiée / intégrée", GameRules.Kind("Intel(R) Arc(TM) A770 Graphics") == "dgpu" && GameRules.Kind("Intel(R) Arc(TM) B580 Graphics") == "dgpu" && GameRules.Kind("Intel(R) Arc(TM) Graphics") == "igpu" && GameRules.Kind("Intel(R) Arc(TM) 140V GPU (16GB)") == "igpu" && GameRules.Kind("Intel(R) UHD Graphics 770") == "igpu");
            A("Kind : virtuelles écartées", GameRules.Kind("Microsoft Basic Display Adapter") == "virtuel" && GameRules.Kind("Parsec Virtual Display Adapter") == "virtuel" && GameRules.Kind("VMware SVGA 3D") == "virtuel" && GameRules.Kind("Microsoft Remote Display Adapter") == "virtuel");
            A("Version NVIDIA", GameRules.NvidiaVersion("32.0.15.6094") == "560.94" && GameRules.NvidiaVersion("31.0.15.3623") == "536.23" && GameRules.NvidiaVersion("32.0.15.8180") == "581.80", GameRules.NvidiaVersion("32.0.15.8180"));
            A("Version NVIDIA : entrée invalide", GameRules.NvidiaVersion("") == "" && GameRules.NvidiaVersion("abc") == "" && GameRules.NvidiaVersion("1.2.3") == "");
            A("Cartes récentes (HAGS conseillé)", GameRules.ModernGpu("NVIDIA GeForce RTX 4060 Laptop GPU") && GameRules.ModernGpu("NVIDIA GeForce RTX 5080") && GameRules.ModernGpu("AMD Radeon RX 9070 XT") && !GameRules.ModernGpu("NVIDIA GeForce RTX 3060"));

            // ---- chipset et overclocking
            A("Chipset lu dans le modèle", GameRules.Chipset("ASUSTeK COMPUTER INC. ROG STRIX Z790-E GAMING WIFI") == "Z790" && GameRules.Chipset("Gigabyte B550M AORUS ELITE") == "B550" && GameRules.Chipset("MSI MAG X670E TOMAHAWK WIFI") == "X670" && GameRules.Chipset("ASRock H610M-HDV") == "H610" && GameRules.Chipset("Dell Inc. 0K240Y") == "");
            A("OC : Intel K + Z = possible", Oc("13th Gen Intel(R) Core(TM) i7-13700K", "ASUSTeK PRIME Z790-P", false).StartsWith("2|"));
            A("OC : Intel KF + B = limité", Oc("Intel(R) Core(TM) i5-12600KF", "MSI PRO B660M-A", false).StartsWith("1|"));
            A("OC : Intel non K = non", Oc("12th Gen Intel(R) Core(TM) i5-12400F", "MSI PRO B660M-A", false).StartsWith("0|"));
            A("OC : Intel Core Ultra 285K + Z890", Oc("Intel(R) Core(TM) Ultra 9 285K", "ASUS ROG MAXIMUS Z890 HERO", false).StartsWith("2|"));
            A("OC : Intel HX portable = limité", Oc("13th Gen Intel(R) Core(TM) i9-13980HX", "", true).StartsWith("1|"));
            A("OC : Intel portable U = non", Oc("13th Gen Intel(R) Core(TM) i7-1355U", "", true).StartsWith("0|"));
            A("OC : Intel K dans PC de marque = limité", Oc("Intel(R) Core(TM) i9-14900K", "Dell Inc. 0K240Y", false).StartsWith("1|"));
            A("OC : Ryzen 7700X + B650 = possible", Oc("AMD Ryzen 7 7700X 8-Core Processor", "Gigabyte B650 AORUS ELITE AX", false).StartsWith("2|"));
            A("OC : Ryzen + A520 = non", Oc("AMD Ryzen 5 5600 6-Core Processor", "ASRock A520M-HDV", false).StartsWith("0|"));
            A("OC : Ryzen 5800X3D / 7800X3D = limité", Oc("AMD Ryzen 7 5800X3D 8-Core Processor", "MSI B550 TOMAHAWK", false).StartsWith("1|") && Oc("AMD Ryzen 7 7800X3D 8-Core Processor", "ASUS TUF X670E", false).StartsWith("1|"));
            A("OC : Ryzen 9800X3D = possible", Oc("AMD Ryzen 7 9800X3D 8-Core Processor", "MSI MAG X870 TOMAHAWK", false).StartsWith("2|"));
            A("OC : Ryzen portable = non", Oc("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "", true).StartsWith("0|") && Oc("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", "", true).StartsWith("0|"));
            A("X3D à deux CCD reconnu", GameRules.DualCcdX3D("AMD Ryzen 9 7950X3D 16-Core Processor") && !GameRules.DualCcdX3D("AMD Ryzen 7 7800X3D 8-Core Processor"));

            // ---- mémoire
            string c, k;
            A("RAM : un seul module = simple canal", GameRules.RamCheck(1, 26, 3200, 3200, false, out c, out k) == "warn" && c.Contains("simple canal"));
            A("RAM : sous la vitesse annoncée", GameRules.RamCheck(2, 26, 3600, 2133, false, out c, out k) == "warn" && k.Contains("XMP"));
            A("RAM : DDR4 2666 = XMP probablement éteint", GameRules.RamCheck(2, 26, 2666, 2666, false, out c, out k) == "info");
            A("RAM : DDR5 6000 = OK", GameRules.RamCheck(2, 34, 4800, 6000, false, out c, out k) == "ok");
            A("RAM : soudée (LPDDR5) = rien à régler", GameRules.RamCheck(1, 35, 6400, 6400, true, out c, out k) == "ok");
            A("RAM : portable 2 modules = pas de conseil XMP", GameRules.RamCheck(2, 26, 3200, 2666, true, out c, out k) == "ok");

            // ---- bibliothèque Steam
            var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"730\"\t\t\"34000000000\"\n\t\t}\n\t}\n}";
            var libs = GameRules.SteamLibraries(vdf);
            A("Steam : bibliothèques lues", libs.Count == 2 && libs[1] == @"D:\SteamLibrary", string.Join(" ; ", libs));
            var acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"name\"\t\t\"Counter-Strike 2\"\n\t\"installdir\"\t\t\"Counter-Strike Global Offensive\"\n\t\"SizeOnDisk\"\t\t\"34000000000\"\n\t\"UserConfig\"\n\t{\n\t\t\"name\"\t\t\"ignored\"\n\t}\n}";
            var a = GameRules.Acf(acf);
            A("Steam : manifeste lu (premier « name » seulement)", a["name"] == "Counter-Strike 2" && a["installdir"] == "Counter-Strike Global Offensive" && a["SizeOnDisk"] == "34000000000");

            // ---- session de jeu : ce qui ne doit jamais être fermé
            var win = @"C:\Windows";
            A("Session : système jamais", GameRules.Classify("explorer", "Microsoft Corporation", @"C:\Windows\explorer.exe", win) == 0 && GameRules.Classify("svchost", "", "", win) == 0 && GameRules.Classify("dwm", "", "", win) == 0);
            A("Session : antivirus jamais", GameRules.Classify("avp", "AO Kaspersky Lab", @"C:\Program Files (x86)\Kaspersky Lab\avp.exe", win) == 0 && GameRules.Classify("msmpeng", "", "", win) == 0);
            A("Session : lanceurs et anti-triche jamais", new[] { "steam", "epicgameslauncher", "battle.net", "vgc", "easyanticheat", "beservice", "riotclientservices", "eadesktop" }.All(n => GameRules.Classify(n, "", @"C:\Program Files\X\" + n + ".exe", win) == 0));
            A("Session : assistance à distance jamais", GameRules.Classify("teamviewer", "TeamViewer Germany GmbH", @"C:\Program Files\TeamViewer\TeamViewer.exe", win) == 0 && GameRules.Classify("anydesk", "", @"C:\Program Files\AnyDesk\AnyDesk.exe", win) == 0);
            A("Session : pilotes et périphériques jamais (éditeur)", GameRules.Classify("someudpagent", "NVIDIA Corporation", @"C:\Program Files\NVIDIA\x.exe", win) == 0 && GameRules.Classify("xyz", "Logitech", @"C:\Program Files\LGHUB\xyz.exe", win) == 0);
            A("Session : navigateurs et OneDrive conseillés", GameRules.Classify("chrome", "Google LLC", @"C:\Program Files\Google\Chrome\Application\chrome.exe", win) == 1 && GameRules.Classify("onedrive", "Microsoft Corporation", @"C:\Users\a\AppData\Local\Microsoft\OneDrive\OneDrive.exe", win) == 1);
            A("Session : Discord et Spotify proposés décochés", GameRules.Classify("discord", "Discord Inc.", @"C:\Users\a\AppData\Local\Discord\app-1\Discord.exe", win) == 2 && GameRules.Classify("spotify", "Spotify Ltd", @"C:\Users\a\AppData\Roaming\Spotify\Spotify.exe", win) == 2);
            A("Session : processus illisible jamais", GameRules.Classify("inconnu", "", "", win) == 0);
            A("Session : appli dans C:\\Windows jamais", GameRules.Classify("notepad", "Microsoft Corporation", @"C:\Windows\System32\notepad.exe", win) == 0);
            A("Session : fermeture forcée seulement sans documents", GameRules.ForceOk("onedrive") && GameRules.ForceOk("teams") && !GameRules.ForceOk("winword") && !GameRules.ForceOk("chrome"));

            // ---- note
            var fl = new List<GFinding> { new GFinding("bad", "a", "", ""), new GFinding("warn", "b", "", ""), new GFinding("info", "c", "", ""), new GFinding("ok", "d", "", "") };
            A("Note sur 100", GameRules.Score(fl) == 79 && GameRules.Score(Enumerable.Range(0, 10).Select(i => new GFinding("bad", "", "", ""))) == 0);

            // ---- sauvegarde et restauration (attaquant : config.ini modifiable par l'utilisateur)
            var tampered = new[]
            {
                "REG|hags|2", "REG|gamemode|ABSENT", "REG|mspeed|1",
                "REG|evil|1", "REG|hags|7", "REG|hags|2;reg add", "REG|mspeed|1 & calc",
                "POWER|8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "POWER|11111111-2222-3333-4444-555555555555", "POWER|; shutdown /s",
                GameRules.PrevGpu(@"C:\Games\Jeu\jeu.exe", null), GameRules.PrevGpu(@"C:\Games\Jeu\jeu.exe", "GpuPreference=1;"), GameRules.PrevGpu(@"..\..\x.exe", null), GameRules.PrevGpu(@"C:\Games\a.exe", "GpuPreference=2;\" & calc"),
                "GPU|!!!|ABSENT", "", "n'importe quoi"
            };
            var ops = GameRules.ParsePrev(tampered);
            A("Restauration : liste blanche stricte", ops.Count == 6, string.Join(" ; ", ops.Select(o => o.Kind + ":" + (o.Slot ?? o.Path) + "=" + (o.Value ?? "∅"))));
            A("Restauration : HKLM seulement pour HAGS (0/1/2)", ops.Where(o => o.Kind == "REG" && GameRules.Slots[o.Slot].Hklm).All(o => o.Slot == "hags" && (o.Value == null || "012".Contains(o.Value))));
            A("Restauration : chemins de jeu validés", GameRules.ValidExe(@"D:\SteamLibrary\steamapps\common\Elden Ring\Game\eldenring.exe") && !GameRules.ValidExe(@"\\serveur\partage\x.exe") && !GameRules.ValidExe(@"C:\x.bat") && !GameRules.ValidExe(@"C:\a""b.exe"));
            var lines = new[] { "PREV|LLMNR|absent", "PREV|NBT|1|0", "PREV|FW|Public|True|Block" };
            var packed = IniLines.Pack(lines);
            A("config.ini : valeur multiligne sur une seule ligne", !packed.Contains("\n") && packed.StartsWith("b64:"));
            A("config.ini : relecture identique (correctif du durcissement 1.4)", IniLines.Unpack(packed).SequenceEqual(lines));
            A("config.ini : ancien format 1.4 encore lu", IniLines.Unpack("PREV|LLMNR|absent").Count == 1 && IniLines.Unpack("").Count == 0 && IniLines.Unpack("b64:%%%").Count == 0);
            var iniPath = Path.Combine(Path.GetTempPath(), "t15-" + Guid.NewGuid().ToString("N") + ".ini");
            var ini = new Ini(iniPath); ini.Set("HardenPrev", packed); ini.Save("t15");
            var ini2 = new Ini(iniPath); File.Delete(iniPath);
            A("config.ini : survit à l'enregistrement sur disque", IniLines.Unpack(ini2.Get("HardenPrev", "")).Count == 3);

            // ---- optimisation puis annulation (registre Mono : mêmes API qu'en Windows)
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\GameBar", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", false); } catch { }
            using (var kk = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar")) kk.SetValue("AutoGameModeEnabled", 0, RegistryValueKind.DWord);
            using (var kk = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR")) kk.SetValue("HistoricalCaptureEnabled", 1, RegistryValueKind.DWord);
            using (var kk = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse")) { kk.SetValue("MouseSpeed", "1"); kk.SetValue("MouseThreshold1", "6"); kk.SetValue("MouseThreshold2", "10"); }
            Prefs.Ini.Remove("GamePrev"); Prefs.Save();
            var log = new List<string>(); Action<string, string> L = (s, col) => log.Add(col + " " + s);
            var choice = new List<GameOpt.Choice> { new GameOpt.Choice { Id = "gamemode", On = true }, new GameOpt.Choice { Id = "bgrec", On = true }, new GameOpt.Choice { Id = "mouse", On = true } };
            var e1 = Try(() => GameOpt.Apply(choice, L));
            A("Optimiser : appliqué", e1 == null && GameScan.RegInt("gamemode", -1) == 1 && GameScan.RegInt("bgrec", -1) == 0 && Convert.ToString(GameScan.RegGet(GameRules.Slots["mspeed"])) == "0", e1 != null ? e1.Message : string.Join(" | ", log));
            using (var kk = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar")) kk.SetValue("AutoGameModeEnabled", 0, RegistryValueKind.DWord);
            Try(() => GameOpt.Apply(new List<GameOpt.Choice> { new GameOpt.Choice { Id = "gamemode", On = true } }, L));
            A("Optimiser deux fois : la valeur d'ORIGINE est gardée", GameRules.Unpack(Prefs.Ini.Get("GamePrev", "")).Count(x => x.StartsWith("REG|gamemode|")) == 1 && GameRules.Unpack(Prefs.Ini.Get("GamePrev", "")).Contains("REG|gamemode|0"));
            var tmp = Prefs.Ini.Get("GamePrev", "");
            Prefs.Ini.Set("GamePrev", IniLines.Pack(IniLines.Unpack(tmp).Concat(new[] { "REG|evil|1", "POWER|; shutdown" }))); Prefs.Save();
            log.Clear(); var e2 = Try(() => GameOpt.Undo(L));
            A("Annuler : réglages d'origine remis", e2 == null && GameScan.RegInt("gamemode", -1) == 0 && GameScan.RegInt("bgrec", -1) == 1 && Convert.ToString(GameScan.RegGet(GameRules.Slots["mspeed"])) == "1" && Convert.ToString(GameScan.RegGet(GameRules.Slots["mth2"])) == "10", e2 != null ? e2.Message : string.Join(" | ", log));
            A("Annuler : lignes trafiquées ignorées, sauvegarde vidée", !GameOpt.HasPrev && !log.Any(x => x.Contains("evil")));
            log.Clear(); GameOpt.Undo(L); A("Annuler sans rien à annuler", log.Any(x => x.Contains("Rien à annuler")));

            // ---- carte graphique par jeu
            log.Clear(); GameOpt.GpuFor("/tmp/pas-un-chemin-windows.exe", L); A("GPU par jeu : chemin non Windows refusé", log.Any(x => x.Contains("invalide")));
            A("GPU par jeu : valeur de registre propre", System.Text.RegularExpressions.Regex.IsMatch("GpuPreference=2;", @"^[A-Za-z0-9=;]{0,120}$"));

            // ---- analyse sans Windows : ne doit jamais planter
            var e3 = Try(() => { var f = GameScan.Analyze((s, col) => { }, false); GameScan.Print(f, (s, col) => { }); });
            A("Analyse : ne plante pas sans WMI / PowerShell", e3 == null, e3 != null ? e3.GetType().Name + " " + e3.Message : "");
            var e4 = Try(() => GameOpt.Overclock((s, col) => { })); A("Overclocking : rapport sans plantage", e4 == null, e4 != null ? e4.Message : "");
            var e5 = Try(() => GameOpt.Components(false, (s, col) => { })); A("Composants : détection sans plantage", e5 == null, e5 != null ? e5.Message : "");
            var e6 = Try(() => GameScan.Library());
            if (e6 is TypeLoadException && e6.Message.Contains("Eventing")) Console.WriteLine("[N/A] Bibliothèque : Mono ne fournit pas System.Diagnostics.Eventing (présent sous Windows)");
            else A("Bibliothèque : sans Steam ni Epic", e6 == null, e6 != null ? e6.Message : "");
            log.Clear(); var e7 = Try(() => GameOpt.Winget("x; calc", L)); A("winget : identifiant injecté refusé", e7 == null && log.Any(x => x.Contains("invalide")));
            A("Outils : identifiants winget au bon format, liens https", GameOpt.Tools.All(t => (t.Winget == null || System.Text.RegularExpressions.Regex.IsMatch(t.Winget, @"^[A-Za-z0-9][A-Za-z0-9.+\-]{1,80}$")) && t.Url.StartsWith("https://")));
            A("Scénario « Prêt à jouer » présent", Scenarios.Names.Contains("Prêt à jouer") && Scenarios.Build("Prêt à jouer").Count == 6);
            A("Scénarios réservés au technicien selon l'édition", Ed.Full ? Scenarios.Names.Contains("PC neuf") : !Scenarios.Names.Contains("PC neuf") && !Scenarios.Names.Contains("Fin d'intervention"));

            Console.WriteLine("T15 (" + Ed.Name + ") : " + ok + " OK / " + ko + " KO");
            Environment.Exit(ko);
        }
    }
}
