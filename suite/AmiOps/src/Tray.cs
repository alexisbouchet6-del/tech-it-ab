// Tech IT AB - icône près de l'horloge, démarrage automatique, modules intégrés (NasConnect, NetSwitch, Outils VTx, PowerMenu)
// Développé by alex.b

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using AlexB;

namespace AmiOps
{
    static class TrayHost
    {
        public static NotifyIcon Icon;
        public static bool Quitting;

        public static void Create(MainForm f)
        {
            Icon = new NotifyIcon { Icon = f.Icon ?? SystemIcons.Application, Text = Ed.Name + " — by alex.b", Visible = Prefs.Ini.GetBool("Tray", true) };
            var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 9.5f) };
            menu.Opening += (s, e) => { Build(menu, f); e.Cancel = false; };
            Icon.ContextMenuStrip = menu;
            Icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Show(f); };
            Icon.BalloonTipClicked += (s, e) => { Show(f); f.Go("taches", null); };
            Jobs.Done = j => f.BeginInvoke(new Action(() => { if (!f.Visible || !f.ContainsFocus) f.Notify("Tâche " + j.State.ToLowerInvariant(), j.Title); }));
        }

        public static void Show(MainForm f) { f.Show(); if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal; f.Activate(); }

        static void Build(ContextMenuStrip m, MainForm f)
        {
            m.Items.Clear();
            m.Items.Add("Ouvrir " + Ed.Name, null, (s, e) => Show(f)).Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
#if !POSTE
            var r = Store.Running();
            if (r != null) m.Items.Add("■ Arrêter le chrono (" + Store.ClientName(r.ClientId) + " · " + HomePage.Fmt(r.TotalMinutes) + ")", null, (s, e) => { r.Minutes = r.TotalMinutes; r.RunningSince = DateTime.MinValue; Store.Save(); });
            else m.Items.Add("▶ Nouvelle intervention (chrono)", null, (s, e) => { Show(f); f.NewIntervention(null); });
#endif
            int run = Jobs.Running; m.Items.Add("Tâches" + (run > 0 ? " (" + run + " en cours)" : ""), null, (s, e) => { Show(f); f.Go("taches", null); });
#if !POSTE
            m.Items.Add("Scénarios…", null, (s, e) => { Show(f); using (var sf = new ScenarioForm(f)) sf.ShowDialog(f); });
#endif
            m.Items.Add(new ToolStripSeparator());

            // réseau rapide (moteur NetSwitch, même configuration que l'appli NetSwitch)
            var net = new ToolStripMenuItem("Réseau rapide (DHCP / IP fixe)");
            try
            {
                var cfg = NetSwitch.Config.Load();
                foreach (var a in NetSwitch.NetOps.GetAdapters().Where(a => a.IsUp || a.IsWifi))
                {
                    var aa = a; var sub = new ToolStripMenuItem(a.Name + " — " + a.StateText);
                    sub.DropDownItems.Add("DHCP (automatique)", null, (s, e) => Net(f, "DHCP sur " + aa.Name, () => NetSwitch.NetOps.SetDhcp(aa.Name)));
                    foreach (var p in cfg.Presets)
                    {
                        var ip = cfg.Base + "." + p + "." + cfg.HostOctet; var gw = cfg.Base + "." + p + "." + cfg.GatewayOctet;
                        var dns = new System.Collections.Generic.List<string> { gw }; if (cfg.Dns2.Length > 0) dns.Add(cfg.Dns2);
                        sub.DropDownItems.Add("IP fixe " + ip + "/" + cfg.Prefix + " (passerelle " + gw + ")", null, (s, e) => Net(f, "IP fixe " + ip + " sur " + aa.Name, () => NetSwitch.NetOps.SetStatic(aa.Name, ip, cfg.Prefix, gw, dns)));
                    }
                    net.DropDownItems.Add(sub);
                }
            }
            catch (Exception ex) { net.DropDownItems.Add("indisponible : " + ex.Message).Enabled = false; }
            if (net.DropDownItems.Count == 0) net.DropDownItems.Add("aucune carte active").Enabled = false;
            m.Items.Add(net);

            // VT-x (moteur Outils VTx)
            var vtx = new ToolStripMenuItem("Virtualisation (VMware / Hyper-V)");
            try
            {
                var st = OutilsVTx.VtxStatus.Read();
                vtx.DropDownItems.Add("Mode actuel : " + st.Current + (st.FirmwareVtx ? "" : " — VT-x désactivé dans le BIOS")).Enabled = false;
                vtx.DropDownItems.Add("Mode VMware (désactive Hyper-V / VBS)", null, (s, e) => Vtx(f, true));
                vtx.DropDownItems.Add("Mode Windows (Hyper-V, WSL, sécurité VBS)", null, (s, e) => Vtx(f, false));
            }
            catch (Exception ex) { vtx.DropDownItems.Add("indisponible : " + ex.Message).Enabled = false; }
            m.Items.Add(vtx);
#if !POSTE
            m.Items.Add("NAS (NasConnect)…", null, (s, e) => Modules.Nas());
#endif
            m.Items.Add("Arrêt / redémarrage (PowerMenu)…", null, (s, e) => Modules.Power());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Quitter " + Ed.Name, null, (s, e) =>
            {
                if (Jobs.Running > 0 && !CyberForm.Ask(f, Jobs.Running + " tâche(s) en cours seront interrompues. Quitter ?")) return;
                Quitting = true; Icon.Visible = false; f.Close();
            });
        }

        static void Net(MainForm f, string title, Func<NetSwitch.OpResult> op)
        {
            Jobs.Run(title, "net", j => { var r = op(); j.Log(r.Message, r.Ok ? "ok" : "bad"); f.BeginInvoke(new Action(() => f.Notify(r.Ok ? "Réseau modifié" : "Échec réseau", r.Message))); });
        }

        static void Vtx(MainForm f, bool vmware)
        {
            if (!CyberForm.Ask(f, (vmware ? "Passer en mode VMware" : "Passer en mode Windows (Hyper-V)") + " ?\nUn REDÉMARRAGE sera nécessaire. Si BitLocker est actif, garde ta clé de récupération à portée de main.")) return;
            Jobs.Run(vmware ? "Mode VMware" : "Mode Windows (Hyper-V)", "servicing", j =>
            {
                var msg = vmware ? OutilsVTx.Actions.EnableVMwareMode() : OutilsVTx.Actions.EnableWindowsMode();
                j.Log(msg, "ok"); f.BeginInvoke(new Action(() => f.Notify("Virtualisation", "Redémarre le PC pour appliquer.")));
            });
        }

        // ---------------------------------------------------------------- démarrage automatique (tâche planifiée : élevé, sans invite UAC)
        const string TaskName = Ed.Id;
        public static bool AutoStart
        {
            get { string o; return Proc.Run(Proc.Sys("schtasks.exe"), "/Query /TN " + TaskName, 15000, out o) == 0; }
            set
            {
                string o;
                if (!value) { Proc.Run(Proc.Sys("schtasks.exe"), "/Delete /TN " + TaskName + " /F", 15000, out o); return; }
                var exe = Application.ExecutablePath;
                if (!Hardening.InProtectedDir(exe)) throw new Exception("démarrage automatique réservé à " + Ed.Name + " installé (Program Files) : un exe lancé en admin sans invite depuis un dossier modifiable (Téléchargements, Bureau, clé USB) pourrait être remplacé par n'importe quel programme");
                var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                    "<RegistrationInfo><Author>alex.b</Author><Description>" + Ed.Name + " by alex.b - démarrage dans la zone de notification</Description></RegistrationInfo>" +
                    "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + System.Security.SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName) + "</UserId><Delay>PT20S</Delay></LogonTrigger></Triggers>" +
                    "<Principals><Principal id=\"Author\"><UserId>" + System.Security.SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                    "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>" +
                    "<Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command><Arguments>--tray</Arguments></Exec></Actions></Task>";
                var dir = Hardening.SecureTempDir("AmiOps-tache"); var tmp = Path.Combine(dir, "amiops-task.xml");
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                int rc = Proc.Run(Proc.Sys("schtasks.exe"), "/Create /TN " + TaskName + " /XML \"" + tmp + "\" /F", 20000, out o);
                try { Directory.Delete(dir, true); } catch { }
                if (rc != 0) throw new Exception("schtasks : " + o.Trim());
            }
        }

        // lecteurs réseau visibles à la fois par Tech IT AB (admin) et l'Explorateur (paramètre Microsoft EnableLinkedConnections)
        const string Pol = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        public static bool LinkedConnections
        {
            get { try { using (var k = Registry.LocalMachine.OpenSubKey(Pol)) return k != null && Convert.ToInt32(k.GetValue("EnableLinkedConnections", 0)) == 1; } catch { return false; } }
            set { using (var k = Registry.LocalMachine.CreateSubKey(Pol)) { if (value) k.SetValue("EnableLinkedConnections", 1, RegistryValueKind.DWord); else k.DeleteValue("EnableLinkedConnections", false); } }
        }
    }

    static class Modules
    {
#if !POSTE
        public static void Nas()
        {
            NasConnect.NasPaths.Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NasConnect");
            Directory.CreateDirectory(NasConnect.NasPaths.Dir);
            if (!TrayHost.LinkedConnections && Prefs.Ini.Get("NasLinkedTip", "") != "1")
            {
                CyberForm.Info(null, "Les NAS (DSM, santé, checkup) fonctionnent ici.\n\nPour que les LECTEURS RÉSEAU connectés depuis " + Ed.Name + " apparaissent aussi dans l'Explorateur : Paramètres > « Lecteurs réseau partagés avec l'Explorateur » (redémarrage nécessaire).");
                Prefs.Ini.Set("NasLinkedTip", "1"); Prefs.Save();
            }
            new NasConnect.MainForm(null).Show();
        }
#endif
        public static void Power() { new PowerMenu.MainForm().Show(); }
        public static void Vtx() { new OutilsVTx.MainForm(null).Show(); }
    }
}
