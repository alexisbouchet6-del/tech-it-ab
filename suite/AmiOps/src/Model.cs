// AMI·OPS - données (clients, équipements, interventions), sauvegardes et coffre chiffré
// Développé by alex.b

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using AlexB;

[assembly: AssemblyTitle("AMI·OPS")]
[assembly: AssemblyDescription("Console du technicien - clients, pannes, réseau, NAS, messagerie, secours")]
[assembly: AssemblyCompany("alex.b")]
[assembly: AssemblyProduct("AMI·OPS")]
[assembly: AssemblyCopyright("by alex.b - 2026")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace AmiOps
{
    public class Contact { public string Name = "", Role = "", Phone = "", Email = ""; }

    public class Asset
    {
        public string Id = "", Kind = "PC", Name = "", Ip = "", Mac = "", Model = "", Serial = "", Os = "", QcId = "", Notes = "";
        public DateTime Added;
        [ScriptIgnore] public string Label { get { return Kind + " · " + (Name.Length > 0 ? Name : Ip.Length > 0 ? Ip : Serial); } }
    }

    public class Client
    {
        public string Id = "", Name = "", Address = "", Phone = "", Email = "", Contract = "", Notes = "";
        public DateTime Created;
        public List<Contact> Contacts = new List<Contact>();
        public List<Asset> Assets = new List<Asset>();
        public List<string> Domains = new List<string>();
        public int GlpiEntity;   // id de l'entité GLPI liée (0 = aucune)
    }

    public class Intervention
    {
        public string Id = "", ClientId = "", AssetId = "", Ticket = "", Type = "Dépannage", Lieu = "Sur site", Tech = "",
            Demande = "", Diag = "", Actions = "", Materiel = "", Reco = "", Result = "Résolu", Signer = "", Signature = "";
        public DateTime Date, RunningSince;
        public int Minutes;
        public bool Done;
        public int GlpiTicket;   // ticket GLPI créé depuis cette intervention
        [ScriptIgnore] public bool Running { get { return RunningSince.Year > 2000; } }
        [ScriptIgnore] public int TotalMinutes { get { return Minutes + (Running ? (int)Math.Max(0, (DateTime.Now - RunningSince).TotalMinutes) : 0); } }
        [ScriptIgnore] public TimeSpan Elapsed { get { return TimeSpan.FromMinutes(Minutes) + (Running && DateTime.Now > RunningSince ? DateTime.Now - RunningSince : TimeSpan.Zero); } }
    }

    public class MailResult { public string Domain = "", Verdict = "", Color = "", Detail = "", Report = ""; public DateTime At; }

    public class Data
    {
        public int Version = 1;
        public List<Client> Clients = new List<Client>();
        public List<Intervention> Interventions = new List<Intervention>();
        public List<MailResult> Mail = new List<MailResult>();
        public string LastPanne = "", LastPanneColor = "", LastPanneText = "";
        public DateTime LastPanneAt;
    }

    // ===================================================================== Stockage JSON (écriture atomique + sauvegardes)
    static class Store
    {
        public static Data D = new Data();
        public static event Action Changed;
        static DateTime loadedStamp;

        public static string Dir
        {
            get
            {
                var d = Prefs.Ini.Get("DataDir", "");
                if (d.Length == 0) d = Path.Combine(AppInfo.DataDir, "donnees");
                Directory.CreateDirectory(d);
                return d;
            }
        }
        public static string FilePath { get { return Path.Combine(Dir, "amiops.json"); } }
        public static string BackupDir { get { var d = Path.Combine(Dir, "sauvegardes"); Directory.CreateDirectory(d); return d; } }

        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 }; }

        public static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 12); }

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    D = Json().Deserialize<Data>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new Data();
                    loadedStamp = File.GetLastWriteTimeUtc(FilePath);
                }
                else D = new Data();
            }
            catch (Exception ex)
            {
                // fichier abîmé : on le met de côté, on repart de la dernière sauvegarde
                Log.Write("Lecture des données impossible : " + ex.Message);
                try { File.Copy(FilePath, FilePath + ".illisible-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
                var last = Directory.GetFiles(BackupDir, "amiops_*.json").OrderByDescending(f => f).FirstOrDefault();
                D = new Data();
                if (last != null) try { D = Json().Deserialize<Data>(File.ReadAllText(last, Encoding.UTF8)) ?? new Data(); } catch { }
            }
            Normalize();
        }

        // JavaScriptSerializer relit les dates en UTC : retour à l'heure locale
        static DateTime L(DateTime d) { return d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d; }
        static void Normalize()
        {
            if (D.Clients == null) D.Clients = new List<Client>();
            if (D.Interventions == null) D.Interventions = new List<Intervention>();
            if (D.Mail == null) D.Mail = new List<MailResult>();
            foreach (var c in D.Clients)
            {
                c.Created = L(c.Created);
                if (c.Contacts == null) c.Contacts = new List<Contact>();
                if (c.Assets == null) c.Assets = new List<Asset>();
                if (c.Domains == null) c.Domains = new List<string>();
                foreach (var a in c.Assets) a.Added = L(a.Added);
            }
            foreach (var i in D.Interventions) { i.Date = L(i.Date); i.RunningSince = L(i.RunningSince); }
            foreach (var m in D.Mail) m.At = L(m.At);
            D.LastPanneAt = L(D.LastPanneAt);
        }

        public static void Save()
        {
            try
            {
                var json = Json().Serialize(D);
                // modifié par un autre poste depuis le chargement (dossier partagé) : on garde sa version de côté
                if (File.Exists(FilePath) && loadedStamp != DateTime.MinValue && File.GetLastWriteTimeUtc(FilePath) > loadedStamp.AddSeconds(1))
                {
                    var conflict = Path.Combine(BackupDir, "conflit_" + Environment.MachineName + "_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".json");
                    File.Copy(FilePath, conflict, true);
                    Log.Write("Données modifiées ailleurs : copie de l'autre version → " + conflict);
                }
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
                loadedStamp = File.GetLastWriteTimeUtc(FilePath);
                // une sauvegarde par jour, 60 conservées
                var daily = Path.Combine(BackupDir, "amiops_" + DateTime.Now.ToString("yyyy-MM-dd") + ".json");
                File.Copy(FilePath, daily, true);
                foreach (var old in Directory.GetFiles(BackupDir, "amiops_*.json").OrderByDescending(f => f).Skip(60)) try { File.Delete(old); } catch { }
            }
            catch (Exception ex) { Log.Write("Enregistrement impossible : " + ex); throw; }
            if (Changed != null) Changed();
        }

        public static Client ClientById(string id) { return D.Clients.FirstOrDefault(c => c.Id == id); }
        public static string ClientName(string id) { var c = ClientById(id); return c != null ? c.Name : "(client supprimé)"; }
        public static Intervention Running() { return D.Interventions.FirstOrDefault(i => i.Running); }
        public static DateTime LastVisit(Client c)
        {
            var l = D.Interventions.Where(i => i.ClientId == c.Id).Select(i => i.Date).DefaultIfEmpty(DateTime.MinValue).Max();
            return l;
        }
        public static MailResult MailOf(string domain) { return D.Mail.FirstOrDefault(m => m.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)); }

        public static string Norm(string s)
        {
            // recherche insensible à la casse et aux accents
            var f = (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in f) if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
            return sb.ToString();
        }
        public static bool Match(string query, params string[] fields)
        {
            var hay = Norm(string.Join(" ", fields));
            return Norm(query).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).All(t => hay.Contains(t));
        }
    }

    // ===================================================================== Secrets d'intégration (jetons API) chiffrés DPAPI pour l'utilisateur
    static class Secrets
    {
        public static string Get(string name)
        {
            var v = Prefs.Ini.Get("Sec." + name, ""); if (v.Length == 0) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v), Encoding.UTF8.GetBytes("AmiOps:" + name), DataProtectionScope.CurrentUser)); } catch { return ""; }
        }
        public static void Set(string name, string value)
        {
            Prefs.Ini.Set("Sec." + name, string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes("AmiOps:" + name), DataProtectionScope.CurrentUser)));
            Prefs.Save();
        }
    }

    // ===================================================================== Coffre (AES-256-CBC + HMAC-SHA256, clé PBKDF2-SHA256)
    public class Secret { public string Id = "", ClientId = "", Label = "", User = "", Url = "", Notes = "", Cipher = ""; public DateTime Updated; }
    public class VaultFile { public string Salt = "", Check = ""; public int Iter = 300000; public List<Secret> Items = new List<Secret>(); }

    static class Vault
    {
        static byte[] encKey, macKey;
        static VaultFile vf;
        public static DateTime LastUse;
        public static string FilePath { get { return Path.Combine(Store.Dir, "coffre.json"); } }
        public static bool Exists { get { return File.Exists(FilePath); } }
        public static bool Unlocked { get { return encKey != null; } }
        public static List<Secret> Items { get { return vf != null ? vf.Items : new List<Secret>(); } }

        static void Derive(string pw, byte[] salt, int iter)
        {
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(pw), salt, iter, HashAlgorithmName.SHA256))
            { var k = kdf.GetBytes(64); encKey = k.Take(32).ToArray(); macKey = k.Skip(32).ToArray(); Array.Clear(k, 0, k.Length); }
        }

        public static void Create(string pw)
        {
            var salt = new byte[32]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(salt);
            vf = new VaultFile { Salt = Convert.ToBase64String(salt) };
            Derive(pw, salt, vf.Iter);
            vf.Check = Encrypt("AMIOPS-COFFRE-OK");
            Save();
        }

        public static bool Unlock(string pw)
        {
            try
            {
                var f = Store.Json().Deserialize<VaultFile>(File.ReadAllText(FilePath, Encoding.UTF8));
                Derive(pw, Convert.FromBase64String(f.Salt), f.Iter);
                if (Decrypt(f.Check) != "AMIOPS-COFFRE-OK") { Lock(); return false; }
                vf = f; if (vf.Items == null) vf.Items = new List<Secret>();
                foreach (var s in vf.Items) if (s.Updated.Kind == DateTimeKind.Utc) s.Updated = s.Updated.ToLocalTime();
                LastUse = DateTime.Now;
                return true;
            }
            catch { Lock(); return false; }
        }

        public static void Lock()
        {
            if (encKey != null) Array.Clear(encKey, 0, encKey.Length);
            if (macKey != null) Array.Clear(macKey, 0, macKey.Length);
            encKey = macKey = null; vf = null;
        }

        public static void ChangePassword(string pw)
        {
            var plain = vf.Items.Select(s => Decrypt(s.Cipher)).ToList();
            var salt = new byte[32]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(salt);
            vf.Salt = Convert.ToBase64String(salt);
            Derive(pw, salt, vf.Iter);
            vf.Check = Encrypt("AMIOPS-COFFRE-OK");
            for (int i = 0; i < plain.Count; i++) vf.Items[i].Cipher = Encrypt(plain[i]);
            Save();
        }

        public static void Save()
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Store.Json().Serialize(vf), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            try { File.Copy(FilePath, Path.Combine(Store.BackupDir, "coffre_" + DateTime.Now.ToString("yyyy-MM-dd") + ".json"), true); } catch { }
        }

        public static string Encrypt(string plain)
        {
            if (encKey == null) throw new InvalidOperationException("Coffre verrouillé");
            LastUse = DateTime.Now;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Key = encKey; aes.GenerateIV(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                byte[] ct; var data = Encoding.UTF8.GetBytes(plain ?? "");
                using (var e = aes.CreateEncryptor()) ct = e.TransformFinalBlock(data, 0, data.Length);
                var body = aes.IV.Concat(ct).ToArray();
                byte[] mac; using (var h = new HMACSHA256(macKey)) mac = h.ComputeHash(body);
                return Convert.ToBase64String(body.Concat(mac).ToArray());
            }
        }

        public static string Decrypt(string b64)
        {
            if (encKey == null) throw new InvalidOperationException("Coffre verrouillé");
            LastUse = DateTime.Now;
            var all = Convert.FromBase64String(b64);
            if (all.Length < 16 + 16 + 32) throw new CryptographicException("donnée tronquée");
            var body = all.Take(all.Length - 32).ToArray(); var mac = all.Skip(all.Length - 32).ToArray();
            byte[] exp; using (var h = new HMACSHA256(macKey)) exp = h.ComputeHash(body);
            int diff = 0; for (int i = 0; i < 32; i++) diff |= exp[i] ^ mac[i];   // comparaison en temps constant
            if (diff != 0) throw new CryptographicException("intégrité invalide");
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Key = encKey; aes.IV = body.Take(16).ToArray(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var d = aes.CreateDecryptor()) return Encoding.UTF8.GetString(d.TransformFinalBlock(body, 16, body.Length - 16));
            }
        }

        // autotest : clé temporaire, sans toucher au coffre réel
        public static string CryptoSelfTest()
        {
            var ek = encKey; var mk = macKey; var keep = vf;
            try
            {
                Derive("autotest-" + Guid.NewGuid(), new byte[32], 1000);
                var c = Encrypt("Test é ✓"); if (Decrypt(c) != "Test é ✓") return "déchiffrement incorrect";
                var b = Convert.FromBase64String(c); b[18] ^= 1;
                try { Decrypt(Convert.ToBase64String(b)); return "altération NON détectée"; } catch (CryptographicException) { }
                return null;
            }
            finally { if (encKey != null && encKey != ek) Array.Clear(encKey, 0, encKey.Length); encKey = ek; macKey = mk; vf = keep; }
        }

        public static string Generate(int len)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!#$%&*+-=?@_";
            var sb = new StringBuilder(); var b = new byte[4];
            using (var r = RandomNumberGenerator.Create())
                while (sb.Length < len) { r.GetBytes(b); uint v = BitConverter.ToUInt32(b, 0); if (v < uint.MaxValue - uint.MaxValue % (uint)chars.Length) sb.Append(chars[(int)(v % (uint)chars.Length)]); }
            return sb.ToString();
        }
    }
}
