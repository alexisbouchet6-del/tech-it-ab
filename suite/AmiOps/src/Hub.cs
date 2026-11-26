// Tech IT AB - socle des modules techniques : page à tuiles + journal, exécution PowerShell / commandes, démarrage (autoruns)
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

namespace AmiOps
{
    // ===================================================================== Exécution
    static class Sh
    {
        public static string PsExe { get { return Proc.Sys(@"WindowsPowerShell\v1.0\powershell.exe"); } }

        // PowerShell encodé (aucun problème de guillemets), sortie UTF-8, lignes en direct si onLine
        public static int Ps(string script, int timeoutMs, Action<string> onLine, out string output)
        {
            var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script));
            return Proc.Run(PsExe, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64, timeoutMs, Encoding.UTF8, onLine, out output);
        }
        public static string Ps(string script, int timeoutMs = 120000) { string o; Ps(script, timeoutMs, null, out o); return o ?? ""; }

        public static int Cmd(string exe, string args, int timeoutMs, Action<string> onLine = null, Encoding enc = null)
        {
            string o; return Proc.Run(exe, args, timeoutMs, enc, onLine, out o);
        }
        public static string CmdOut(string exe, string args, int timeoutMs = 60000, Encoding enc = null) { string o; Proc.Run(exe, args, timeoutMs, enc, null, out o); return o ?? ""; }

        public static string Q(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }   // littéral PowerShell sûr

        public static bool Service(string name, bool start)
        {
            return Cmd(Proc.Sys("net.exe"), (start ? "start " : "stop ") + name + (start ? "" : " /y"), 120000) == 0;
        }

        public static long DirSize(string dir, DateTime olderThan)
        {
            long s = 0;
            try
            {
                var st = new Stack<DirectoryInfo>(); st.Push(new DirectoryInfo(dir));
                while (st.Count > 0)
                {
                    var d = st.Pop();
                    try { foreach (var f in d.GetFiles()) if (f.LastWriteTime < olderThan) s += f.Length; } catch { }
                    try { foreach (var c in d.GetDirectories()) if ((c.Attributes & FileAttributes.ReparsePoint) == 0) st.Push(c); } catch { }
                }
            }
            catch { }
            return s;
        }

        // supprime le contenu (fichiers plus vieux que la date), ignore les fichiers verrouillés ; renvoie les octets libérés
        public static long Purge(string dir, DateTime olderThan)
        {
            long freed = 0; if (!Directory.Exists(dir)) return 0;
            var di = new DirectoryInfo(dir);
            try
            {
                foreach (var f in di.GetFiles()) if (f.LastWriteTime < olderThan) try { long l = f.Length; f.Attributes = FileAttributes.Normal; f.Delete(); freed += l; } catch { }
                foreach (var c in di.GetDirectories())
                {
                    if ((c.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    freed += Purge(c.FullName, olderThan);
                    try { if (!c.EnumerateFileSystemInfos().Any()) c.Delete(); } catch { }
                }
            }
            catch { }
            return freed;
        }
    }

    // ===================================================================== Page à tuiles + journal
    abstract class HubPage : Page
    {
        protected readonly FlowLayoutPanel tiles;
        protected readonly LogBox box;
        protected readonly Label sub;

        protected HubPage(string title, string subtitle, int tilesHeight)
        {
            Lbl(title, 0, k(2), "accent", 13f, true);
            sub = Lbl(subtitle, k(16) + TextRenderer.MeasureText(title, new Font("Consolas", 13f, FontStyle.Bold)).Width, k(10), "muted");
            tiles = new FlowLayoutPanel { Location = new Point(0, k(40)), Height = k(tilesHeight), AutoScroll = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            box = new LogBox { Location = new Point(0, k(48 + tilesHeight)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Controls.Add(tiles); Controls.Add(box);
            Resize += (s, e) => { tiles.Width = ClientSize.Width; box.Size = new Size(ClientSize.Width, Math.Max(k(80), ClientSize.Height - box.Top)); };
        }

        protected Tile T(string glyph, string title, string hint, Func<Theme, Color> acc, Action go, bool background = true, string confirm = null, string group = null)
        {
            var t = new Tile { Glyph = glyph, Title = title, Hint = hint, AccentOf = acc, Size = new Size(k(150), k(108)) };
            t.Click += (s, e) =>
            {
                if (confirm != null && !CyberForm.Ask(this, confirm)) return;
                if (!background) { try { go(); } catch (Exception ex) { Out("Erreur : " + ex.Message, "bad"); Log.Write(ex.ToString()); } return; }
                Start(title, go, group);
            };
            tiles.Controls.Add(t); return t;
        }

        // chaque action devient une tâche du centre des tâches (parallèle, annulable, historisée)
        public void Start(string title, Action go, string group = null)
        {
            if (Jobs.Busy(title)) { CyberForm.Info(this, "« " + title + " » est déjà en cours (page Tâches)."); return; }
            box.Line("", "text"); box.Line("▶ " + title + " — " + DateTime.Now.ToString("HH:mm:ss") + (group != null && Jobs.All.Any(j => !j.Finished && j.Group == group) ? " (en file d'attente)" : ""), "accent", true);
            Jobs.Run(title, group, j => go());
        }

        public void Out(string s, string c) { box.Line(s, c); var j = Job.Current; if (j != null) j.Record(s, c); }
        public void Ui(Action a) { try { if (IsHandleCreated) BeginInvoke(a); } catch { } }
        public bool AskUi(string q) { bool r = false; if (InvokeRequired) Invoke(new Action(() => r = CyberForm.Ask(this, q))); else r = CyberForm.Ask(this, q); return r; }
        public string Text_() { return box.Text.Replace("\n", "\r\n"); }
    }

    // ===================================================================== Programmes au démarrage (type Autoruns)
    class StartItem
    {
        public string Kind = "", Hive = "", Key = "", Name = "", Command = "", File = "", Publisher = "", Sig = "", Sha = "", Note = "";
        public bool Enabled = true, Recommend;
        public int Vt = -2;   // -2 non vérifié, -1 inconnu de VirusTotal, sinon nb de moteurs « malveillant »
        public bool Suspicious { get { return Kind == "WMI" || (Sig != "Valid" && Sig != "" && Sig != "?") || Vt > 0; } }
    }

    static class Startup
    {
        static readonly Regex NonEssential = new Regex(@"spotify|steam|discord|epicgames|epic games|origin|ubisoft|battle\.net|riot|skype|ccleaner|utorrent|bittorrent|qbittorrent|itunes|ipod|adobe ?(arm|updater|creative cloud|gc invoker)|acrobat.*update|cc ?library|googleupdate|google update|edgeupdate|opera|vivaldi|brave|yandex|whatsapp|messenger|cortana|logi ?options|razer|icue|corsair|nahimic|armoury|wallpaper|copilot|clipchamp|xbox|gamebar|dropbox|mega(sync)?|teamviewer|anydesk|zoom|java ?update|jusched|ituneshelper|lghub|lightshot|greenshot", RegexOptions.IgnoreCase);

        static string Exe(string cmd)
        {
            cmd = Environment.ExpandEnvironmentVariables((cmd ?? "").Trim());
            if (cmd.StartsWith("\"")) { int i = cmd.IndexOf('"', 1); return i > 0 ? cmd.Substring(1, i - 1) : cmd.Trim('"'); }
            var m = Regex.Match(cmd, @"^(.+?\.(exe|com|bat|cmd|vbs|js|ps1|dll|scr))(\s|,|$)", RegexOptions.IgnoreCase);
            var p = m.Success ? m.Groups[1].Value : cmd.Split(' ')[0];
            if (p.StartsWith(@"\??\")) p = p.Substring(4);
            if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot"), p.Substring(12));
            if (!Path.IsPathRooted(p)) { var s = Path.Combine(Environment.SystemDirectory, p); if (File.Exists(s)) return s; }
            return p;
        }

        static byte[] Approved(RegistryKey root, string sub, string name)
        {
            try { using (var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + sub)) return k == null ? null : k.GetValue(name) as byte[]; } catch { return null; }
        }

        public static List<StartItem> Scan(bool services, bool tasks)
        {
            var l = new List<StartItem>();
            var runs = new[]
            {
                Tuple.Create(Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run"),
                Tuple.Create(Registry.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32"),
                Tuple.Create(Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "Run"),
                Tuple.Create(Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", ""),
                Tuple.Create(Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\RunOnce", ""),
            };
            foreach (var r in runs)
                try
                {
                    using (var k = RegistryKey.OpenBaseKey(r.Item1 == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64).OpenSubKey(r.Item3))
                        if (k != null) foreach (var n in k.GetValueNames())
                            {
                                if (n.Length == 0) continue;
                                var cmd = Convert.ToString(k.GetValue(n));
                                var ap = r.Item4.Length > 0 ? Approved(r.Item1, r.Item4, n) : null;
                                l.Add(new StartItem { Kind = r.Item4.Length > 0 ? "Registre" : "RunOnce", Hive = r.Item2, Key = r.Item4, Name = n, Command = cmd, File = Exe(cmd), Enabled = ap == null || (ap[0] & 1) == 0 });
                            }
                }
                catch { }
            foreach (var t in new[] { Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "HKLM"), Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "HKCU") })
                try
                {
                    foreach (var f in Directory.GetFiles(t.Item1).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                    {
                        var ap = Approved(t.Item2 == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser, "StartupFolder", Path.GetFileName(f));
                        l.Add(new StartItem { Kind = "Dossier Démarrage", Hive = t.Item2, Key = "StartupFolder", Name = Path.GetFileName(f), Command = f, File = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? LnkTarget(f) : f, Enabled = ap == null || (ap[0] & 1) == 0 });
                    }
                }
                catch { }
            if (tasks)
                try
                {
                    foreach (var d in PanneScan.PowerShellJson("Get-ScheduledTask | ?{$_.TaskPath -notlike '\\Microsoft\\*'} | %{ $a=$_.Actions|select -First 1; [pscustomobject]@{N=$_.TaskName;P=$_.TaskPath;S=[string]$_.State;E=[string]$a.Execute;A=[string]$a.Arguments} } | ConvertTo-Json -Compress"))
                    {
                        Func<string, string> g = key => d.ContainsKey(key) && d[key] != null ? Convert.ToString(d[key]) : "";
                        if (g("E").Length == 0) continue;
                        l.Add(new StartItem { Kind = "Tâche planifiée", Key = g("P"), Name = g("N"), Command = (g("E") + " " + g("A")).Trim(), File = Exe("\"" + Environment.ExpandEnvironmentVariables(g("E")).Trim('"') + "\""), Enabled = g("S") != "Disabled" });
                    }
                }
                catch (Exception ex) { Log.Write("Tâches : " + ex.Message); }
            if (services)
                try
                {
                    using (var s = new System.Management.ManagementObjectSearcher("SELECT Name, DisplayName, PathName, StartMode FROM Win32_Service WHERE StartMode = 'Auto' OR StartMode = 'Manual'"))
                        foreach (System.Management.ManagementObject o in s.Get())
                        {
                            var path = Convert.ToString(o["PathName"]); var file = Exe(path);
                            if (file.StartsWith(Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows", StringComparison.OrdinalIgnoreCase) && !file.ToLowerInvariant().Contains("\\temp\\")) continue;
                            var mode = Convert.ToString(o["StartMode"]);
                            l.Add(new StartItem { Kind = "Service", Name = Convert.ToString(o["Name"]), Note = Convert.ToString(o["DisplayName"]), Command = path, File = file, Enabled = mode == "Auto" });
                        }
                }
                catch { }
            // persistance WMI : quasi toujours malveillante sur un poste de bureau
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher(@"root\subscription", "SELECT Name, CommandLineTemplate FROM CommandLineEventConsumer"))
                    foreach (System.Management.ManagementObject o in s.Get())
                        l.Add(new StartItem { Kind = "WMI", Name = Convert.ToString(o["Name"]), Command = Convert.ToString(o["CommandLineTemplate"]), File = Exe(Convert.ToString(o["CommandLineTemplate"])), Note = "abonnement WMI (technique de persistance de malware)" });
                using (var s = new System.Management.ManagementObjectSearcher(@"root\subscription", "SELECT Name FROM ActiveScriptEventConsumer"))
                    foreach (System.Management.ManagementObject o in s.Get())
                        l.Add(new StartItem { Kind = "WMI", Name = Convert.ToString(o["Name"]), Command = "(script WMI)", Note = "script WMI permanent (très suspect)" });
            }
            catch { }
            Signatures(l);
            foreach (var i in l)
            {
                // Microsoft et Windows ne sont jamais recommandés à la désactivation
                var ms = i.Publisher.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 && !Regex.IsMatch(i.Name + i.Command, "teams|onedrive|edgeupdate|skype|xbox|gamebar|clipchamp|copilot", RegexOptions.IgnoreCase);
                i.Recommend = i.Kind != "Service" && !ms && NonEssential.IsMatch(i.Name + " " + i.Command);
                if (i.Kind == "Service") i.Recommend = Regex.IsMatch(i.Name + " " + i.Note, @"(google|edge)update|adobearm|steam client|epic|origin|ubisoft|battle\.net|gupdate|brave|opera|logi|razer|corsair|nahimic|armoury|wallpaper", RegexOptions.IgnoreCase);
            }
            return l.OrderByDescending(i => i.Suspicious).ThenBy(i => i.Kind).ThenBy(i => i.Name).ToList();
        }

        static string LnkTarget(string lnk)
        {
            try { var o = Sh.Ps("(New-Object -ComObject WScript.Shell).CreateShortcut(" + Sh.Q(lnk) + ").TargetPath", 20000).Trim(); return o.Length > 0 ? o : lnk; } catch { return lnk; }
        }

        // signature Authenticode + éditeur, en un seul appel PowerShell
        static void Signatures(List<StartItem> l)
        {
            var files = l.Select(i => i.File).Where(f => f.Length > 3 && File.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) return;
            var tmp = Path.Combine(Path.GetTempPath(), "amiops-sig-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(tmp, files, Encoding.UTF8);
            try
            {
                var res = PanneScan.PowerShellJson("Get-Content -LiteralPath " + Sh.Q(tmp) + " -Encoding UTF8 | %{ $s=Get-AuthenticodeSignature -LiteralPath $_; $cn=''; if($s.SignerCertificate){ $cn=($s.SignerCertificate.Subject -split ',')[0] -replace '^CN=','' -replace '\"','' }; [pscustomobject]@{F=$_;S=[string]$s.Status;P=$cn} } | ConvertTo-Json -Compress");
                var map = res.Where(d => d.ContainsKey("F")).ToDictionary(d => Convert.ToString(d["F"]), d => d, StringComparer.OrdinalIgnoreCase);
                foreach (var i in l)
                {
                    Dictionary<string, object> d;
                    if (map.TryGetValue(i.File, out d)) { i.Sig = Convert.ToString(d["S"]); i.Publisher = Convert.ToString(d["P"]); }
                    else i.Sig = File.Exists(i.File) ? "?" : "Absent";
                    if (i.Publisher.Length == 0) try { if (File.Exists(i.File)) i.Publisher = FileVersionInfo.GetVersionInfo(i.File).CompanyName ?? ""; } catch { }
                }
            }
            catch (Exception ex) { Log.Write("Signatures : " + ex.Message); }
            finally { try { File.Delete(tmp); } catch { } }
        }

        public static bool SetEnabled(StartItem i, bool on, Action<string, string> log)
        {
            try
            {
                if (i.Kind == "Registre" || i.Kind == "Dossier Démarrage")
                {
                    // même mécanisme que le Gestionnaire des tâches (onglet Démarrage) : réversible
                    var root = i.Hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                    using (var k = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + i.Key))
                    {
                        var v = new byte[12]; v[0] = (byte)(on ? 2 : 3);
                        if (!on) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(v, 4);
                        k.SetValue(i.Name, v, RegistryValueKind.Binary);
                    }
                }
                else if (i.Kind == "Tâche planifiée")
                {
                    if (Sh.Cmd(Proc.Sys("schtasks.exe"), "/Change /TN \"" + (i.Key + i.Name).Replace("\"", "") + "\" " + (on ? "/ENABLE" : "/DISABLE"), 30000) != 0) throw new Exception("schtasks a refusé");
                }
                else if (i.Kind == "Service")
                {
                    if (!Regex.IsMatch(i.Name, @"^[\w\-. ]+$")) throw new Exception("nom de service inattendu");
                    if (Sh.Cmd(Proc.Sys("sc.exe"), "config \"" + i.Name + "\" start= " + (on ? "auto" : "demand"), 30000) != 0) throw new Exception("sc config a refusé");
                }
                else if (i.Kind == "WMI" && !on)
                {
                    Sh.Ps("Get-WmiObject -Namespace root\\subscription -Class __FilterToConsumerBinding | ?{ $_.Consumer -like ('*' + " + Sh.Q(i.Name) + " + '*') } | Remove-WmiObject;" +
                          "Get-WmiObject -Namespace root\\subscription -Class __EventConsumer | ?{ $_.Name -eq " + Sh.Q(i.Name) + " } | Remove-WmiObject", 60000);
                }
                else throw new Exception("type non modifiable");
                i.Enabled = on;
                log((on ? "Réactivé : " : "Désactivé : ") + i.Name + (i.Kind == "Service" ? (on ? " (automatique)" : " (démarrage manuel)") : ""), "ok");
                Log.Write("Démarrage " + (on ? "réactivé" : "désactivé") + " : " + i.Kind + " " + i.Name);
                return true;
            }
            catch (Exception ex) { log(i.Name + " : " + ex.Message, "bad"); return false; }
        }

        public static string Sha256(string file)
        {
            using (var s = SHA256.Create()) using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(s.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        // VirusTotal : seule l'empreinte SHA-256 est envoyée, jamais le fichier
        public static int VirusTotal(string sha, string key)
        {
            var req = (HttpWebRequest)WebRequest.Create("https://www.virustotal.com/api/v3/files/" + sha);
            req.Headers["x-apikey"] = key; req.Timeout = 20000; req.UserAgent = "AmiOps";
            try
            {
                using (var r = (HttpWebResponse)req.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream()))
                {
                    var m = Regex.Match(sr.ReadToEnd(), "\"last_analysis_stats\"\\s*:\\s*\\{[^}]*\"malicious\"\\s*:\\s*(\\d+)");
                    return m.Success ? int.Parse(m.Groups[1].Value) : -1;
                }
            }
            catch (WebException ex) { var r = ex.Response as HttpWebResponse; if (r != null && r.StatusCode == HttpStatusCode.NotFound) return -1; throw; }
        }

        public static string VtKey
        {
            get
            {
                var v = Prefs.Ini.Get("VtKey", ""); if (v.Length == 0) return "";
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v), null, DataProtectionScope.CurrentUser)); } catch { return ""; }
            }
            set { Prefs.Ini.Set("VtKey", string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser))); Prefs.Save(); }
        }
    }

    // ===================================================================== Fenêtre « Démarrage » (case cochée = actif)
    class StartupForm : Form
    {
        readonly Grid g; readonly Label stat; List<StartItem> items; readonly Action<string, string> log;

        public StartupForm(List<StartItem> list, Action<string, string> logger, bool malwareView)
        {
            items = list; log = logger;
            Func<float, int> k = Theme.S;
            Text = (malwareView ? "Démarrage & malwares" : "Programmes au démarrage") + " · by alex.b"; Size = new Size(k(1180), k(720)); StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Current.Bg; ShowInTaskbar = false; Font = new Font("Segoe UI", 9f);
            g = new Grid { CheckBoxes = true, Dock = DockStyle.Fill };
            g.Col("Type", 120).Col("Nom", 200).Col("Éditeur", 170).Col("Signature", 90).Col("VirusTotal", 90).Col("Avis", 170).Col("Commande", 380);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = k(44), Padding = new Padding(k(6)) };
            Action<string, Func<Theme, Color>, Action> b = (t, a, go) => { var c = new Chip(t) { AccentOf = a, Margin = new Padding(0, 0, k(6), 0) }; c.Click += (s, e) => go(); bar.Controls.Add(c); };
            b("Décocher les recommandés", t => t.Accent, () => { foreach (ListViewItem it in g.Items) if (((StartItem)it.Tag).Recommend) it.Checked = false; });
            b("✓ Appliquer", t => t.Ok, Apply);
            b("Vérifier sur VirusTotal (non signés)", t => t.Warn, () => Vt(false));
            b("Tout vérifier sur VirusTotal", t => t.Muted, () => Vt(true));
            b("Ouvrir l'emplacement", t => t.Muted, () => { var i = g.Sel as StartItem; if (i != null && File.Exists(i.File)) Process.Start("explorer.exe", "/select,\"" + i.File + "\""); });
            b("Copier la ligne", t => t.Muted, () => { var i = g.Sel as StartItem; if (i != null) Clip.Text(i.Kind + " | " + i.Name + " | " + i.Publisher + " | " + i.Sig + " | " + i.Command); });
            stat = new Label { Dock = DockStyle.Bottom, Height = k(40), Tag = "muted", Padding = new Padding(k(6)), Text = "Case cochée = se lance au démarrage. Désactiver est réversible (même mécanisme que le Gestionnaire des tâches ; services → démarrage manuel). « Avis » = recommandation " + Ed.Name + ". Rouge = suspect (non signé, abonnement WMI, détecté par VirusTotal)." };
            Controls.Add(g); Controls.Add(bar); Controls.Add(stat);
            Fill(); CyberForm.Recolor(this);
        }

        void Fill()
        {
            var t = Theme.Current; g.BeginUpdate(); g.Items.Clear();
            foreach (var i in items)
            {
                var avis = i.Kind == "WMI" ? "SUSPECT : à supprimer" : i.Vt > 0 ? "MALVEILLANT (" + i.Vt + " moteurs)" : (i.Sig == "NotSigned" || i.Sig == "HashMismatch") ? "non signé : vérifier" : i.Sig == "Absent" ? "fichier absent : entrée orpheline" : i.Recommend ? "inutile au démarrage" : "";
                g.Items.Add(new ListViewItem(new[] { i.Kind, i.Name + (i.Note.Length > 0 && i.Kind == "Service" ? " (" + i.Note + ")" : ""), i.Publisher, i.Sig == "Valid" ? "valide" : i.Sig == "NotSigned" ? "NON SIGNÉ" : i.Sig, i.Vt == -2 ? "" : i.Vt == -1 ? "inconnu" : i.Vt + " / 70+", avis, i.Command })
                { Tag = i, Checked = i.Enabled, ForeColor = i.Suspicious ? t.Bad : i.Sig == "Absent" ? t.Muted : i.Recommend ? t.Warn : t.Text });
            }
            g.EndUpdate();
        }

        void Apply()
        {
            var changes = g.Items.Cast<ListViewItem>().Where(it => ((StartItem)it.Tag).Enabled != it.Checked).ToList();
            if (changes.Count == 0) { CyberForm.Info(this, "Aucun changement."); return; }
            if (!CyberForm.Ask(this, changes.Count + " élément(s) modifié(s) :\n\n" + string.Join("\n", changes.Take(15).Select(it => (it.Checked ? "✓ réactiver  " : "✗ désactiver ") + ((StartItem)it.Tag).Name)) + (changes.Count > 15 ? "\n…" : "") + "\n\nAppliquer ?")) return;
            foreach (var it in changes) Startup.SetEnabled((StartItem)it.Tag, it.Checked, log);
            Fill(); stat.Text = "Appliqué. Effet au prochain démarrage de session (services : prochain redémarrage).";
        }

        void Vt(bool all)
        {
            var key = Startup.VtKey;
            if (key.Length == 0)
            {
                key = Dlg.Ask(this, "VirusTotal", "Clé API VirusTotal (gratuite sur virustotal.com > profil > API key). Stockée chiffrée (DPAPI) sur ce PC.", "", true);
                if (string.IsNullOrEmpty(key)) return; Startup.VtKey = key;
            }
            var todo = items.Where(i => File.Exists(i.File) && (all || i.Sig != "Valid")).GroupBy(i => i.File, StringComparer.OrdinalIgnoreCase).ToList();
            if (todo.Count == 0) { CyberForm.Info(this, "Rien à vérifier (tous les fichiers sont signés)."); return; }
            stat.Text = "VirusTotal : " + todo.Count + " fichier(s), 4 par minute (limite de l'API gratuite)…";
            Task.Run(() =>
            {
                int n = 0;
                foreach (var grp in todo)
                {
                    int res;
                    try { res = Startup.VirusTotal(Startup.Sha256(grp.Key), key); }
                    catch (Exception ex) { BeginInvoke(new Action(() => stat.Text = "VirusTotal : " + ex.Message)); if (ex.Message.Contains("401")) { Startup.VtKey = ""; return; } res = -2; }
                    foreach (var i in grp) i.Vt = res;
                    n++; int nn = n; BeginInvoke(new Action(() => { Fill(); stat.Text = "VirusTotal : " + nn + " / " + todo.Count; }));
                    if (n < todo.Count) Thread.Sleep(15500);
                }
                BeginInvoke(new Action(() => stat.Text = "VirusTotal terminé : " + items.Count(i => i.Vt > 0) + " fichier(s) détecté(s). Seules les empreintes SHA-256 ont été envoyées."));
            });
        }
    }
}
