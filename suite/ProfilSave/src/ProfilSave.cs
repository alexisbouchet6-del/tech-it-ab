// ProfilSave - sauvegarde et restauration d'un profil utilisateur avant réinstallation / changement de PC
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

[assembly: AssemblyTitle("ProfilSave")]
[assembly: AssemblyDescription("Sauvegarde et restauration de profil utilisateur")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("ProfilSave")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace ProfilSave
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            AppInfo.Name = "ProfilSave"; AppInfo.Id = "ProfilSave"; AppInfo.Version = "1.1.0"; AppInfo.MachineWide = true;
            Boot.Run(() => new MainForm(), true, "lecture des profils des autres utilisateurs et copie de leurs fichiers.");
        }
    }

    // ===================================================================== Profils
    class UserProfile
    {
        public string Name, Path, Sid;
        public bool IsCurrent;
        public override string ToString() { return Name + (IsCurrent ? "  (session actuelle)" : ""); }

        public static List<UserProfile> All()
        {
            var list = new List<UserProfile>();
            string mySid = "";
            try { mySid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; } catch { }
            try
            {
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var pl = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                    foreach (var sid in pl.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-") && !s.EndsWith(".bak")))
                        using (var k = pl.OpenSubKey(sid))
                        {
                            var p = Environment.ExpandEnvironmentVariables(Convert.ToString(k.GetValue("ProfileImagePath")));
                            if (!Directory.Exists(p)) continue;
                            list.Add(new UserProfile { Sid = sid, Path = p, Name = System.IO.Path.GetFileName(p), IsCurrent = sid == mySid });
                        }
            }
            catch (Exception ex) { Log.Write("Profils : " + ex.Message); }
            return list.OrderByDescending(p => p.IsCurrent).ThenBy(p => p.Name).ToList();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        static readonly Dictionary<string, string> RegNames = new Dictionary<string, string>
        {
            { "Desktop", "Desktop" }, { "Documents", "Personal" }, { "Pictures", "My Pictures" },
            { "Videos", "My Video" }, { "Music", "My Music" }, { "Downloads", "{374DE290-123F-4565-9164-39C4925E467B}" }
        };

        // Chemin réel d'un dossier (gère la redirection OneDrive)
        public string Folder(string kind)
        {
            if (IsCurrent)
            {
                switch (kind)
                {
                    case "Desktop": return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    case "Documents": return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    case "Pictures": return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    case "Videos": return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                    case "Music": return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                    case "Downloads":
                        IntPtr p;
                        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
                        {
                            var s = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p); return s;
                        }
                        break;
                }
            }
            // autre utilisateur : registre de sa session si chargée, sinon emplacements habituels
            try
            {
                using (var k = Registry.Users.OpenSubKey(Sid + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                {
                    var v = k == null ? null : k.GetValue(RegNames[kind], null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (!string.IsNullOrEmpty(v))
                    {
                        var exp = v.Replace("%USERPROFILE%", Path).Replace("%userprofile%", Path);
                        if (Directory.Exists(exp)) return exp;
                    }
                }
            }
            catch { }
            var fr = new Dictionary<string, string[]>
            {
                { "Desktop", new[] { "Desktop", "Bureau" } }, { "Documents", new[] { "Documents" } },
                { "Pictures", new[] { "Pictures", "Images" } }, { "Videos", new[] { "Videos", "Vidéos" } },
                { "Music", new[] { "Music", "Musique" } }, { "Downloads", new[] { "Downloads", "Téléchargements" } }
            };
            foreach (var od in Directory.Exists(Path) ? Directory.GetDirectories(Path, "OneDrive*") : new string[0])
                foreach (var n in fr[kind])
                    if (Directory.Exists(System.IO.Path.Combine(od, n)) && Directory.EnumerateFileSystemEntries(System.IO.Path.Combine(od, n)).Any())
                        return System.IO.Path.Combine(od, n);
            return System.IO.Path.Combine(Path, fr[kind][0]);
        }

        public string Roaming { get { return System.IO.Path.Combine(Path, @"AppData\Roaming"); } }
        public string Local { get { return System.IO.Path.Combine(Path, @"AppData\Local"); } }
    }

    // ===================================================================== Éléments sauvegardés
    class Item
    {
        public string Key, Label, Hint;
        public bool Default, Sensitive;
        public CheckBox Box;
    }

    static class Items
    {
        public static readonly List<Item> All = new List<Item>
        {
            new Item { Key = "Desktop",   Label = "Bureau",                     Default = true },
            new Item { Key = "Documents", Label = "Documents",                  Default = true },
            new Item { Key = "Downloads", Label = "Téléchargements",            Default = true },
            new Item { Key = "Pictures",  Label = "Images",                     Default = true },
            new Item { Key = "Videos",    Label = "Vidéos",                     Default = false },
            new Item { Key = "Music",     Label = "Musique",                    Default = false },
            new Item { Key = "Browsers",  Label = "Favoris Chrome / Edge / Firefox", Default = true },
            new Item { Key = "Signatures",Label = "Signatures Outlook",         Default = true },
            new Item { Key = "Pst",       Label = "Fichiers PST Outlook",       Default = true, Hint = "fermer Outlook" },
            new Item { Key = "Printers",  Label = "Liste des imprimantes",      Default = true },
            new Item { Key = "Drives",    Label = "Lecteurs réseau mappés",     Default = true },
            new Item { Key = "Software",  Label = "Liste des logiciels",        Default = true },
            new Item { Key = "Wifi",      Label = "Profils Wi-Fi + mots de passe", Default = false, Sensitive = true, Hint = "mots de passe en clair" },
        };
    }

    // ===================================================================== Moteur
    class Engine
    {
        readonly LogBox log;
        readonly Action<double, string> progress;
        public volatile bool Cancel;

        public Engine(LogBox l, Action<double, string> p) { log = l; progress = p; }

        void Ok(string s) { log.Line("  ✓ " + s, "ok"); Log.Write("OK " + s); }
        void Warn(string s) { log.Line("  ! " + s, "warn"); Log.Write("WARN " + s); }
        void Bad(string s) { log.Line("  ✗ " + s, "bad"); Log.Write("ERR " + s); }
        void Info(string s) { log.Line("  " + s, "muted"); }
        void Step(string s) { log.Line("» " + s, "accent", true); }

        bool Robocopy(string src, string dst, string label)
        {
            if (!Directory.Exists(src)) { Info(label + " : dossier absent (" + src + ")"); return true; }
            Directory.CreateDirectory(dst);
            string o;
            int rc = Proc.Run(Proc.Sys("robocopy.exe"),
                "\"" + src.TrimEnd('\\') + "\" \"" + dst.TrimEnd('\\') + "\" /E /R:1 /W:1 /XJ /MT:8 /NP /NFL /NDL /NJH " +
                "/XF desktop.ini thumbs.db ~$* *.tmp /XD \"$RECYCLE.BIN\" \"System Volume Information\"",
                6 * 60 * 60 * 1000, null, line =>
                {
                    var l = line.Trim();
                    if (l.StartsWith("Rép") || l.StartsWith("Dirs") || l.StartsWith("Fich") || l.StartsWith("Files") || l.StartsWith("Octets") || l.StartsWith("Bytes"))
                        Info("    " + System.Text.RegularExpressions.Regex.Replace(l, @"\s{2,}", "  "));
                }, out o);
            if (rc >= 8) { Bad(label + " : erreurs de copie (code robocopy " + rc + "), voir le journal"); Log.Write(o); return false; }
            Ok(label + " → " + dst);
            return true;
        }

        static string Safe(string s) { foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }

        // ------------------------------------------------------------------ SAUVEGARDE
        public string Backup(UserProfile u, string destRoot, List<Item> items)
        {
            string target = System.IO.Path.Combine(destRoot, Safe(Environment.MachineName + "_" + u.Name + "_" + DateTime.Now.ToString("yyyyMMdd-HHmm")));
            Directory.CreateDirectory(target);
            log.Line("SAUVEGARDE de « " + u.Name + " » vers " + target, "accent", true);
            int i = 0, errors = 0;
            var done = new List<string>();

            foreach (var it in items)
            {
                if (Cancel) { Warn("Annulé par l'utilisateur"); break; }
                progress((double)i / items.Count, it.Label);
                Step(it.Label);
                try
                {
                    bool ok = true;
                    switch (it.Key)
                    {
                        case "Desktop": case "Documents": case "Downloads": case "Pictures": case "Videos": case "Music":
                            ok = Robocopy(u.Folder(it.Key), System.IO.Path.Combine(target, it.Key), it.Label); break;
                        case "Browsers": ok = BackupBrowsers(u, System.IO.Path.Combine(target, "Navigateurs")); break;
                        case "Signatures": ok = Robocopy(System.IO.Path.Combine(u.Roaming, @"Microsoft\Signatures"), System.IO.Path.Combine(target, @"Outlook\Signatures"), it.Label); break;
                        case "Pst": ok = BackupPst(u, System.IO.Path.Combine(target, @"Outlook\PST")); break;
                        case "Printers": ok = ExportPrinters(System.IO.Path.Combine(target, "imprimantes.csv")); break;
                        case "Drives": ok = ExportDrives(u, System.IO.Path.Combine(target, "lecteurs-reseau.txt")); break;
                        case "Software": ok = ExportSoftware(System.IO.Path.Combine(target, "logiciels.txt")); break;
                        case "Wifi": ok = ExportWifi(System.IO.Path.Combine(target, "WiFi")); break;
                    }
                    if (ok) done.Add(it.Key); else errors++;
                }
                catch (Exception ex) { Bad(it.Label + " : " + ex.Message); errors++; }
                i++;
            }

            var man = new Ini(System.IO.Path.Combine(target, "manifest.ini"));
            man.Set("SourcePC", Environment.MachineName); man.Set("SourceUser", u.Name);
            man.Set("Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm")); man.Set("Items", string.Join(",", done));
            man.Set("Tool", "ProfilSave " + AppInfo.Version);
            man.Save("ProfilSave - manifeste de sauvegarde");
            progress(1, "terminé");
            log.Line(errors == 0 ? "SAUVEGARDE TERMINÉE SANS ERREUR" : "SAUVEGARDE TERMINÉE AVEC " + errors + " ERREUR(S)", errors == 0 ? "ok" : "warn", true);
            SaveReport(target);
            return target;
        }

        bool BackupBrowsers(UserProfile u, string dst)
        {
            int n = 0;
            foreach (var b in new[] { new[] { "Chrome", @"Google\Chrome\User Data" }, new[] { "Edge", @"Microsoft\Edge\User Data" } })
            {
                var root = System.IO.Path.Combine(u.Local, b[1]);
                if (!Directory.Exists(root)) continue;
                foreach (var prof in Directory.GetDirectories(root).Where(d => { var n2 = System.IO.Path.GetFileName(d); return n2 == "Default" || n2.StartsWith("Profile "); }))
                {
                    var bm = System.IO.Path.Combine(prof, "Bookmarks");
                    if (!File.Exists(bm)) continue;
                    var d2 = System.IO.Path.Combine(dst, b[0], System.IO.Path.GetFileName(prof));
                    Directory.CreateDirectory(d2);
                    File.Copy(bm, System.IO.Path.Combine(d2, "Bookmarks"), true);
                    n++; Ok(b[0] + " / " + System.IO.Path.GetFileName(prof));
                }
            }
            var ff = System.IO.Path.Combine(u.Roaming, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(ff))
                foreach (var prof in Directory.GetDirectories(ff))
                    foreach (var f in new[] { "places.sqlite", "favicons.sqlite" })
                    {
                        var src = System.IO.Path.Combine(prof, f);
                        if (!File.Exists(src)) continue;
                        var d2 = System.IO.Path.Combine(dst, "Firefox", System.IO.Path.GetFileName(prof));
                        Directory.CreateDirectory(d2);
                        try { File.Copy(src, System.IO.Path.Combine(d2, f), true); if (f == "places.sqlite") { n++; Ok("Firefox / " + System.IO.Path.GetFileName(prof)); } }
                        catch (IOException) { Warn("Firefox ouvert : " + f + " verrouillé, ferme Firefox et relance"); }
                    }
            if (n == 0) Info("aucun favori trouvé");
            return true;
        }

        IEnumerable<string> PstFiles(UserProfile u)
        {
            var dirs = new[]
            {
                System.IO.Path.Combine(u.Folder("Documents"), "Fichiers Outlook"), System.IO.Path.Combine(u.Folder("Documents"), "Outlook Files"),
                System.IO.Path.Combine(u.Local, @"Microsoft\Outlook")
            };
            foreach (var d in dirs.Where(Directory.Exists))
                foreach (var f in Directory.GetFiles(d, "*.pst", SearchOption.AllDirectories)) yield return f;
        }

        bool BackupPst(UserProfile u, string dst)
        {
            if (Process.GetProcessesByName("OUTLOOK").Length > 0) Warn("Outlook est ouvert : les PST ouverts ne pourront pas être copiés. Ferme Outlook pour une copie complète.");
            var files = PstFiles(u).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) { Info("aucun fichier PST trouvé (les OST se resynchronisent, inutile de les copier)"); return true; }
            Directory.CreateDirectory(dst);
            bool all = true;
            foreach (var f in files)
            {
                try { File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), true); Ok(System.IO.Path.GetFileName(f) + " (" + Fmt.Size(new FileInfo(f).Length) + ")"); }
                catch (Exception ex) { Bad(System.IO.Path.GetFileName(f) + " : " + ex.Message); all = false; }
            }
            File.WriteAllLines(System.IO.Path.Combine(dst, "emplacements-origine.txt"), files, Encoding.UTF8);
            return all;
        }

        bool ExportPrinters(string file)
        {
            var sb = new StringBuilder("Nom;Pilote;Port;Partagée;Par défaut\r\n");
            int n = 0;
            using (var s = new ManagementObjectSearcher("SELECT Name, DriverName, PortName, Shared, Default FROM Win32_Printer"))
                foreach (ManagementObject p in s.Get())
                {
                    sb.AppendLine(string.Join(";", p["Name"], p["DriverName"], p["PortName"], p["Shared"], p["Default"]));
                    n++;
                }
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
            Ok(n + " imprimante(s) listée(s) → " + System.IO.Path.GetFileName(file));
            return true;
        }

        bool ExportDrives(UserProfile u, string file)
        {
            var lines = new List<string>();
            RegistryKey net = u.IsCurrent ? Registry.CurrentUser.OpenSubKey("Network") : Registry.Users.OpenSubKey(u.Sid + @"\Network");
            if (net == null) { Info(u.IsCurrent ? "aucun lecteur réseau mappé" : "session de l'utilisateur non chargée : connecte-toi avec son compte pour lister ses lecteurs"); return true; }
            using (net)
                foreach (var l in net.GetSubKeyNames())
                    using (var k = net.OpenSubKey(l))
                        lines.Add(l.ToUpperInvariant() + ":  " + k.GetValue("RemotePath") + "   (utilisateur : " + k.GetValue("UserName") + ")");
            File.WriteAllLines(file, lines, Encoding.UTF8);
            Ok(lines.Count + " lecteur(s) → " + System.IO.Path.GetFileName(file));
            return true;
        }

        bool ExportSoftware(string file)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var un = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    if (un != null)
                        foreach (var sk in un.GetSubKeyNames())
                            using (var k = un.OpenSubKey(sk))
                            {
                                var n = k.GetValue("DisplayName") as string;
                                if (string.IsNullOrWhiteSpace(n) || Convert.ToInt32(k.GetValue("SystemComponent", 0)) == 1) continue;
                                var v = k.GetValue("DisplayVersion") as string;
                                names.Add(n.Trim() + (string.IsNullOrEmpty(v) ? "" : "  —  " + v));
                            }
            File.WriteAllLines(file, names, Encoding.UTF8);
            Ok(names.Count + " logiciels listés → " + System.IO.Path.GetFileName(file));
            return true;
        }

        bool ExportWifi(string dst)
        {
            Directory.CreateDirectory(dst);
            string o;
            int rc = Proc.Run(Proc.Sys("netsh.exe"), "wlan export profile folder=\"" + dst + "\" key=clear", 60000, out o);
            int n = Directory.GetFiles(dst, "*.xml").Length;
            if (rc != 0 && n == 0) { Info("aucun profil Wi-Fi (ou service WLAN absent)"); return true; }
            Ok(n + " profil(s) Wi-Fi exporté(s). Dossier sensible : supprime-le après restauration.");
            return true;
        }

        // ================================================================== CHIFFREMENT (7-Zip AES-256)
        public static string SevenZip
        {
            get
            {
                foreach (var p in new[] { @"%ProgramFiles%\7-Zip\7z.exe", @"%ProgramFiles(x86)%\7-Zip\7z.exe", @"%ProgramW6432%\7-Zip\7z.exe" })
                {
                    var e = Environment.ExpandEnvironmentVariables(p);
                    if (File.Exists(e)) return e;
                }
                return null;
            }
        }

        // lance 7-Zip SANS écrire la ligne de commande (qui contient le mot de passe) dans le journal
        int Run7z(string args, out string output)
        {
            var sb = new StringBuilder();
            var psi = new ProcessStartInfo(SevenZip, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler h = (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.OutputDataReceived += h; p.ErrorDataReceived += h;
                p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                p.WaitForExit();
                output = sb.ToString();
                Log.Write("7-Zip " + args.Split(' ')[0] + " [mot de passe masqué] => " + p.ExitCode);
                return p.ExitCode;
            }
        }

        static readonly string[] FolderKeys = { "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music" };

        bool ExportExtra(string key, UserProfile u, string root)
        {
            switch (key)
            {
                case "Browsers": return BackupBrowsers(u, System.IO.Path.Combine(root, "Navigateurs"));
                case "Signatures": return Robocopy(System.IO.Path.Combine(u.Roaming, @"Microsoft\Signatures"), System.IO.Path.Combine(root, @"Outlook\Signatures"), "Signatures Outlook");
                case "Pst": return BackupPst(u, System.IO.Path.Combine(root, @"Outlook\PST"));
                case "Printers": return ExportPrinters(System.IO.Path.Combine(root, "imprimantes.csv"));
                case "Drives": return ExportDrives(u, System.IO.Path.Combine(root, "lecteurs-reseau.txt"));
                case "Software": return ExportSoftware(System.IO.Path.Combine(root, "logiciels.txt"));
                case "Wifi": return ExportWifi(System.IO.Path.Combine(root, "WiFi"));
            }
            return true;
        }

        // shredAll : écrase tout ; sinon seulement les petits fichiers sensibles (Wi-Fi, favoris, listes)
        static void Wipe(string dir, bool shredAll)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    if (shredAll || new FileInfo(f).Length < 20 * 1024 * 1024) Hardening.Shred(f);
                Directory.Delete(dir, true);
            }
            catch { }
        }

        public string BackupEncrypted(UserProfile u, string destRoot, List<Item> items, string password)
        {
            string target = System.IO.Path.Combine(destRoot, Safe(Environment.MachineName + "_" + u.Name + "_" + DateTime.Now.ToString("yyyyMMdd-HHmm")));
            string staging = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ProfilSave-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(target); Directory.CreateDirectory(staging);
            log.Line("SAUVEGARDE CHIFFRÉE (7-Zip AES-256) de « " + u.Name + " » vers " + target, "accent", true);
            var man = new Ini(System.IO.Path.Combine(staging, "manifest.ini"));
            var sources = new List<string>(); var done = new List<string>();
            var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i = 0, errors = 0;
            try
            {
                foreach (var it in items)
                {
                    if (Cancel) { Warn("Annulé par l'utilisateur"); break; }
                    progress((double)i / (items.Count + 2), it.Label);
                    Step(it.Label);
                    try
                    {
                        if (FolderKeys.Contains(it.Key))
                        {
                            var src = u.Folder(it.Key);
                            if (!Directory.Exists(src)) { Info(it.Label + " : dossier absent (" + src + ")"); }
                            else
                            {
                                var leaf = System.IO.Path.GetFileName(src.TrimEnd('\\'));
                                if (!leaves.Add(leaf)) { Bad(it.Label + " : un autre dossier porte déjà le nom « " + leaf + " », ignoré"); errors++; }
                                else { sources.Add(src); man.Set("Folder." + it.Key, leaf); done.Add(it.Key); Ok(it.Label + " → ajouté à l'archive (" + src + ")"); }
                            }
                        }
                        else if (ExportExtra(it.Key, u, staging)) done.Add(it.Key); else errors++;
                    }
                    catch (Exception ex) { Bad(it.Label + " : " + ex.Message); errors++; }
                    i++;
                }
                if (Cancel) return target;

                man.Set("SourcePC", Environment.MachineName); man.Set("SourceUser", u.Name);
                man.Set("Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm")); man.Set("Items", string.Join(",", done));
                man.Set("Encrypted", "1"); man.Set("Archive", "profil.7z"); man.Set("Tool", "ProfilSave " + AppInfo.Version);
                man.Save("ProfilSave - manifeste de sauvegarde");

                progress((double)items.Count / (items.Count + 2), "chiffrement…");
                Step("Compression + chiffrement AES-256 (peut être long)");
                string archive = System.IO.Path.Combine(target, "profil.7z");
                var a = new StringBuilder("a -t7z -mhe=on -mx=1 -ssw -y -p\"" + password + "\" \"" + archive + "\"");
                foreach (var s in sources) a.Append(" \"" + s.TrimEnd('\\') + "\"");
                a.Append(" \"" + staging + "\\*\" -xr!desktop.ini -xr!thumbs.db -xr!~$* -xr!*.tmp");
                string o;
                int rc = Run7z(a.ToString(), out o);
                if (rc == 0) Ok("archive chiffrée créée : " + archive + " (" + Fmt.Size(new FileInfo(archive).Length) + ")");
                else if (rc == 1) { Warn("archive créée avec avertissements (fichiers verrouillés ignorés, ex. PST d'Outlook ouvert)"); }
                else { Bad("échec 7-Zip (code " + rc + ") : " + o.Split('\n').LastOrDefault(l => l.Trim().Length > 0)); errors++; }
                File.Copy(System.IO.Path.Combine(staging, "manifest.ini"), System.IO.Path.Combine(target, "manifest.ini"), true);
            }
            finally
            {
                Step("Effacement sécurisé des fichiers temporaires");
                Wipe(staging, true);
            }
            progress(1, "terminé");
            log.Line(errors == 0 ? "SAUVEGARDE CHIFFRÉE TERMINÉE SANS ERREUR" : "SAUVEGARDE CHIFFRÉE TERMINÉE AVEC " + errors + " ERREUR(S)", errors == 0 ? "ok" : "warn", true);
            log.Line("Sans le mot de passe, cette sauvegarde est irrécupérable : garde-le en lieu sûr.", "warn");
            SaveReport(target);
            return target;
        }

        public void RestoreEncrypted(string folder, UserProfile u, string password)
        {
            var man = new Ini(System.IO.Path.Combine(folder, "manifest.ini"));
            string archive = System.IO.Path.Combine(folder, man.Get("Archive", "profil.7z"));
            string staging = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ProfilSave-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Step("Déchiffrement de l'archive");
                progress(0.05, "déchiffrement…");
                string o;
                int rc = Run7z("x -y -p\"" + password + "\" -o\"" + staging + "\" \"" + archive + "\"", out o);
                if (rc != 0 && rc != 1)
                {
                    Bad(o.IndexOf("Wrong password", StringComparison.OrdinalIgnoreCase) >= 0 ? "mot de passe incorrect" : "échec du déchiffrement (code " + rc + ")");
                    return;
                }
                Ok("archive déchiffrée");
                foreach (var key in FolderKeys)
                {
                    var leaf = man.Get("Folder." + key, "");
                    var from = System.IO.Path.Combine(staging, leaf);
                    var to = System.IO.Path.Combine(staging, key);
                    if (leaf.Length > 0 && !leaf.Equals(key, StringComparison.OrdinalIgnoreCase) && Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
                }
                Restore(staging, u);
            }
            finally
            {
                Step("Effacement sécurisé des fichiers temporaires");
                Wipe(staging, false);
            }
        }

        void SaveReport(string folder)
        {
            try
            {
                string txt = "";
                log.Invoke(new Action(() => txt = log.Text));
                File.WriteAllText(System.IO.Path.Combine(folder, "rapport-profilsave.txt"),
                    "ProfilSave " + AppInfo.Version + " - Développé by alex.b\r\n" + DateTime.Now + "\r\n\r\n" + txt.Replace("\n", "\r\n"), Encoding.UTF8);
            }
            catch { }
        }

        // ------------------------------------------------------------------ RESTAURATION
        public void Restore(string folder, UserProfile u)
        {
            var man = new Ini(System.IO.Path.Combine(folder, "manifest.ini"));
            var items = man.Get("Items", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            log.Line("RESTAURATION de " + man.Get("SourceUser", "?") + "@" + man.Get("SourcePC", "?") + " (" + man.Get("Date", "?") + ") vers « " + u.Name + " »", "accent", true);
            int i = 0, errors = 0;
            foreach (var key in items)
            {
                if (Cancel) { Warn("Annulé par l'utilisateur"); break; }
                var def = Items.All.FirstOrDefault(x => x.Key == key);
                string label = def != null ? def.Label : key;
                progress((double)i / Math.Max(1, items.Count), label);
                Step(label);
                try
                {
                    switch (key)
                    {
                        case "Desktop": case "Documents": case "Downloads": case "Pictures": case "Videos": case "Music":
                            if (!Robocopy(System.IO.Path.Combine(folder, key), u.Folder(key), label)) errors++;
                            break;
                        case "Browsers": RestoreBrowsers(u, System.IO.Path.Combine(folder, "Navigateurs")); break;
                        case "Signatures":
                            if (!Robocopy(System.IO.Path.Combine(folder, @"Outlook\Signatures"), System.IO.Path.Combine(u.Roaming, @"Microsoft\Signatures"), label)) errors++;
                            break;
                        case "Pst":
                            var dst = System.IO.Path.Combine(u.Folder("Documents"), "Fichiers Outlook");
                            if (Robocopy(System.IO.Path.Combine(folder, @"Outlook\PST"), dst, label))
                                Warn("À faire dans Outlook : Fichier > Ouvrir et exporter > Ouvrir un fichier de données Outlook > " + dst);
                            else errors++;
                            break;
                        case "Wifi": RestoreWifi(System.IO.Path.Combine(folder, "WiFi")); break;
                        case "Printers": Info("Liste à réinstaller : " + System.IO.Path.Combine(folder, "imprimantes.csv")); break;
                        case "Drives": Info("Lecteurs à recréer (NasConnect) : " + System.IO.Path.Combine(folder, "lecteurs-reseau.txt")); break;
                        case "Software": Info("Logiciels à réinstaller : " + System.IO.Path.Combine(folder, "logiciels.txt")); break;
                    }
                }
                catch (Exception ex) { Bad(label + " : " + ex.Message); errors++; }
                i++;
            }
            progress(1, "terminé");
            log.Line(errors == 0 ? "RESTAURATION TERMINÉE SANS ERREUR" : "RESTAURATION TERMINÉE AVEC " + errors + " ERREUR(S)", errors == 0 ? "ok" : "warn", true);
            SaveReport(folder);
        }

        void RestoreBrowsers(UserProfile u, string src)
        {
            if (!Directory.Exists(src)) { Info("aucun favori dans la sauvegarde"); return; }
            foreach (var b in new[] { new[] { "Chrome", @"Google\Chrome\User Data", "chrome" }, new[] { "Edge", @"Microsoft\Edge\User Data", "msedge" } })
            {
                var from = System.IO.Path.Combine(src, b[0]);
                if (!Directory.Exists(from)) continue;
                if (Process.GetProcessesByName(b[2]).Length > 0) { Warn(b[0] + " est ouvert : ferme-le puis relance la restauration des favoris"); continue; }
                foreach (var prof in Directory.GetDirectories(from))
                {
                    var target = System.IO.Path.Combine(u.Local, b[1], System.IO.Path.GetFileName(prof));
                    if (!Directory.Exists(target)) { Warn(b[0] + " / " + System.IO.Path.GetFileName(prof) + " : ouvre " + b[0] + " une première fois sur ce compte, puis relance"); continue; }
                    var dstFile = System.IO.Path.Combine(target, "Bookmarks");
                    if (File.Exists(dstFile)) File.Copy(dstFile, dstFile + ".avant-profilsave", true);
                    File.Copy(System.IO.Path.Combine(prof, "Bookmarks"), dstFile, true);
                    Ok(b[0] + " / " + System.IO.Path.GetFileName(prof) + " (ancien fichier gardé en .avant-profilsave)");
                }
            }
            if (Directory.Exists(System.IO.Path.Combine(src, "Firefox")))
                Warn("Firefox : copie manuelle de places.sqlite dans le nouveau profil (Firefox fermé) : " + System.IO.Path.Combine(src, "Firefox"));
        }

        void RestoreWifi(string src)
        {
            if (!Directory.Exists(src)) return;
            foreach (var f in Directory.GetFiles(src, "*.xml"))
            {
                string o;
                int rc = Proc.Run(Proc.Sys("netsh.exe"), "wlan add profile filename=\"" + f + "\" user=all", 30000, out o);
                if (rc == 0) Ok("Wi-Fi " + System.IO.Path.GetFileNameWithoutExtension(f)); else Warn("Wi-Fi " + System.IO.Path.GetFileName(f) + " : " + o.Trim());
            }
            Warn("Pense à supprimer le dossier WiFi de la sauvegarde (mots de passe en clair).");
        }
    }

    // ===================================================================== Fenêtre
    class MainForm : CyberForm
    {
        readonly Chip mBackup, mRestore, bBrowse, bGo, bCancel, bOpen;
        readonly ComboBox users;
        readonly TextBox path;
        readonly Label lPath, lUser, lStep, lInfo;
        readonly CheckBox cEncrypt;
        readonly Card itemsCard;
        readonly NeonBar bar;
        readonly LogBox logBox;
        bool restoreMode, busy;
        Engine engine;
        string lastFolder;

        public MainForm() : base("PROFIL_SAVE", 860, 760)
        {
            SetSubtitle("Sauvegarde / restauration de profil avant réinstallation  //  by alex.b");
            Func<float, int> k = Theme.S;
            int W = Body.Width;

            mBackup = new Chip("SAUVEGARDER") { Active = true, AccentOf = t => t.Ok, Location = new Point(k(16), k(12)) };
            mRestore = new Chip("RESTAURER") { AccentOf = t => t.Accent2 };
            mRestore.Location = new Point(mBackup.Right + k(8), k(12));
            mBackup.Click += (s, e) => SetMode(false);
            mRestore.Click += (s, e) => SetMode(true);
            Body.Controls.Add(mBackup); Body.Controls.Add(mRestore);

            lUser = new Label { Text = "Profil à sauvegarder", AutoSize = true, Location = new Point(k(18), k(58)), Tag = "muted" };
            users = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Location = new Point(k(200), k(54)), Width = k(330), Font = new Font("Segoe UI", 10f) };
            foreach (var p in UserProfile.All()) users.Items.Add(p);
            if (users.Items.Count > 0) users.SelectedIndex = 0;

            lPath = new Label { Text = "Destination (NAS, clé…)", AutoSize = true, Location = new Point(k(18), k(96)), Tag = "muted" };
            path = new TextBox { Location = new Point(k(200), k(92)), Width = k(500), Font = new Font("Consolas", 10f), BorderStyle = BorderStyle.FixedSingle };
            path.Text = Prefs.Ini.Get("LastDest", "");
            bBrowse = new Chip("Parcourir…") { Location = new Point(k(710), k(90)) };
            bBrowse.Click += (s, e) => Browse();
            Body.Controls.AddRange(new Control[] { lUser, users, lPath, path, bBrowse });

            itemsCard = new Card { Location = new Point(k(16), k(132)), Size = new Size(k(828), k(176)), StatusOf = t => t.Accent };
            int col = 0, row = 0;
            foreach (var it in Items.All)
            {
                it.Box = new CheckBox
                {
                    Text = it.Label + (it.Hint != null ? "  (" + it.Hint + ")" : ""), Checked = Prefs.Ini.GetBool("Item." + it.Key, it.Default),
                    AutoSize = true, Location = new Point(k(20) + col * k(270), k(14) + row * k(30)), Tag = it.Sensitive ? "warn" : null
                };
                itemsCard.Controls.Add(it.Box);
                if (++col == 3) { col = 0; row++; }
            }
            Body.Controls.Add(itemsCard);
            bool has7z = Engine.SevenZip != null;
            cEncrypt = new CheckBox
            {
                Text = has7z ? "Chiffrer la sauvegarde (7-Zip AES-256, noms de fichiers compris — mot de passe demandé)" : "Chiffrement indisponible : installer 7-Zip (winget install 7zip.7zip)",
                AutoSize = true, Location = new Point(k(18), k(314)), Enabled = has7z, Checked = has7z && Prefs.Ini.GetBool("Encrypt", true), Tag = has7z ? "ok" : "muted"
            };
            lInfo = new Label { Text = "", AutoSize = true, Location = new Point(k(18), k(316)), Tag = "muted" };
            Body.Controls.Add(cEncrypt); Body.Controls.Add(lInfo);

            bGo = new Chip("▶  LANCER LA SAUVEGARDE") { Primary = true, AccentOf = t => t.Ok, Location = new Point(k(16), k(342)) };
            bCancel = new Chip("Annuler") { AccentOf = t => t.Bad, Enabled = false };
            bOpen = new Chip("Ouvrir le dossier") { AccentOf = t => t.Muted, Enabled = false };
            bGo.Click += (s, e) => Go();
            bCancel.Click += (s, e) => { if (engine != null) engine.Cancel = true; lStep.Text = "annulation après l'étape en cours…"; };
            bOpen.Click += (s, e) => { if (lastFolder != null) Process.Start("explorer.exe", "\"" + lastFolder + "\""); };
            Body.Controls.AddRange(new Control[] { bGo, bCancel, bOpen });

            bar = new NeonBar { Location = new Point(k(16), k(384)), Width = k(828) };
            lStep = new Label { Text = "prêt", AutoSize = true, Location = new Point(k(18), k(398)), Tag = "accent", Font = new Font("Consolas", 9f) };
            logBox = new LogBox { Location = new Point(k(16), k(424)), Size = new Size(k(828), Body.Height - k(436)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Body.Controls.AddRange(new Control[] { bar, lStep, logBox });
            Body.Resize += (s, e) => { bar.Width = Body.Width - k(32); itemsCard.Width = Body.Width - k(32); };

            LayoutButtons();
            logBox.Line("ProfilSave " + AppInfo.Version + " · Développé by alex.b", "accent", true);
            logBox.Line("1. Choisis le profil et la destination (dossier du NAS, clé USB, disque externe).", "muted");
            logBox.Line("2. Coche les éléments, puis LANCER. Un dossier PC_utilisateur_date est créé.", "muted");
            logBox.Line("3. Sur le nouveau PC : RESTAURER, choisis ce dossier et le profil cible.", "muted");
            FormClosing += (s, e) => { if (busy && !Ask(this, "Une opération est en cours. Quitter quand même ?")) e.Cancel = true; };
        }

        void LayoutButtons()
        {
            bCancel.Location = new Point(bGo.Right + Theme.S(8), bGo.Top);
            bOpen.Location = new Point(bCancel.Right + Theme.S(8), bGo.Top);
        }

        void SetMode(bool restore)
        {
            if (busy) return;
            restoreMode = restore;
            mBackup.Active = !restore; mRestore.Active = restore;
            mBackup.Invalidate(); mRestore.Invalidate();
            lUser.Text = restore ? "Profil cible" : "Profil à sauvegarder";
            lPath.Text = restore ? "Dossier de sauvegarde" : "Destination (NAS, clé…)";
            path.Text = restore ? "" : Prefs.Ini.Get("LastDest", "");
            itemsCard.Enabled = !restore;
            foreach (var it in Items.All) it.Box.Enabled = !restore;
            lInfo.Text = restore ? "La restauration reprend les éléments présents dans la sauvegarde (manifest.ini). Mot de passe demandé si elle est chiffrée." : "";
            cEncrypt.Visible = !restore;
            bGo.SetText(restore ? "▶  LANCER LA RESTAURATION" : "▶  LANCER LA SAUVEGARDE");
            bGo.AccentOf = restore ? (Func<Theme, Color>)(t => t.Accent2) : (t => t.Ok);
            LayoutButtons();
        }

        void Browse()
        {
            using (var d = new FolderBrowserDialog { Description = restoreMode ? "Dossier de sauvegarde (contient manifest.ini)" : "Où créer la sauvegarde ?", ShowNewFolderButton = !restoreMode })
            {
                if (Directory.Exists(path.Text)) d.SelectedPath = path.Text;
                if (d.ShowDialog(this) == DialogResult.OK) path.Text = d.SelectedPath;
            }
        }

        void SetBusy(bool on)
        {
            busy = on;
            foreach (Control c in new Control[] { mBackup, mRestore, users, path, bBrowse, bGo, cEncrypt }) c.Enabled = !on && (c != cEncrypt || Engine.SevenZip != null);
            if (!restoreMode) foreach (var it in Items.All) it.Box.Enabled = !on;
            bCancel.Enabled = on;
            UseWaitCursor = on;
        }

        void Go()
        {
            var u = users.SelectedItem as UserProfile;
            if (u == null) { Warn(this, "Choisis un profil."); return; }
            string p = path.Text.Trim();
            if (p.Length == 0 || !Directory.Exists(p)) { Warn(this, "Dossier introuvable : " + p); return; }

            if (restoreMode)
            {
                if (!File.Exists(Path.Combine(p, "manifest.ini"))) { Warn(this, "Ce dossier n'est pas une sauvegarde ProfilSave (manifest.ini absent)."); return; }
                if (!Ask(this, "Restaurer cette sauvegarde dans le profil « " + u.Name + " » ?\n\nLes fichiers sont ajoutés ; un fichier de même nom est remplacé s'il est différent. Rien n'est supprimé.")) return;
            }
            else
            {
                var sel = Items.All.Where(i => i.Box.Checked).ToList();
                if (sel.Count == 0) { Warn(this, "Coche au moins un élément."); return; }
                if (sel.Any(i => i.Sensitive) && !Ask(this, "Les profils Wi-Fi seront exportés AVEC leurs mots de passe en clair.\nGarde cette sauvegarde en lieu sûr et supprime le dossier WiFi après usage. Continuer ?")) return;
                if (p.StartsWith(u.Path, StringComparison.OrdinalIgnoreCase)) { Warn(this, "La destination est dans le profil à sauvegarder : choisis un autre emplacement."); return; }
                Prefs.Ini.Set("LastDest", p);
                foreach (var it in Items.All) Prefs.Ini.Set("Item." + it.Key, it.Box.Checked);
                Prefs.Save();
            }

            string password = null;
            if (restoreMode)
            {
                var man = new Ini(Path.Combine(p, "manifest.ini"));
                if (man.Get("Encrypted", "0") == "1")
                {
                    if (Engine.SevenZip == null) { Warn(this, "Cette sauvegarde est chiffrée : installe 7-Zip sur ce PC (winget install 7zip.7zip) puis relance."); return; }
                    password = PasswordDialog.Ask(this, "Mot de passe de la sauvegarde", false);
                    if (password == null) return;
                }
            }
            else if (cEncrypt.Checked)
            {
                password = PasswordDialog.Ask(this, "Mot de passe de chiffrement", true);
                if (password == null) return;
            }
            Prefs.Ini.Set("Encrypt", cEncrypt.Checked); Prefs.Save();

            SetBusy(true);
            logBox.Clear();
            engine = new Engine(logBox, (v, step) => BeginInvoke(new Action(() => { bar.Value = v; lStep.Text = step; })));
            var items = Items.All.Where(i => i.Box.Checked).ToList();
            bool restore = restoreMode;
            Task.Run(() =>
            {
                string folder = null;
                try { if (restore) { if (password != null) engine.RestoreEncrypted(p, u, password); else engine.Restore(p, u); folder = p; } else folder = password != null ? engine.BackupEncrypted(u, p, items, password) : engine.Backup(u, p, items); }
                catch (Exception ex) { logBox.Line("ERREUR : " + ex.Message, "bad", true); Log.Write("ERREUR " + ex); }
                BeginInvoke(new Action(() =>
                {
                    SetBusy(false);
                    lastFolder = folder; bOpen.Enabled = folder != null;
                    lStep.Text = "terminé — rapport : rapport-profilsave.txt";
                }));
            });
        }
    }

    // ===================================================================== Saisie du mot de passe (jamais enregistré)
    class PasswordDialog : Form
    {
        readonly TextBox p1, p2;
        string result;

        PasswordDialog(string title, bool confirm)
        {
            var t = Theme.Current; Func<float, int> k = Theme.S;
            Text = title + " · by alex.b"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; BackColor = t.Bg; Font = new Font("Segoe UI", 9.5f); KeyPreview = true;
            int y = k(16);
            Controls.Add(new Label { Text = "> " + title.ToUpperInvariant(), Tag = "accent", AutoSize = true, Font = new Font("Consolas", 11f, FontStyle.Bold), Location = new Point(k(16), y) }); y += k(34);
            Controls.Add(new Label { Text = confirm ? "12 caractères minimum. Il n'est enregistré nulle part : sans lui, la sauvegarde est irrécupérable." : "Mot de passe choisi lors de la sauvegarde.", Tag = "muted", AutoSize = true, MaximumSize = new Size(k(420), 0), Location = new Point(k(16), y) }); y += k(40);
            p1 = new TextBox { UseSystemPasswordChar = true, Location = new Point(k(16), y), Width = k(420), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10f) }; Controls.Add(p1); y += k(34);
            if (confirm) { Controls.Add(new Label { Text = "Confirmer", AutoSize = true, Location = new Point(k(16), y) }); y += k(22); p2 = new TextBox { UseSystemPasswordChar = true, Location = new Point(k(16), y), Width = k(420), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10f) }; Controls.Add(p2); y += k(38); }
            var ok = new Chip("Valider") { Primary = true, AccentOf = x => x.Ok };
            var cancel = new Chip("Annuler") { AccentOf = x => x.Muted };
            cancel.Location = new Point(k(436) - cancel.Width, y); ok.Location = new Point(cancel.Left - k(8) - ok.Width, y);
            ok.Click += (s, e) => Accept(confirm); cancel.Click += (s, e) => Close();
            Controls.Add(ok); Controls.Add(cancel);
            ClientSize = new Size(k(452), y + k(46));
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Accept(confirm); } };
            CyberForm.Recolor(this);
            Shown += (s, e) => p1.Focus();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Dwm.Style(this); }

        void Accept(bool confirm)
        {
            var a = p1.Text;
            if (a.Contains("\"")) { CyberForm.Warn(this, "Le caractère \" n'est pas autorisé."); return; }
            if (confirm)
            {
                if (a.Length < 12) { CyberForm.Warn(this, "12 caractères minimum."); return; }
                if (a != p2.Text) { CyberForm.Warn(this, "Les deux mots de passe sont différents."); return; }
            }
            else if (a.Length == 0) return;
            result = a; Close();
        }

        public static string Ask(IWin32Window owner, string title, bool confirm)
        {
            using (var d = new PasswordDialog(title, confirm)) { d.ShowDialog(owner); return d.result; }
        }
    }
}
