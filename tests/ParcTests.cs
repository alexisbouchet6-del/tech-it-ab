// Tests AMI·OPS 1.3 (Mono) — by alex.b
using System; using System.Collections.Generic; using System.Linq; using System.IO; using System.Net; using System.Threading; using System.Diagnostics; using AlexB;
namespace AmiOps
{
    static class T13
    {
        static int ok, ko;
        static void A(string name, bool cond, string detail = "") { if (cond) ok++; else ko++; Console.WriteLine((cond ? "[OK] " : "[KO] ") + name + (detail.Length > 0 ? " — " + detail : "")); }
        static void Main()
        {
            AppInfo.Name = "AMI·OPS"; AppInfo.Id = "AmiOpsTest13"; AppInfo.Version = "1.3.0";
            ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;   // faux serveur auto-signé (Infra.Check garde sa propre validation)
            Store.D = new Data();
            var cl = new Client { Id = "c1", Name = "CLI-09-16-0699 - Garage Test", GlpiEntity = 5 }; Store.D.Clients.Add(cl);

            // 1. conventions
            A("CodeOf", Glpi.CodeOf(cl.Name) == "CLI-09-16-0699");
            A("PcPrefix/PcName", Glpi.PcPrefix("CLI-09-16-0699") == "CLI09160699-" && Glpi.PcName("CLI-09-16-0699", 4) == "CLI09160699-04");
            A("NextNumber (trou)", Glpi.NextNumber(new[] { "CLI09160699-01", "CLI09160699-03", "x" }, "CLI-09-16-0699") == 2);

            // 2. recette : analyse + évaluation
            var outp = "R|Name|CLI09160699-01\nR|Win|1;Windows(R), Professional edition\nR|Office|1;Office 24, Office24ProPlus2024VL_MAK_AE edition\nR|BL|1;{11111111-2222-3333-4444-555555555555}\n" +
                       "R|Glpi|Running;https://glpi.ami-tech.fr:8443/front/inventory.php;CLI-09-16-0699\nR|Trmm|Running\nR|Av|Kaspersky Endpoint Security for Windows:266240\nR|Kav|AVP.KES.12:Running,klnagent:Running\nR|Reboot|False\nR|Hotfix|" + DateTime.Today.AddDays(-5).ToString("yyyy-MM-dd") + "\nR|User|True;True\nR|QS|1\nR|Edition|Professional";
            var loc = Recette.ParseLocal(outp);
            A("ParseLocal", loc.Count == 13 && loc["Glpi"].StartsWith("Running;"), loc.Count + " clés");
            var kit = Store.Json().DeserializeObject("{\"schema\":\"amiprotek.deploiement/1\",\"kitVersion\":\"0.3.1\",\"result\":\"CONFORME\",\"issues\":[]}") as Dictionary<string, object>;
            var g = new Recette.Glpi_ { Found = true, Entity = 5, Id = 1, LastInventory = DateTime.Now.AddHours(-2), LastContact = DateTime.Now.AddHours(-1), Tag = "CLI-09-16-0699" };
            var ag = new Trmm.Ag { Host = "CLI09160699-01", Client = "CLI-09-16-0699 - Garage Test", Site = "Siège", Status = "online" };
            var l = Recette.Evaluate(cl, loc, kit, g, ag, true, true, DateTime.Now);
            Func<string, string> st = n => { var x = l.FirstOrDefault(c => c.Name == n); return x == null ? "?" : x.State; };
            A("Recette conforme : aucun KO", l.All(x => x.State != "bad"), string.Join(", ", l.Where(x => x.State != "ok").Select(x => x.Name + "=" + x.State)));
            A("Recette : Kaspersky 266240 = actif + à jour", st("Kaspersky") == "ok");
            A("Recette : clé BitLocker coffre verrouillé = alerte", st("Clé BitLocker séquestrée") == "warn");
            // cas défaillants
            loc["Name"] = "DESKTOP-ABC"; loc["Win"] = "0;Windows(R), Professional edition"; loc["Glpi"] = "Running;https://glpi.ami-tech.fr:8443/front/inventory.php;ATELIER";
            loc["Av"] = "Kaspersky Endpoint Security for Windows:262160"; loc["Reboot"] = "True"; loc["BL"] = "0;"; loc["Edition"] = "Professional";
            kit = Store.Json().DeserializeObject("{\"schema\":\"amiprotek.deploiement/1\",\"kitVersion\":\"0.3.1\",\"result\":\"A_VERIFIER\",\"issues\":[\"Windows non activé (Notification)\"]}") as Dictionary<string, object>;
            g = new Recette.Glpi_ { Found = true, Entity = 7, Id = 1, LastInventory = DateTime.Now.AddDays(-5) };
            ag.Client = "CLI-01-01-0001 - Autre";
            l = Recette.Evaluate(cl, loc, kit, g, ag, true, true, DateTime.Now);
            A("Recette KO : nom", st("Nom du PC") == "bad"); A("Recette KO : licence", st("Licence Windows") == "bad");
            A("Recette KO : étiquette GLPI", st("Agent GLPI") == "bad" && l.First(c => c.Name == "Agent GLPI").Detail.Contains("ATELIER"));
            A("Recette KO : mauvaise entité", st("Inventaire reçu par GLPI") == "bad"); A("Recette KO : mauvais client TRMM", st("Agent Tactical RMM") == "bad");
            A("Recette : rapport kit A_VERIFIER = alerte", st("Rapport du kit d'installation") == "warn");
            A("Recette : Kaspersky 262160 = inactif → KO", st("Kaspersky") == "bad");
            loc["Av"] = "Kaspersky Endpoint Security for Windows:266256"; var l2 = Recette.Evaluate(cl, loc, kit, g, ag, true, true, DateTime.Now);
            A("Recette : Kaspersky 266256 = actif, bases pas à jour → alerte", l2.First(c => c.Name == "Kaspersky").State == "warn", l2.First(c => c.Name == "Kaspersky").Detail);
            A("Recette : BitLocker inactif = KO", st("BitLocker") == "bad"); A("Recette : redémarrage = alerte", st("Mises à jour Windows") == "warn");
            l = Recette.Evaluate(cl, loc, null, null, null, false, false, DateTime.Now);
            A("Recette sans kit/GLPI/TRMM : n/a", st("Rapport du kit d'installation") == "na" && st("Inventaire reçu par GLPI") == "na");
            A("Recette.Text", Recette.Text("PC1", l).Contains("[KO]  Nom du PC"));

            // 3. tableau de bord : évaluation
            var r1 = new ParcRow { Pc = "A", LastSeen = DateTime.Now.AddDays(-10), FreePct = 8, Encrypted = 0, AvActive = true, AvFresh = false, OsName = "Windows 10 Pro", Reboot = true };
            Parc.Eval(r1, DateTime.Now); A("Parc.Eval critique", r1.Level == 2 && r1.Issues.Count == 6, string.Join(" ; ", r1.Issues));
            var r2 = new ParcRow { Pc = "B", LastSeen = DateTime.Now, FreePct = 50, Encrypted = 1, AvActive = true, AvFresh = true, OsName = "Windows 11", OsVersion = "26H2", GlpiId = 3 };
            Parc.Eval(r2, DateTime.Now); A("Parc.Eval conforme", r2.Level == 0 && r2.Issues.Count == 0, string.Join(" ; ", r2.Issues));

            // 4. Safety Scanner
            A("Safety Scanner : chaîne Microsoft", Optim.SafetyScannerTrusted("Valid|CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US|CN=Microsoft Root Certificate Authority 2011, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
            A("Safety Scanner : racine tierce refusée", !Optim.SafetyScannerTrusted("Valid|CN=Microsoft Corporation, O=Microsoft Corporation, C=US|CN=Evil Root, O=Evil, C=US"));
            A("Safety Scanner : HashMismatch refusé", !Optim.SafetyScannerTrusted("HashMismatch|CN=Microsoft Corporation, O=Microsoft Corporation|CN=Microsoft Root Certificate Authority 2011, O=Microsoft Corporation"));
            A("Safety Scanner : O=Microsoft Corporation Fake refusé", !Optim.SafetyScannerTrusted("Valid|CN=x, O=Microsoft Corporation Fake|CN=Microsoft Root Certificate Authority 2011, O=Microsoft Corporation"));

            // 5. connecteurs contre le faux serveur
            try { Secrets.Set("GlpiUser", "tok"); Secrets.Set("TrmmKey", "k1"); A("Secrets (DPAPI Mono)", Secrets.Get("GlpiUser") == "tok"); } catch (Exception ex) { A("Secrets (DPAPI Mono)", false, ex.Message); }
            Prefs.Ini.Set("GlpiUrl", "https://127.0.0.1:8443/apirest.php"); Prefs.Ini.Set("TrmmUrl", "https://127.0.0.1:8443/trmm"); Prefs.Save();
            A("Trmm.CheckDeployUrl valide", Try(() => Trmm.CheckDeployUrl("https://127.0.0.1:8443/clients/ab-12/deploy/")) == null);
            A("Trmm.CheckDeployUrl autre domaine refusé", Try(() => Trmm.CheckDeployUrl("https://evil.example.com/clients/ab-12/deploy/")) != null);
            A("Trmm.CheckDeployUrl http refusé", Try(() => Trmm.CheckDeployUrl("http://127.0.0.1/clients/ab/deploy/")) != null);
            try
            {
                var res = Glpi.Session(t => new object[] { Glpi.FindComputer(t, "CLI09160699-01"), Glpi.NextNumber(Glpi.Computers(t).Select(x => Http.S(x, "name")), "CLI-09-16-0699"), Glpi.OpenTickets(t, 5), Glpi.EnsureEntity(t, "CLI-09-16-0699", cl.Name), Glpi.EnsureEntity(t, "CLI-02-02-0002", "Nouveau client") });
                A("GLPI FindComputer", Glpi.IdOf(res[0] as Dictionary<string, object>) == 1);
                A("GLPI NextNumber", (int)res[1] == 2);
                A("GLPI OpenTickets (recherche)", ((List<Dictionary<string, object>>)res[2]).Count == 1 && Http.S(((List<Dictionary<string, object>>)res[2])[0], "2") == "42");
                A("GLPI EnsureEntity existante / créée", (int)res[3] == 5 && (int)res[4] == 9);
                var pdf = Path.Combine(Path.GetTempPath(), "pv-test.html"); File.WriteAllText(pdf, "<html><body>PV</body></html>");
                var ids = Glpi.Session(t => { var tid = Glpi.NewTicket(t, 5, "Livraison du poste X", "<b>ok</b>", 2, 2); Glpi.LinkComputer(t, tid, 1); Glpi.Task(t, tid, "tâche", 35); Glpi.Followup(t, tid, "suivi"); Glpi.Solve(t, tid, "fini"); return new[] { tid, Glpi.Upload(t, pdf, 5, tid) }; });
                A("GLPI ticket + upload", ids[0] == 42 && ids[1] == 77);
            }
            catch (Exception ex) { A("GLPI (faux serveur)", false, ex.Message); }

            // 6. parc (GLPI + Tactical RMM)
            try
            {
                var rows = Parc.Load(cl, (s, c) => { });
                Func<string, ParcRow> R = n => rows.FirstOrDefault(x => x.Pc == n);
                A("Parc.Load : 3 postes fusionnés", rows.Count == 3, string.Join(", ", rows.Select(x => x.Pc)));
                var p1 = R("CLI09160699-01");
                A("Parc : PC 01 disque 8 % + antivirus (repli ComputerAntivirus) + TRMM", p1 != null && Math.Abs(p1.FreePct - 8) < 0.1 && p1.AvName.StartsWith("Kaspersky") && p1.AvFresh == false && p1.Failing == 2 && p1.Reboot && p1.Level == 2, p1 == null ? "absent" : string.Join(" ; ", p1.Issues));
                var p3 = R("CLI09160699-03");
                A("Parc : PC 03 Windows 10 + sans antivirus", p3 != null && p3.Issues.Any(i => i.StartsWith("Windows 10")) && p3.Issues.Contains("aucun antivirus inventorié"), p3 == null ? "" : string.Join(" ; ", p3.Issues));
                var p2 = R("CLI09160699-02");
                A("Parc : PC 02 (Tactical RMM seul) pas vu + MAJ en attente", p2 != null && p2.GlpiId == 0 && p2.Patches && p2.Level == 2, p2 == null ? "" : string.Join(" ; ", p2.Issues));
                var html = Monthly.Html(cl, rows, new DateTime(2026, 11, 1), new List<Intervention>());
                A("Rapport mensuel HTML", html.Contains("novembre 2026") && html.Contains("CLI09160699-03") && html.Contains("à traiter"));
            }
            catch (Exception ex) { A("Parc.Load", false, ex.ToString()); }

            // 7. Multi-PC via Tactical RMM
            try
            {
                List<string> errs; var res = Multi.RunTrmm(new List<string> { "CLI09160699-01", "cli09160699-02", "INCONNU" }, "Inventaire", "", (s, c) => { }, out errs);
                A("Multi-PC TRMM : 1 résultat, 2 échecs", res.Count == 1 && Http.S(res[0], "PSComputerName") == "CLI09160699-01" && Http.S(res[0], "Modele") == "Dell Latitude" && errs.Count == 2, string.Join(" | ", errs));
            }
            catch (Exception ex) { A("Multi-PC TRMM", false, ex.Message); }

            // 8. santé des serveurs : certificat auto-signé refusé, durée relevée
            var inf = Infra.Check("https://127.0.0.1:8443/");
            A("Infra : certificat non approuvé = KO", inf.Level == "bad" && inf.Detail.Contains("CERTIFICAT"), inf.Detail + " · jours=" + inf.Days);
            A("Infra : durée de validité lue", inf.Days >= 8 && inf.Days <= 10, inf.Days.ToString());

            // 9. annulation réelle d'un programme (centre des tâches)
            var cts = new CancellationTokenSource(); Proc.Token = () => cts.Token; cts.CancelAfter(1000);
            var sw = Stopwatch.StartNew(); bool cancelled = false;
            try { string o; Proc.Run("/bin/sleep", "30", 60000, out o); } catch (OperationCanceledException) { cancelled = true; }
            A("Annulation : programme arrêté", cancelled && sw.ElapsedMilliseconds < 5000, sw.ElapsedMilliseconds + " ms");
            A("Annulation : tâche déjà annulée = refus immédiat", Try(() => { string o; Proc.Run("/bin/true", "", 5000, out o); }) is OperationCanceledException);
            A("Journal : mot de passe masqué", Proc.Mask("/i \"a.msi\" /qn PASSWORD=\"p@ss w\" USER=\"u\"") == "/i \"a.msi\" /qn PASSWORD=*** USER=\"u\"" && Proc.Mask("x -Password s3cr3t y") == "x -Password *** y", Proc.Mask("/i \"a.msi\" /qn PASSWORD=\"p@ss w\" USER=\"u\""));
            Proc.Token = null; string o2; A("Sans jeton : exécution normale", Proc.Run("/bin/echo", "ok", 5000, out o2) == 0 && o2.Contains("ok"));

            // 10. requêtes envoyées
            try
            {
                var log = Http.List(Http.Call("GET", "https://127.0.0.1:8443/__log", null, null)).ToList();
                Func<string, Dictionary<string, object>> last = path => log.LastOrDefault(x => Http.S(x, "p") == path);
                var tk = last("/apirest.php/Ticket/"); var task = last("/apirest.php/TicketTask/"); var fu = last("/apirest.php/ITILFollowup/"); var up = last("/apirest.php/Document/");
                A("Requête ticket : entité, type, statut", tk != null && Http.S(tk, "b").Contains("\"entities_id\":5") && Http.S(tk, "b").Contains("\"type\":2") && Http.S(tk, "b").Contains("\"status\":2"));
                A("Requête tâche : 35 min = 2100 s", task != null && Http.S(task, "b").Contains("\"actiontime\":2100") && Http.S(task, "b").Contains("\"tickets_id\":42"));
                A("Requête suivi : itemtype Ticket", fu != null && Http.S(fu, "b").Contains("\"itemtype\":\"Ticket\"") && Http.S(fu, "b").Contains("\"items_id\":42"));
                var upl = up != null && up.ContainsKey("upload") ? up["upload"] as Dictionary<string, object> : null;
                A("Requête document : manifeste + fichier", upl != null && Http.S(upl, "filename") == "pv-test.html" && Http.S(upl, "size") == "28", upl == null ? "" : Store.Json().Serialize(upl));
                A("Session-Token et clé TRMM envoyés", log.Any(x => Http.S(x, "p") == "/apirest.php/Ticket/" && Store.Json().Serialize(x["h"]).Contains("S1")) && log.Any(x => Http.S(x, "p") == "/trmm/agents/" && Store.Json().Serialize(x["h"]).Contains("k1")));
                A("Sessions fermées (killSession)", log.Count(x => Http.S(x, "p") == "/apirest.php/initSession") == log.Count(x => Http.S(x, "p") == "/apirest.php/killSession"));
            }
            catch (Exception ex) { A("Journal du faux serveur", false, ex.Message); }
            Console.WriteLine(ok + " OK · " + ko + " KO");
            Environment.Exit(ko);
        }
        static Exception Try(Action a) { try { a(); return null; } catch (Exception ex) { return ex; } }
        static Exception Try(Func<object> f) { try { f(); return null; } catch (Exception ex) { return ex; } }
    }
}
