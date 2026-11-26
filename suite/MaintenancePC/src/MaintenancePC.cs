// MaintenancePC - maintenance Windows en un clic (temp, cache WU, DNS, SFC, DISM...)
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlexB;

[assembly: AssemblyTitle("MaintenancePC")]
[assembly: AssemblyDescription("Maintenance Windows en un clic")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("MaintenancePC")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]

namespace MaintenancePC
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            AppInfo.Name = "MaintenancePC"; AppInfo.Id = "MaintenancePC"; AppInfo.Version = "1.1.1"; AppInfo.MachineWide = true;
            Boot.Run(() => new MainForm(), true, "nettoyage système, services Windows Update, SFC et DISM.");
        }
    }

    class Job
    {
        public string Key, Label, Hint;
        public bool Quick, Full;
        public CheckBox Box;
    }

    // ===================================================================== Moteur
    class Engine
    {
        readonly LogBox log;
        readonly Action<string> step;
        public volatile bool Cancel;
        public bool RebootAdvised;
        public long Freed;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

        public Engine(LogBox l, Action<string> s) { log = l; step = s; }

        void Ok(string s) { log.Line("  ✓ " + s, "ok"); Log.Write("OK " + s); }
        void Warn(string s) { log.Line("  ! " + s, "warn"); Log.Write("WARN " + s); }
        void Bad(string s) { log.Line("  ✗ " + s, "bad"); Log.Write("ERR " + s); }
        void Info(string s) { log.Line("  " + s, "muted"); }

        static long FreeC()
        {
            try { return new DriveInfo(Proc.SysDrive).AvailableFreeSpace; } catch { return 0; }
        }

        // supprime le contenu d'un dossier, en ignorant les fichiers verrouillés
        long Purge(string dir, int minAgeHours, ref int skipped)
        {
            long freed = 0;
            if (!Directory.Exists(dir)) return 0;
            var limit = DateTime.Now.AddHours(-minAgeHours);
            foreach (var f in SafeFiles(dir))
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.LastWriteTime > limit) continue;
                    long len = fi.Length;
                    fi.Attributes = FileAttributes.Normal;
                    fi.Delete();
                    freed += len;
                }
                catch { skipped++; }
            }
            foreach (var d in SafeDirs(dir).OrderByDescending(x => x.Length))
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            return freed;
        }

        static IEnumerable<string> SafeFiles(string root)
        {
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                string[] files = new string[0], dirs = new string[0];
                try { files = Directory.GetFiles(d); } catch { }
                try { dirs = Directory.GetDirectories(d).Where(x => (new DirectoryInfo(x).Attributes & FileAttributes.ReparsePoint) == 0).ToArray(); } catch { }
                foreach (var f in files) yield return f;
                foreach (var s in dirs) stack.Push(s);
            }
        }
        static IEnumerable<string> SafeDirs(string root)
        {
            try { return Directory.GetDirectories(root, "*", SearchOption.AllDirectories); } catch { return new string[0]; }
        }

        int Cmd(string exe, string args, int timeoutMin, Encoding enc, Func<string, bool> showLine)
        {
            string o;
            return Proc.Run(exe, args, timeoutMin * 60000, enc, line =>
            {
                var l = line.Trim();
                if (l.Length == 0) return;
                if (showLine == null || showLine(l)) step(l.Length > 90 ? l.Substring(0, 90) + "…" : l);
            }, out o);
        }

        public void Run(List<Job> jobs)
        {
            long before = FreeC();
            int i = 0;
            foreach (var j in jobs)
            {
                if (Cancel) { Warn("Annulé par l'utilisateur"); break; }
                i++;
                log.Line("» [" + i + "/" + jobs.Count + "] " + j.Label, "accent", true);
                step(j.Label);
                var sw = Stopwatch.StartNew();
                try { Do(j.Key); }
                catch (Exception ex) { Bad(ex.Message); Log.Write(j.Key + " : " + ex); }
                Info("durée : " + (sw.Elapsed.TotalMinutes >= 1 ? (int)sw.Elapsed.TotalMinutes + " min " : "") + sw.Elapsed.Seconds + " s");
            }
            Freed = Math.Max(0, FreeC() - before);
        }

        // ---- appels COM tardifs (API Windows Update Agent, sans dépendance supplémentaire)
        static object Call(object o, string m, params object[] a) { return o.GetType().InvokeMember(m, BindingFlags.InvokeMethod, null, o, a); }
        static object Get(object o, string p, params object[] a) { return o.GetType().InvokeMember(p, BindingFlags.GetProperty, null, o, a); }
        static void Set(object o, string p, object v) { o.GetType().InvokeMember(p, BindingFlags.SetProperty, null, o, new[] { v }); }

        void Drivers()
        {
            object session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session"));
            Set(session, "ClientApplicationID", "MaintenancePC by alex.b");
            object searcher = Call(session, "CreateUpdateSearcher");
            Set(searcher, "ServerSelection", 2);   // Windows Update (et non le WSUS de l'entreprise)
            step("recherche des pilotes sur Windows Update (peut prendre plusieurs minutes)…");
            object res;
            try { res = Call(searcher, "Search", "IsInstalled=0 and Type='Driver' and IsHidden=0"); }
            catch (TargetInvocationException ex)
            {
                var h = ex.InnerException as COMException;
                Bad("recherche impossible" + (h != null ? " (0x" + h.ErrorCode.ToString("X8") + ")" : "") + " : vérifier l'accès Internet et le service Windows Update");
                return;
            }
            object ups = Get(res, "Updates");
            int n = Convert.ToInt32(Get(ups, "Count"));
            if (n == 0) { Ok("aucun pilote à mettre à jour proposé par Windows Update"); return; }
            Info(n + " pilote(s) proposé(s) :");
            object coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl"));
            var titles = new List<string>();
            for (int i = 0; i < n; i++)
            {
                object u = Get(ups, "Item", i);
                string t = Convert.ToString(Get(u, "Title"));
                titles.Add(t);
                Info("   • " + t);
                try { if (!Convert.ToBoolean(Get(u, "EulaAccepted"))) Call(u, "AcceptEula"); } catch { }
                Call(coll, "Add", u);
            }
            if (Cancel) { Warn("annulé avant le téléchargement"); return; }
            object dl = Call(session, "CreateUpdateDownloader");
            Set(dl, "Updates", coll);
            step("téléchargement de " + n + " pilote(s)…");
            Call(dl, "Download");
            object inst = Call(session, "CreateUpdateInstaller");
            Set(inst, "Updates", coll);
            step("installation de " + n + " pilote(s)…");
            object ir = Call(inst, "Install");
            int ok = 0;
            for (int i = 0; i < n; i++)
            {
                int rc = Convert.ToInt32(Get(Call(ir, "GetUpdateResult", i), "ResultCode"));
                if (rc == 2 || rc == 3) ok++; else Warn("échec : " + titles[i] + " (code " + rc + ")");
            }
            if (Convert.ToBoolean(Get(ir, "RebootRequired"))) RebootAdvised = true;
            if (ok > 0) Ok(ok + "/" + n + " pilote(s) installé(s)" + (RebootAdvised ? " — redémarrage nécessaire" : ""));
        }

        static string FindFile(params string[] paths)
        {
            foreach (var p in paths)
            {
                var e = Environment.ExpandEnvironmentVariables(p);
                if (File.Exists(e)) return e;
            }
            return null;
        }

        void OemTool()
        {
            string maker = "";
            try { using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS")) maker = Convert.ToString(k.GetValue("SystemManufacturer")).Trim(); } catch { }
            Info("fabricant détecté : " + (maker.Length > 0 ? maker : "inconnu"));
            var m = maker.ToLowerInvariant();
            if (m.Contains("dell"))
            {
                var dcu = FindFile(@"%ProgramFiles%\Dell\CommandUpdate\dcu-cli.exe", @"%ProgramFiles(x86)%\Dell\CommandUpdate\dcu-cli.exe");
                if (dcu == null) { Warn("Dell Command | Update absent : l'installer depuis dell.com/support (modèles Latitude / OptiPlex / Precision). Sur Inspiron / Vostro grand public : utiliser « Dell Update »."); return; }
                Warn("Dell Command Update peut redémarrer le PC à la fin malgré l'option de non-redémarrage (comportement connu de Dell).");
                string o;
                int rc = Proc.Run(dcu, "/applyUpdates -updateType=driver -autoSuspendBitLocker=enable -reboot=disable -outputLog=\"" + Path.Combine(AppInfo.DataDir, "dell-command-update.log") + "\"",
                    90 * 60000, null, l => { var t = l.Trim(); if (t.Length > 0) step(t.Length > 90 ? t.Substring(0, 90) + "…" : t); }, out o);
                if (rc == 0) Ok("Dell Command Update : pilotes installés");
                else if (rc == 1 || rc == 5) { Ok("Dell Command Update : pilotes installés, redémarrage nécessaire"); RebootAdvised = true; }
                else if (rc == 500) Ok("Dell Command Update : aucune mise à jour disponible");
                else Warn("Dell Command Update : code " + rc + " (voir " + Path.Combine(AppInfo.DataDir, "dell-command-update.log") + ")");
            }
            else if (m.Contains("lenovo"))
            {
                var tvsu = FindFile(@"%ProgramFiles(x86)%\Lenovo\System Update\tvsu.exe", @"%ProgramFiles%\Lenovo\System Update\tvsu.exe");
                if (tvsu == null) { Warn("Lenovo System Update absent : l'installer depuis support.lenovo.com (ou utiliser Lenovo Vantage)."); return; }
                Process.Start(tvsu);
                Ok("Lenovo System Update ouvert : lance la recherche et installe les pilotes proposés");
            }
            else if (m.Contains("hp") || m.Contains("hewlett"))
            {
                var hpsa = FindFile(@"%ProgramFiles(x86)%\Hewlett-Packard\HP Support Framework\HPSF.exe", @"%ProgramFiles%\HP\HP Support Framework\HPSF.exe");
                if (hpsa == null) { Warn("HP Support Assistant absent : l'installer depuis hp.com/support (ou HP Image Assistant pour les PC pro)."); return; }
                Process.Start(hpsa);
                Ok("HP Support Assistant ouvert : vérifie les mises à jour et installe les pilotes proposés");
            }
            else Info("pas d'outil fabricant automatique pour ce modèle : Windows Update couvre l'essentiel des pilotes");
        }

        void Do(string key)
        {
            int skipped = 0;
            switch (key)
            {
                case "restore":
                {
                    int rc = Cmd(Proc.Sys(@"WindowsPowerShell\v1.0\powershell.exe"),
                        "-NoProfile -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'MaintenancePC by alex.b' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop\"",
                        10, null, null);
                    if (rc == 0) Ok("point de restauration créé (ou déjà créé dans les dernières 24 h)");
                    else Warn("point de restauration non créé : protection système désactivée sur C: ? (Panneau de configuration > Système > Protection du système)");
                    break;
                }
                case "temp":
                {
                    long freed = 0;
                    var users = Path.Combine(Proc.SysDrive, "Users");
                    foreach (var u in Directory.Exists(users) ? Directory.GetDirectories(users) : new string[0])
                        freed += Purge(Path.Combine(u, @"AppData\Local\Temp"), 24, ref skipped);
                    freed += Purge(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), 24, ref skipped);
                    Ok(Fmt.Size(freed) + " supprimés" + (skipped > 0 ? "  (" + skipped + " fichiers en cours d'utilisation ignorés)" : ""));
                    Info("les fichiers de moins de 24 h sont conservés (installations en cours)");
                    break;
                }
                case "wu":
                {
                    string o;
                    foreach (var s in new[] { "wuauserv", "bits", "usosvc" }) Proc.Run(Proc.Sys("net.exe"), "stop " + s + " /y", 120000, out o);
                    long freed = Purge(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SoftwareDistribution\Download"), 0, ref skipped);
                    freed += Purge(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SoftwareDistribution\DeliveryOptimization"), 0, ref skipped);
                    foreach (var s in new[] { "bits", "wuauserv", "usosvc" }) Proc.Run(Proc.Sys("net.exe"), "start " + s, 120000, out o);
                    Ok(Fmt.Size(freed) + " de téléchargements Windows Update supprimés (ils seront re-téléchargés si besoin)");
                    break;
                }
                case "recycle":
                {
                    int rc = SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);   // sans confirmation, sans barre, sans son
                    if (rc == 0) Ok("corbeille vidée");
                    else if (rc == unchecked((int)0x8000FFFF)) Ok("corbeille déjà vide");
                    else Warn("corbeille non vidée (code 0x" + rc.ToString("X8") + ")");
                    break;
                }
                case "wer":
                {
                    long freed = 0;
                    var wer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER");
                    foreach (var sub in new[] { "ReportArchive", "ReportQueue", "Temp" }) freed += Purge(Path.Combine(wer, sub), 0, ref skipped);
                    Ok(Fmt.Size(freed) + " de rapports d'erreurs supprimés");
                    break;
                }
                case "dns":
                {
                    string o;
                    int rc = Proc.Run(Proc.Sys("ipconfig.exe"), "/flushdns", 30000, out o);
                    if (rc == 0) Ok("cache DNS vidé"); else Warn("ipconfig /flushdns : " + o.Trim());
                    break;
                }
                case "component":
                {
                    int rc = Cmd(Proc.Sys("Dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup", 90, null, l => l.Contains("%"));
                    if (rc == 0) Ok("anciens composants Windows nettoyés (WinSxS)"); else Warn("DISM StartComponentCleanup : code " + rc);
                    break;
                }
                case "dism":
                {
                    int rc = Cmd(Proc.Sys("Dism.exe"), "/Online /Cleanup-Image /RestoreHealth", 120, null, l => l.Contains("%"));
                    if (rc == 0) Ok("image Windows vérifiée / réparée (DISM RestoreHealth)");
                    else Bad("DISM RestoreHealth a échoué (code " + rc + ") : vérifier l'accès Internet / Windows Update, voir C:\\Windows\\Logs\\DISM\\dism.log");
                    break;
                }
                case "sfc":
                {
                    string all;
                    int rc = Proc.Run(Proc.Sys("sfc.exe"), "/scannow", 120 * 60000, Encoding.Unicode, l =>
                    {
                        var t = l.Trim();
                        if (t.Length > 0) step(t.Length > 90 ? t.Substring(0, 90) + "…" : t);
                    }, out all);
                    var a = all.ToLowerInvariant();
                    if (a.Contains("aucune violation") || a.Contains("did not find any integrity")) Ok("SFC : aucun fichier système corrompu");
                    else if (a.Contains("les a réparés") || a.Contains("successfully repaired")) { Ok("SFC : fichiers corrompus trouvés ET réparés"); RebootAdvised = true; }
                    else if (a.Contains("pas pu") || a.Contains("unable to fix")) Bad("SFC : fichiers corrompus NON réparés → voir C:\\Windows\\Logs\\CBS\\CBS.log (relancer après DISM)");
                    else if (a.Contains("redémarrage") || a.Contains("restart")) { Warn("SFC : réparation en attente d'un redémarrage"); RebootAdvised = true; }
                    else Warn("SFC terminé (code " + rc + "), résultat non reconnu : voir le journal");
                    Log.Write(all);
                    break;
                }
                case "drivers": Drivers(); break;
                case "oem": OemTool(); break;
                case "optimize":
                {
                    int rc = Cmd(Proc.Sys("defrag.exe"), Proc.SysDrive.TrimEnd('\\') + " /O /U", 120, null, l => l.Contains("%"));
                    if (rc == 0) Ok("disque système optimisé (TRIM pour SSD, défragmentation pour HDD)"); else Warn("optimisation : code " + rc);
                    break;
                }
            }
        }
    }

    // ===================================================================== Fenêtre
    class MainForm : CyberForm
    {
        static readonly List<Job> Jobs = new List<Job>
        {
            new Job { Key = "restore",   Label = "Point de restauration avant maintenance", Hint = "1 min",  Quick = true,  Full = true },
            new Job { Key = "temp",      Label = "Fichiers temporaires (tous les profils + Windows)", Hint = "1-3 min", Quick = true, Full = true },
            new Job { Key = "wu",        Label = "Cache de téléchargement Windows Update", Hint = "1 min", Quick = true,  Full = true },
            new Job { Key = "recycle",   Label = "Vider la corbeille",                    Hint = "instant", Quick = true,  Full = true },
            new Job { Key = "wer",       Label = "Rapports d'erreurs Windows",            Hint = "instant", Quick = true,  Full = true },
            new Job { Key = "dns",       Label = "Vider le cache DNS",                    Hint = "instant", Quick = true,  Full = true },
            new Job { Key = "drivers",   Label = "Pilotes via Windows Update (recherche + installation)", Hint = "5-20 min", Quick = false, Full = true },
            new Job { Key = "oem",       Label = "Outil du fabricant (Dell / Lenovo / HP)", Hint = "Dell : peut redémarrer", Quick = false, Full = false },
            new Job { Key = "component", Label = "Nettoyage des composants (WinSxS)",     Hint = "5-15 min", Quick = false, Full = true },
            new Job { Key = "dism",      Label = "Réparer l'image Windows (DISM RestoreHealth)", Hint = "10-20 min", Quick = false, Full = true },
            new Job { Key = "sfc",       Label = "Vérifier les fichiers système (SFC /scannow)", Hint = "10-15 min", Quick = false, Full = true },
            new Job { Key = "optimize",  Label = "Optimiser le disque système (TRIM / défrag)", Hint = "1-10 min", Quick = false, Full = true },
        };

        readonly Chip pQuick, pFull, bGo, bCancel, bReport;
        readonly Card card, result;
        readonly Label rTitle, rSub, lStep;
        readonly NeonBar bar;
        readonly LogBox box;
        Engine engine;
        bool busy;
        string reportPath;

        public MainForm() : base("MAINTENANCE_PC", 860, 880)
        {
            SetSubtitle("Maintenance Windows en un clic  //  " + Environment.MachineName + "  //  by alex.b");
            Func<float, int> k = Theme.S;

            pQuick = new Chip("⚡ RAPIDE  (~5 min)") { Active = true, AccentOf = t => t.Ok, Location = new Point(k(16), k(12)) };
            pFull = new Chip("✚ COMPLET  (~45 min)") { AccentOf = t => t.Warn };
            pFull.Location = new Point(pQuick.Right + k(8), k(12));
            pQuick.Click += (s, e) => Preset(true);
            pFull.Click += (s, e) => Preset(false);
            Body.Controls.Add(pQuick); Body.Controls.Add(pFull);

            card = new Card { Location = new Point(k(16), k(52)), Size = new Size(Body.Width - k(32), k(20) + Jobs.Count * k(28)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, StatusOf = t => t.Accent };
            int y = k(10);
            foreach (var j in Jobs)
            {
                j.Box = new CheckBox { Text = j.Label, AutoSize = true, Location = new Point(k(20), y), Checked = j.Quick };
                var h = new Label { Text = j.Hint, AutoSize = true, Location = new Point(k(520), y + k(3)), Tag = "muted", Font = new Font("Consolas", 8.5f) };
                card.Controls.Add(j.Box); card.Controls.Add(h);
                y += k(28);
            }
            Body.Controls.Add(card);

            int top = card.Bottom + k(12);
            bGo = new Chip("▶  LANCER LA MAINTENANCE") { Primary = true, AccentOf = t => t.Ok, Location = new Point(k(16), top) };
            bCancel = new Chip("Annuler") { AccentOf = t => t.Bad, Enabled = false };
            bReport = new Chip("Ouvrir le rapport") { AccentOf = t => t.Muted, Enabled = false };
            bCancel.Location = new Point(bGo.Right + k(8), top);
            bReport.Location = new Point(bCancel.Right + k(8), top);
            bGo.Click += (s, e) => Go();
            bCancel.Click += (s, e) => { if (engine != null) engine.Cancel = true; lStep.Text = "annulation après l'étape en cours…"; };
            bReport.Click += (s, e) => { if (reportPath != null) Process.Start("notepad.exe", "\"" + reportPath + "\""); };
            Body.Controls.AddRange(new Control[] { bGo, bCancel, bReport });

            bar = new NeonBar { Location = new Point(k(16), top + k(42)), Width = Body.Width - k(32), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            lStep = new Label { Text = "prêt", AutoSize = true, Location = new Point(k(18), top + k(56)), Tag = "accent", Font = new Font("Consolas", 9f) };
            result = new Card { Location = new Point(k(16), top + k(80)), Size = new Size(Body.Width - k(32), k(62)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, StatusOf = t => t.Muted, Visible = false };
            rTitle = new Label { AutoSize = true, Font = new Font("Consolas", 13f, FontStyle.Bold), Location = new Point(k(18), k(8)), Tag = "ok" };
            rSub = new Label { AutoSize = true, Location = new Point(k(20), k(36)), Tag = "muted" };
            result.Controls.Add(rTitle); result.Controls.Add(rSub);
            box = new LogBox { Location = new Point(k(16), top + k(150)), Size = new Size(Body.Width - k(32), Body.Height - top - k(160)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Body.Controls.AddRange(new Control[] { bar, lStep, result, box });

            long free = 0; try { free = new DriveInfo(Proc.SysDrive).AvailableFreeSpace; } catch { }
            box.Line("MaintenancePC " + AppInfo.Version + " · Développé by alex.b", "accent", true);
            box.Line("Espace libre actuel sur " + Proc.SysDrive + " : " + Fmt.Size(free), "muted");
            box.Line("RAPIDE : nettoyage (≈ 5 min).  COMPLET : + pilotes Windows Update, réparation DISM/SFC et optimisation (30 à 60 min).", "muted");
            box.Line("Outil du fabricant (Dell / Lenovo / HP) : à cocher à la main ; les pilotes les plus récents du constructeur.", "muted");
            box.Line("Ferme les logiciels ouverts avant de lancer. Rien n'est supprimé dans les documents des utilisateurs.", "muted");
            FormClosing += (s, e) => { if (busy && !Ask(this, "La maintenance est en cours (SFC/DISM peuvent être longs). Quitter quand même ?")) e.Cancel = true; };
        }

        void Preset(bool quick)
        {
            if (busy) return;
            pQuick.Active = quick; pFull.Active = !quick; pQuick.Invalidate(); pFull.Invalidate();
            foreach (var j in Jobs) j.Box.Checked = quick ? j.Quick : j.Full;
        }

        void Go()
        {
            var sel = Jobs.Where(j => j.Box.Checked).ToList();
            if (sel.Count == 0) { Warn(this, "Coche au moins une tâche."); return; }
            if (!Ask(this, sel.Count + " tâche(s) vont être exécutées. Ferme les logiciels ouverts.\nContinuer ?")) return;

            busy = true;
            foreach (Control c in new Control[] { pQuick, pFull, bGo, card }) c.Enabled = false;
            bCancel.Enabled = true; bReport.Enabled = false; result.Visible = false;
            box.Clear(); bar.Value = 0;
            UseWaitCursor = true;
            int idx = 0;
            engine = new Engine(box, s => BeginInvoke(new Action(() =>
            {
                lStep.Text = s;
                var j = sel.FirstOrDefault(x => x.Label == s);
                if (j != null) { idx = sel.IndexOf(j); bar.Value = (double)idx / sel.Count; }
            })));
            Task.Run(() =>
            {
                var start = DateTime.Now;
                try { engine.Run(sel); } catch (Exception ex) { box.Line("ERREUR : " + ex.Message, "bad", true); }
                BeginInvoke(new Action(() => Done(start)));
            });
        }

        void Done(DateTime start)
        {
            busy = false; UseWaitCursor = false;
            foreach (Control c in new Control[] { pQuick, pFull, bGo, card }) c.Enabled = true;
            bCancel.Enabled = false; bar.Value = 1; lStep.Text = "terminé";
            var dur = DateTime.Now - start;
            rTitle.Text = engine.Cancel ? "MAINTENANCE INTERROMPUE" : "MAINTENANCE TERMINÉE";
            rTitle.Tag = engine.Cancel ? "warn" : "ok";
            rSub.Text = "Espace libéré : " + Fmt.Size(engine.Freed) + "   ·   durée : " + (int)dur.TotalMinutes + " min " + dur.Seconds + " s" +
                        (engine.RebootAdvised ? "   ·   REDÉMARRAGE CONSEILLÉ" : "");
            result.StatusOf = engine.Cancel ? (Func<Theme, Color>)(t => t.Warn) : (t => t.Ok);
            result.Visible = true; Recolor(result); result.Invalidate();

            try
            {
                reportPath = Path.Combine(AppInfo.DataDir, "rapport-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt");
                File.WriteAllText(reportPath, "MaintenancePC " + AppInfo.Version + " - Développé by alex.b\r\n" + Environment.MachineName + " - " + DateTime.Now +
                    "\r\n" + rSub.Text + "\r\n\r\n" + box.Text.Replace("\n", "\r\n"), Encoding.UTF8);
                bReport.Enabled = true;
            }
            catch { }
            if (engine.RebootAdvised && Ask(this, "Des réparations nécessitent un redémarrage. Redémarrer maintenant ?"))
            {
                string o; Proc.Run(Proc.Sys("shutdown.exe"), "/r /t 10 /c \"MaintenancePC (by alex.b) : redémarrage après réparation\"", 10000, out o);
            }
        }
    }
}
