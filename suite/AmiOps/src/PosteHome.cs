// PC Santé AB - accueil de l'édition poste : état du PC en un coup d'œil et accès aux contrôles.
// Compilé uniquement avec le symbole POSTE.
// Développé by alex.b
#if POSTE
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

namespace AmiOps
{
    // dossier des rapports et version de Windows (équivalent réduit de Report, sans fiche client)
    static class Report
    {
        public static string Dir
        {
            get
            {
                var d = Prefs.Ini.Get("ReportDir", "");
                if (d.Length == 0 || !Directory.Exists(d)) d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Ed.Name);
                Directory.CreateDirectory(d); return d;
            }
        }
        public static string WinVersion()
        {
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build; int.TryParse((k.GetValue("CurrentBuildNumber") ?? "0").ToString(), out build);
                    var name = (k.GetValue("ProductName") ?? "Windows").ToString(); if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");
                    return name + " " + (k.GetValue("DisplayVersion") ?? "") + " (build " + build + ")";
                }
            }
            catch { return Environment.OSVersion.VersionString; }
        }
    }

    class PosteHome : Page
    {
        readonly Label info;
        public override string Title { get { return "Accueil"; } }
        public PosteHome()
        {
            Lbl(Ed.Name.ToUpperInvariant(), 0, k(2), "accent", 15f, true);
            Lbl("Santé, dépannage et optimisation de CE PC. Aucun accès aux autres machines ni aux données d'autres personnes.", 0, k(36), "muted");
            info = Lbl("", 0, k(64), "text");
            string[][] tiles =
            {
                new[] { "optim", "Bilan du PC (note /10)", "mises à jour, pilotes, démarrage, disque, sécurité, vitesse" },
                new[] { "panne", "PanneScan", "écrans bleus, arrêts brutaux, disque, mémoire, pilotes" },
                new[] { "depann", "Dépannage", "réparations Windows, imprimante, réseau, Outlook" },
                new[] { "secu", "Sécurité du PC", "note /100 et corrections" },
                new[] { "netdiag", "Diagnostic réseau", "Wi-Fi, box, Internet, débit" },
                new[] { "reseau", "Appareils du réseau", "liste des appareils connectés (sans test de ports)" },
                new[] { "jeu", "Jeu : optimiser le PC", "pilotes, écran, applis ouvertes, overclocking" },
                new[] { "outils", "Outils", "raccourcis Windows et utilitaires" },
                new[] { "scen", "Scénarios", "PC lent, écran bleu, réseau, prêt à jouer" },
            };
            int y = k(104), x = 0;
            foreach (var t in tiles)
            {
                var key = t[0];
                var b = new Chip(t[1] + "\n" + t[2]) { AccentOf = th => th.Accent, Location = new Point(x, y), Size = new Size(k(330), k(64)) };
                b.Click += (s, e) => { if (key == "scen") using (var sf = new ScenarioForm(Shell)) sf.ShowDialog(this); else Shell.Go(key, null); }; Controls.Add(b);
                x += k(342); if (x + k(330) > k(1040)) { x = 0; y += k(74); }
            }
        }
        public override void OnShow()
        {
            long free = -1, total = -1; try { var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)); free = d.AvailableFreeSpace; total = d.TotalSize; } catch { }
            info.Text = Environment.MachineName + " · " + Report.WinVersion() + (total > 0 ? " · disque système : " + (free * 100 / total) + " % libre (" + Fmt.Size(free) + ")" : "") + " · démarré depuis " + TimeSpan.FromMilliseconds(Environment.TickCount & int.MaxValue).ToString(@"d\j\ hh\h");
        }
    }
}
#endif
