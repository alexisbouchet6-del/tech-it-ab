// TriFichiers - doublons, rangement par type / année, quarantaine et annulation
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlexB;

[assembly: AssemblyTitle("TriFichiers")]
[assembly: AssemblyDescription("Doublons et rangement de fichiers")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("TriFichiers")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace TriFichiers
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            AppInfo.Name = "TriFichiers"; AppInfo.Id = "TriFichiers"; AppInfo.Version = "1.0.0"; AppInfo.MachineWide = false;
            Boot.Run(() => new MainForm(), false, null);
        }
    }

    class Move { public string From, To; }

    // ===================================================================== Moteur
    static class Engine
    {
        public const string Quarantine = "_TriFichiers_Quarantaine";
        static readonly string[] Never = { ".pst", ".ost", ".nst" };                 // fichiers Outlook : jamais déplacés
        static readonly string[] Junk = { "desktop.ini", "thumbs.db", ".ds_store" };

        public static readonly Dictionary<string, string> Cat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static Engine()
        {
            Action<string, string> m = (c, exts) => { foreach (var e in exts.Split(' ')) Cat["." + e] = c; };
            m("Images", "jpg jpeg png gif bmp tif tiff heic heif webp raw cr2 cr3 nef arw dng orf rw2 svg ico");
            m("Vidéos", "mp4 mov avi mkv wmv m4v 3gp mts m2ts webm flv mpg mpeg");
            m("Musique", "mp3 wav flac aac m4a ogg wma opus");
            m("Documents", "doc docx docm odt rtf txt md pages");
            m("PDF", "pdf");
            m("Tableurs", "xls xlsx xlsm xlsb ods csv numbers");
            m("Présentations", "ppt pptx pptm odp key");
            m("Archives", "zip rar 7z tar gz tgz bz2 xz iso cab");
            m("Programmes", "exe msi msix appx bat cmd ps1 vbs reg");
            m("Mails", "eml msg vcf ics");
            m("Plans et dessins", "dwg dxf skp psd ai indd eps xcf");
        }

        // Dossiers interdits : système, programmes, profil technique
        public static string Forbidden(string dir)
        {
            string full; try { full = Path.GetFullPath(dir).TrimEnd('\\') + "\\"; } catch { return "chemin invalide"; }
            if (!Directory.Exists(full)) return "dossier introuvable";
            if (Path.GetPathRoot(full).Equals(full, StringComparison.OrdinalIgnoreCase)) return "racine d'un lecteur : choisis un sous-dossier (ex. Documents, Téléchargements)";
            var bad = new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
            foreach (var b in bad.Where(x => !string.IsNullOrEmpty(x)))
            {
                var bb = b.TrimEnd('\\') + "\\";
                if (full.StartsWith(bb, StringComparison.OrdinalIgnoreCase)) return "dossier système / programmes interdit (" + b + ")";
            }
            var prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\') + "\\";
            if (full.Equals(prof, StringComparison.OrdinalIgnoreCase)) return "racine du profil interdite : choisis Documents, Bureau, Images, Téléchargements…";
            return null;
        }

        static bool Skip(FileInfo f)
        {
            if ((f.Attributes & (FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) return true;
            if (f.Name.StartsWith("~$") || f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return true;
            if (Never.Contains(f.Extension.ToLowerInvariant()) || Junk.Contains(f.Name.ToLowerInvariant())) return true;
            return false;
        }

        // Parcours sans suivre les liens / jonctions, en ignorant la quarantaine et les dossiers inaccessibles
        public static List<FileInfo> Files(string root, bool recursive, CancellationToken ct)
        {
            var res = new List<FileInfo>(); var stack = new Stack<DirectoryInfo>(); stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0 && !ct.IsCancellationRequested)
            {
                var d = stack.Pop();
                try { foreach (var f in d.GetFiles()) if (!Skip(f)) res.Add(f); } catch { }
                if (!recursive) continue;
                try
                {
                    foreach (var s in d.GetDirectories())
                        if ((s.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0 && !s.Name.Equals(Quarantine, StringComparison.OrdinalIgnoreCase)) stack.Push(s);
                }
                catch { }
            }
            return res;
        }

        static string Hash(string path, long max)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
            {
                if (max <= 0 || fs.Length <= max) return BitConverter.ToString(sha.ComputeHash(fs));
                var buf = new byte[max]; int got = 0, n; while (got < max && (n = fs.Read(buf, got, (int)max - got)) > 0) got += n;
                return BitConverter.ToString(sha.ComputeHash(buf, 0, got));
            }
        }

        // Doublons = même taille, même empreinte du début, puis même SHA-256 complet
        public static List<List<FileInfo>> Duplicates(List<FileInfo> files, long minSize, Action<string, double> progress, CancellationToken ct)
        {
            var bySize = files.Where(f => f.Length >= minSize).GroupBy(f => f.Length).Where(g => g.Count() > 1).ToList();
            int total = bySize.Sum(g => g.Count()), done = 0;
            var result = new List<List<FileInfo>>();
            foreach (var g in bySize)
            {
                if (ct.IsCancellationRequested) break;
                var partial = new Dictionary<string, List<FileInfo>>();
                foreach (var f in g)
                {
                    try { var h = Hash(f.FullName, 65536); List<FileInfo> l; if (!partial.TryGetValue(h, out l)) partial[h] = l = new List<FileInfo>(); l.Add(f); } catch { }
                    progress(f.Name, (double)++done / Math.Max(1, total));
                }
                foreach (var p in partial.Values.Where(v => v.Count > 1))
                {
                    if (p[0].Length <= 65536) { result.Add(p); continue; }
                    var full = new Dictionary<string, List<FileInfo>>();
                    foreach (var f in p)
                    {
                        if (ct.IsCancellationRequested) break;
                        try { var h = Hash(f.FullName, 0); List<FileInfo> l; if (!full.TryGetValue(h, out l)) full[h] = l = new List<FileInfo>(); l.Add(f); } catch { }
                        progress("SHA-256 " + f.Name, (double)done / Math.Max(1, total));
                    }
                    result.AddRange(full.Values.Where(v => v.Count > 1));
                }
            }
            // l'original conservé = le plus ancien (date de modification), puis le chemin le plus court
            foreach (var r in result) r.Sort((a, b) => { int c = a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); return c != 0 ? c : a.FullName.Length.CompareTo(b.FullName.Length); });
            return result.OrderByDescending(r => r[0].Length * (r.Count - 1)).ToList();
        }

        // Date de prise de vue EXIF (JPEG / TIFF), sinon date de modification
        public static DateTime TakenOrModified(FileInfo f)
        {
            var e = f.Extension.ToLowerInvariant();
            if (e == ".jpg" || e == ".jpeg" || e == ".tif" || e == ".tiff")
                try
                {
                    using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var img = Image.FromStream(fs, false, false))
                        foreach (var id in new[] { 0x9003, 0x0132 })
                            if (img.PropertyIdList.Contains(id))
                            {
                                var s = Encoding.ASCII.GetString(img.GetPropertyItem(id).Value).Trim('\0', ' ');
                                DateTime d;
                                if (DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) && d.Year > 1990 && d <= DateTime.Now.AddDays(1)) return d;
                            }
                }
                catch { }
            return f.LastWriteTime;
        }

        public static string Category(FileInfo f) { string c; return Cat.TryGetValue(f.Extension, out c) ? c : "Autres"; }

        static readonly string[] Months = { "01 janvier", "02 février", "03 mars", "04 avril", "05 mai", "06 juin", "07 juillet", "08 août", "09 septembre", "10 octobre", "11 novembre", "12 décembre" };

        public static string SortTarget(FileInfo f, string dest, bool byType, bool byYear, bool photoMonths)
        {
            var parts = new List<string> { dest };
            var cat = Category(f);
            if (byType) parts.Add(cat);
            if (byYear)
            {
                var d = TakenOrModified(f);
                parts.Add(d.Year.ToString());
                if (photoMonths && (cat == "Images" || cat == "Vidéos")) parts.Add(Months[d.Month - 1]);
            }
            return Path.Combine(Path.Combine(parts.ToArray()), f.Name);
        }

        public static string Free(string target, HashSet<string> reserved)
        {
            string dir = Path.GetDirectoryName(target), name = Path.GetFileNameWithoutExtension(target), ext = Path.GetExtension(target), t = target;
            for (int i = 2; File.Exists(t) || Directory.Exists(t) || reserved.Contains(t.ToLowerInvariant()); i++) t = Path.Combine(dir, name + " (" + i + ")" + ext);
            reserved.Add(t.ToLowerInvariant());
            return t;
        }

        public static string JournalDir { get { var d = Path.Combine(AppInfo.DataDir, "journaux"); Directory.CreateDirectory(d); return d; } }

        // Exécute les déplacements en écrivant le journal au fil de l'eau (annulation possible même après un plantage)
        public static int Apply(List<Move> moves, string label, Action<int, int, string> progress, out int failed, out string journal)
        {
            failed = 0; int ok = 0;
            journal = Path.Combine(JournalDir, DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + "_" + label + ".csv");
            using (var w = new StreamWriter(journal, false, new UTF8Encoding(true)))
            {
                w.WriteLine("# TriFichiers (by alex.b) - " + label + " - " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " - origine|destination");
                for (int i = 0; i < moves.Count; i++)
                {
                    var m = moves[i];
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(m.To));
                        File.Move(m.From, m.To);
                        w.WriteLine(m.From + "|" + m.To); w.Flush(); ok++;
                    }
                    catch (Exception ex) { failed++; Log.Write("Échec " + m.From + " : " + ex.Message); }
                    progress(i + 1, moves.Count, Path.GetFileName(m.From));
                }
            }
            Log.Write(label + " : " + ok + " déplacé(s), " + failed + " échec(s), journal " + journal);
            return ok;
        }

        public static string LastJournal()
        {
            return Directory.GetFiles(JournalDir, "*.csv").Where(f => !f.EndsWith(".annule.csv")).OrderByDescending(f => f).FirstOrDefault();
        }

        public static int Undo(string journal, out int failed)
        {
            failed = 0; int ok = 0;
            var lines = File.ReadAllLines(journal, Encoding.UTF8).Where(l => l.Length > 0 && !l.StartsWith("#")).Reverse().ToList();
            var reserved = new HashSet<string>();
            foreach (var l in lines)
            {
                var p = l.Split('|'); if (p.Length != 2) continue;
                try
                {
                    if (!File.Exists(p[1])) { failed++; continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(p[0]));
                    File.Move(p[1], File.Exists(p[0]) ? Free(p[0], reserved) : p[0]); ok++;
                    // nettoie les dossiers créés par le tri s'ils sont vides
                    var d = Path.GetDirectoryName(p[1]);
                    for (int i = 0; i < 4 && d != null && Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any(); i++) { Directory.Delete(d); d = Path.GetDirectoryName(d); }
                }
                catch (Exception ex) { failed++; Log.Write("Annulation " + p[1] + " : " + ex.Message); }
            }
            File.Move(journal, journal.Substring(0, journal.Length - 4) + ".annule.csv");
            Log.Write("Annulation " + journal + " : " + ok + " restauré(s), " + failed + " échec(s)");
            return ok;
        }
    }

    // ===================================================================== Fenêtre
    class MainForm : CyberForm
    {
        readonly TextBox tDir, tDest;
        readonly Chip tabDup, tabSort, bBrowse, bScan, bApply, bUndo, bDest, bJournal;
        readonly CheckBox cRec, cType, cYear, cMonths;
        readonly NumericUpDown nMin;
        readonly Panel pDup, pSort;
        readonly ListView lv;
        readonly NeonBar bar;
        readonly Label lStat;
        bool dupMode = true, busy;
        CancellationTokenSource cts;
        List<Move> planned = new List<Move>();

        public MainForm() : base("TRI_FICHIERS", 1120, 760)
        {
            SetSubtitle("Doublons + rangement  //  aperçu avant action, quarantaine, annulation  //  by alex.b");
            Func<float, int> k = Theme.S;
            tabDup = new Chip("DOUBLONS") { Active = true, AccentOf = t => t.Accent, Location = new Point(k(16), k(8)) };
            tabSort = new Chip("RANGER PAR TYPE / ANNÉE") { AccentOf = t => t.Accent2, Location = new Point(tabDup.Right + k(8), k(8)) };
            var ld = new Label { Text = "Dossier :", AutoSize = true, Location = new Point(k(16), k(52)), Tag = "muted" };
            tDir = new TextBox { Text = Prefs.Ini.Get("Dir", ""), Location = new Point(k(84), k(48)), Width = k(600), Font = new Font("Consolas", 9.5f) };
            bBrowse = new Chip("Parcourir…") { AccentOf = t => t.Muted, Location = new Point(k(694), k(44)) };
            cRec = new CheckBox { Text = "Sous-dossiers inclus", Checked = true, AutoSize = true, Location = new Point(bBrowse.Right + k(12), k(50)) };

            pDup = new Panel { Location = new Point(k(16), k(80)), Size = new Size(Body.Width - k(32), k(34)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            pDup.Controls.Add(new Label { Text = "Taille mini (Ko) :", AutoSize = true, Location = new Point(0, k(8)), Tag = "muted" });
            nMin = new NumericUpDown { Minimum = 0, Maximum = 10000000, Value = Prefs.Ini.GetInt("MinKo", 1), Location = new Point(k(120), k(4)), Width = k(90) };
            pDup.Controls.Add(nMin);
            pDup.Controls.Add(new Label { Text = "Conservé = le plus ancien. Les copies cochées partent en quarantaine (" + Engine.Quarantine + "), jamais supprimées.", AutoSize = true, Location = new Point(k(224), k(8)), Tag = "muted" });

            pSort = new Panel { Location = pDup.Location, Size = pDup.Size, Anchor = pDup.Anchor, Visible = false };
            cType = new CheckBox { Text = "Par type", Checked = true, AutoSize = true, Location = new Point(0, k(6)) };
            cYear = new CheckBox { Text = "Par année", Checked = true, AutoSize = true, Location = new Point(k(90), k(6)) };
            cMonths = new CheckBox { Text = "Photos / vidéos par mois", Checked = true, AutoSize = true, Location = new Point(k(186), k(6)) };
            pSort.Controls.Add(new Label { Text = "Destination :", AutoSize = true, Location = new Point(k(370), k(8)), Tag = "muted" });
            tDest = new TextBox { Location = new Point(k(456), k(4)), Width = k(380), Font = new Font("Consolas", 9.5f) };
            bDest = new Chip("…") { AccentOf = t => t.Muted, Location = new Point(k(844), k(0)) };
            pSort.Controls.AddRange(new Control[] { cType, cYear, cMonths, tDest, bDest });

            bScan = new Chip("▶  ANALYSER (aperçu)") { Primary = true, AccentOf = t => t.Ok, Location = new Point(k(16), k(120)) };
            bApply = new Chip("Appliquer la sélection") { AccentOf = t => t.Warn, Location = new Point(bScan.Right + k(8), k(120)), Enabled = false };
            bUndo = new Chip("↶ Annuler la dernière opération") { AccentOf = t => t.Accent2, Location = new Point(bApply.Right + k(8), k(120)) };
            bJournal = new Chip("Journaux") { AccentOf = t => t.Muted, Location = new Point(bUndo.Right + k(8), k(120)) };
            bar = new NeonBar { Location = new Point(k(16), k(162)), Size = new Size(Body.Width - k(32), k(6)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            lStat = new Label { Text = "Choisis un dossier (Documents, Téléchargements, partage NAS…) puis « Analyser ». Rien n'est modifié avant « Appliquer ».", AutoSize = true, Location = new Point(k(16), k(174)), Tag = "muted" };
            lv = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9f),
                Location = new Point(k(16), k(198)), Size = new Size(Body.Width - k(32), Body.Height - k(210)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            lv.Columns.Add("Fichier", k(300)); lv.Columns.Add("Taille", k(80)); lv.Columns.Add("Modifié", k(120)); lv.Columns.Add("Dossier / destination", k(520));
            var cm = new ContextMenuStrip();
            cm.Items.Add("Ouvrir le fichier", null, (s, e) => { var p = SelPath(); if (p != null) try { Process.Start(p); } catch { } });
            cm.Items.Add("Afficher dans l'Explorateur", null, (s, e) => { var p = SelPath(); if (p != null) Process.Start("explorer.exe", "/select,\"" + p + "\""); });
            cm.Items.Add("Tout cocher", null, (s, e) => { foreach (ListViewItem i in lv.Items) if (i.Tag != null) i.Checked = true; });
            cm.Items.Add("Tout décocher", null, (s, e) => { foreach (ListViewItem i in lv.Items) i.Checked = false; });
            lv.ContextMenuStrip = cm;
            lv.ItemChecked += (s, e) => { if (e.Item.Tag is string && e.Item.Checked) { e.Item.Checked = false; return; } if (!busy) UpdateStat(); };   // l'original n'est jamais déplacé

            Body.Controls.AddRange(new Control[] { tabDup, tabSort, ld, tDir, bBrowse, cRec, pDup, pSort, bScan, bApply, bUndo, bJournal, bar, lStat, lv });
            tabDup.Click += (s, e) => SetMode(true);
            tabSort.Click += (s, e) => SetMode(false);
            bBrowse.Click += (s, e) => { var p = Pick(tDir.Text); if (p != null) { tDir.Text = p; if (tDest.Text.Length == 0) tDest.Text = p; } };
            bDest.Click += (s, e) => { var p = Pick(tDest.Text); if (p != null) tDest.Text = p; };
            bScan.Click += (s, e) => Scan();
            bApply.Click += (s, e) => ApplySel();
            bUndo.Click += (s, e) => UndoLast();
            bJournal.Click += (s, e) => Process.Start("explorer.exe", "\"" + Engine.JournalDir + "\"");
            tDest.Text = Prefs.Ini.Get("Dest", tDir.Text);
        }

        string SelPath()
        {
            if (lv.SelectedItems.Count == 0) return null;
            var m = lv.SelectedItems[0].Tag as Move; if (m != null) return m.From;
            return lv.SelectedItems[0].Tag as string;
        }

        string Pick(string start)
        {
            using (var d = new FolderBrowserDialog { Description = "TriFichiers · by alex.b", SelectedPath = Directory.Exists(start) ? start : "" })
                return d.ShowDialog(this) == DialogResult.OK ? d.SelectedPath : null;
        }

        void SetMode(bool dup)
        {
            if (busy) return;
            dupMode = dup; tabDup.Active = dup; tabSort.Active = !dup; tabDup.Invalidate(); tabSort.Invalidate();
            pDup.Visible = dup; pSort.Visible = !dup;
            cRec.Checked = dup;   // ranger : par défaut uniquement le dossier lui-même (préserve l'arborescence existante)
            lv.Items.Clear(); lv.Groups.Clear(); planned.Clear(); bApply.Enabled = false;
            bApply.SetText(dup ? "Mettre en quarantaine la sélection" : "Ranger la sélection");
            lStat.Text = dup ? "Doublons : contenu identique (SHA-256), quel que soit le nom." : "Ranger : aperçu des déplacements, puis « Ranger la sélection ». Les noms en conflit reçoivent « (2) ».";
        }

        void SetBusy(bool on)
        {
            busy = on; UseWaitCursor = on;
            foreach (var c in new Control[] { tabDup, tabSort, bBrowse, bApply, bUndo, tDir, pDup, pSort, cRec }) c.Enabled = !on;
            bScan.SetText(on ? "■  ARRÊTER" : "▶  ANALYSER (aperçu)");
            if (!on) bApply.Enabled = planned.Count > 0;
        }

        void UpdateStat()
        {
            var sel = lv.CheckedItems.Cast<ListViewItem>().Select(i => i.Tag as Move).Where(m => m != null).ToList();
            long size = 0; foreach (var m in sel) try { size += new FileInfo(m.From).Length; } catch { }
            lStat.Text = sel.Count + " fichier(s) sélectionné(s) — " + Fmt.Size(size) + (dupMode ? " récupérables" : " à ranger");
        }

        void Scan()
        {
            if (busy) { if (cts != null) cts.Cancel(); return; }
            var dir = tDir.Text.Trim(); var why = Engine.Forbidden(dir);
            if (why != null) { Warn(this, "Dossier refusé : " + why + "."); return; }
            string dest = null;
            if (!dupMode)
            {
                dest = tDest.Text.Trim(); if (dest.Length == 0) dest = dir;
                var w2 = Engine.Forbidden(dest); if (w2 != null) { Warn(this, "Destination refusée : " + w2 + "."); return; }
                if (!cType.Checked && !cYear.Checked) { Warn(this, "Coche « par type » et / ou « par année »."); return; }
                if (cRec.Checked && !Ask(this, "Sous-dossiers inclus : TOUS les fichiers des sous-dossiers seront regroupés par type / année, l'arborescence actuelle sera perdue (annulable).\n\nContinuer ?")) return;
            }
            Prefs.Ini.Set("Dir", dir); Prefs.Ini.Set("MinKo", ((int)nMin.Value).ToString()); if (dest != null) Prefs.Ini.Set("Dest", dest); Prefs.Save();
            cts = new CancellationTokenSource(); var ct = cts.Token;
            lv.Items.Clear(); lv.Groups.Clear(); planned.Clear(); bar.Value = 0;
            SetBusy(true);
            bool rec = cRec.Checked, byType = cType.Checked, byYear = cYear.Checked, months = cMonths.Checked; long min = (long)nMin.Value * 1024;
            bool dup = dupMode;
            Task.Run(() =>
            {
                var items = new List<ListViewItem>(); var groups = new List<ListViewGroup>(); string summary = "";
                try
                {
                    Ui(() => lStat.Text = "Inventaire des fichiers…");
                    var files = Engine.Files(dir, rec, ct);
                    if (dup)
                    {
                        long lastUi = 0; var sw = Stopwatch.StartNew();
                        var sets = Engine.Duplicates(files, min, (name, p) => { if (sw.ElapsedMilliseconds - lastUi > 150) { lastUi = sw.ElapsedMilliseconds; Ui(() => { bar.Value = p; lStat.Text = "Comparaison… " + name; }); } }, ct);
                        long gain = 0; int n = 0;
                        var qroot = Path.Combine(dir, Engine.Quarantine, DateTime.Now.ToString("yyyy-MM-dd_HHmm"));
                        var reserved = new HashSet<string>();
                        foreach (var set in sets)
                        {
                            var g = new ListViewGroup(Fmt.Size(set[0].Length) + " × " + set.Count + " — " + set[0].Name);
                            groups.Add(g);
                            for (int i = 0; i < set.Count; i++)
                            {
                                var f = set[i];
                                var rel = f.FullName.Substring(dir.TrimEnd('\\').Length).TrimStart('\\');
                                var it = new ListViewItem(new[] { (i == 0 ? "✓ " : "  ") + f.Name, Fmt.Size(f.Length), f.LastWriteTime.ToString("dd/MM/yyyy HH:mm"), f.DirectoryName }, g);
                                if (i == 0) { it.Tag = f.FullName; it.ForeColor = Theme.Current.Ok; }
                                else
                                {
                                    var m = new Move { From = f.FullName, To = Engine.Free(Path.Combine(qroot, rel), reserved) };
                                    it.Tag = m; it.Checked = true; planned.Add(m); gain += f.Length; n++;
                                }
                                items.Add(it);
                            }
                        }
                        summary = files.Count + " fichier(s) analysé(s) · " + sets.Count + " groupe(s) de doublons · " + n + " copie(s) · " + Fmt.Size(gain) + " récupérables. Vert = original conservé.";
                    }
                    else
                    {
                        var reserved = new HashSet<string>(); int i = 0;
                        var cats = new Dictionary<string, ListViewGroup>();
                        foreach (var f in files.OrderBy(x => Engine.Category(x)).ThenBy(x => x.Name))
                        {
                            if (ct.IsCancellationRequested) break;
                            var target = Engine.SortTarget(f, dest, byType, byYear, months);
                            if (++i % 50 == 0) { int ii = i; Ui(() => { bar.Value = (double)ii / files.Count; lStat.Text = "Calcul… " + ii + " / " + files.Count; }); }
                            if (target.Equals(f.FullName, StringComparison.OrdinalIgnoreCase)) continue;   // déjà rangé
                            var m = new Move { From = f.FullName, To = Engine.Free(target, reserved) };
                            var c = Engine.Category(f); ListViewGroup g;
                            if (!cats.TryGetValue(c, out g)) { cats[c] = g = new ListViewGroup(c); groups.Add(g); }
                            var it = new ListViewItem(new[] { f.Name, Fmt.Size(f.Length), f.LastWriteTime.ToString("dd/MM/yyyy"), "→ " + m.To.Substring(dest.TrimEnd('\\').Length) }, g) { Tag = m, Checked = true };
                            if (!m.To.EndsWith(f.Name, StringComparison.OrdinalIgnoreCase)) it.ForeColor = Theme.Current.Warn;   // renommé (conflit)
                            items.Add(it); planned.Add(m);
                        }
                        summary = files.Count + " fichier(s) · " + planned.Count + " à déplacer · " + string.Join("  ·  ", cats.Keys.Select(c => c + " " + items.Count(x => x.Group == cats[c])));
                    }
                }
                catch (Exception ex) { summary = "Erreur : " + ex.Message; Log.Write(ex.ToString()); }
                Ui(() =>
                {
                    lv.BeginUpdate(); lv.Groups.AddRange(groups.ToArray()); lv.Items.AddRange(items.ToArray()); lv.EndUpdate();
                    bar.Value = 1; cts = null; SetBusy(false);
                    lStat.Text = (ct.IsCancellationRequested ? "Arrêté — " : "") + (planned.Count == 0 ? (dup ? "Aucun doublon trouvé. " : "Rien à ranger. ") : "") + summary;
                });
            });
        }

        void Ui(Action a) { try { BeginInvoke(a); } catch { } }

        void ApplySel()
        {
            var sel = lv.CheckedItems.Cast<ListViewItem>().Select(i => i.Tag as Move).Where(m => m != null && File.Exists(m.From)).ToList();
            if (sel.Count == 0) { Info(this, "Aucun fichier coché."); return; }
            if (dupMode)
            {
                // sécurité : jamais toutes les copies d'un même groupe
                foreach (ListViewGroup g in lv.Groups)
                {
                    var all = g.Items.Cast<ListViewItem>().ToList();
                    if (all.All(i => i.Checked || i.Tag is string) && !all.Any(i => i.Tag is string)) { Warn(this, "Le groupe « " + g.Header + " » n'aurait plus aucun exemplaire : décoche au moins un fichier."); return; }
                }
            }
            if (!Ask(this, sel.Count + " fichier(s) vont être déplacés" + (dupMode ? " en quarantaine (" + Engine.Quarantine + ")" : "") + ".\nOpération annulable avec « Annuler la dernière opération ».\n\nContinuer ?")) return;
            SetBusy(true); bScan.Enabled = false;
            Task.Run(() =>
            {
                int failed; string journal;
                int ok = Engine.Apply(sel, dupMode ? "doublons" : "rangement", (i, n, name) => { if (i % 20 == 0 || i == n) Ui(() => { bar.Value = (double)i / n; lStat.Text = "Déplacement… " + i + " / " + n + " " + name; }); }, out failed, out journal);
                Ui(() =>
                {
                    planned.Clear(); lv.Items.Clear(); lv.Groups.Clear(); SetBusy(false); bScan.Enabled = true;
                    lStat.Text = ok + " fichier(s) déplacé(s)" + (failed > 0 ? ", " + failed + " échec(s) (fichiers ouverts ? voir le journal)" : "") + ". Journal : " + Path.GetFileName(journal);
                    if (dupMode && ok > 0) Info(this, "Copies déplacées dans :\n" + Path.Combine(tDir.Text.Trim(), Engine.Quarantine) + "\n\nVérifie puis supprime ce dossier toi-même quand tu es sûr.");
                });
            });
        }

        void UndoLast()
        {
            var j = Engine.LastJournal();
            if (j == null) { Info(this, "Aucune opération à annuler."); return; }
            int n = File.ReadAllLines(j, Encoding.UTF8).Count(l => l.Length > 0 && !l.StartsWith("#"));
            if (!Ask(this, "Annuler l'opération du " + File.GetCreationTime(j).ToString("dd/MM/yyyy HH:mm") + " (" + Path.GetFileNameWithoutExtension(j) + ") ?\n" + n + " fichier(s) reviendront à leur emplacement d'origine.")) return;
            SetBusy(true); bScan.Enabled = false;
            Task.Run(() =>
            {
                int failed = 0, ok = 0;
                try { ok = Engine.Undo(j, out failed); } catch (Exception ex) { Log.Write(ex.ToString()); }
                Ui(() => { SetBusy(false); bScan.Enabled = true; lStat.Text = "Annulation : " + ok + " fichier(s) remis en place" + (failed > 0 ? ", " + failed + " introuvable(s) / échec(s)" : "") + "."; });
            });
        }
    }
}
