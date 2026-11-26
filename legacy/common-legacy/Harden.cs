// Développé by alex.b — durcissement commun (NetSwitch, Outils VTx, PowerMenu)
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

// DLL natives chargées uniquement depuis System32 (anti DLL hijacking)
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace AlexBHarden
{
    static class Harden
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetDllDirectory(string path);

        // Retire le dossier courant de l'ordre de recherche des DLL ; verrouille le dossier de données si admin
        public static void Apply(string dataDir)
        {
            try { SetDllDirectory(""); } catch { }
            if (!string.IsNullOrEmpty(dataDir)) LockDir(dataDir);
        }

        static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        // dossier temporaire NEUF, créé directement avec une ACL SYSTEM + Administrateurs (sans fenêtre de course) :
        // pour tout fichier ensuite exécuté ou importé avec les droits admin (%TEMP% est modifiable par les processus non élevés)
        public static string SecureTempDir(string prefix)
        {
            var name = prefix + "-" + Guid.NewGuid().ToString("N");
            if (!IsAdmin()) { var t = Path.Combine(Path.GetTempPath(), name); Directory.CreateDirectory(t); return t; }
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), name);
            var ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            var inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            try { Directory.CreateDirectory(d, ds); }
            catch (Exception ex) when (ex is PlatformNotSupportedException || ex is NotImplementedException) { Directory.CreateDirectory(d); }   // Mono (tests)
            return d;
        }

        // un exe lancé avec les droits admin sans invite (tâche planifiée) doit être dans Program Files :
        // dans Téléchargements ou sur le Bureau, n'importe quel programme de la session pourrait le remplacer
        public static bool InProtectedDir(string exe)
        {
            var full = Path.GetFullPath(exe);
            foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var pf = Environment.GetFolderPath(sf);
                if (pf.Length > 0 && full.StartsWith(pf.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ProgramData\<App> : Admins + SYSTEM contrôle total, Utilisateurs lecture seule
        public static void LockDir(string dir)
        {
            if (!IsAdmin()) return;
            try
            {
                Directory.CreateDirectory(dir);
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var users  = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                var inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                var ds = new DirectorySecurity();
                ds.SetAccessRuleProtection(true, false);
                ds.SetOwner(admins);
                ds.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
                ds.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
                ds.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(dir, ds);
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fs = new FileSecurity();
                        fs.SetAccessRuleProtection(false, false);
                        fs.SetOwner(admins);
                        File.SetAccessControl(f, fs);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
