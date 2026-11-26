// Tech IT AB / PC Santé AB - contrôle d'authenticité : l'exe vérifie sa propre signature Authenticode
// contre l'empreinte épinglée d'alex.b. Une copie modifiée ou re-signée par un tiers passe en « non autorisée »
// et les fonctions sensibles (cyber, labo, supervision) sont désactivées.
// Développé by alex.b
//
// Honnêteté : ça détecte et bride une copie trafiquée, ça ne rend pas le binaire impossible à modifier.
// Un reverse-engineer déterminé peut retirer ce contrôle ; il arrête le pillage facile (« envoie l'exe à une IA
// et rebrande »), pas un professionnel motivé. La vraie protection « sous mon nom », c'est la signature + la source privée.

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace AmiOps
{
    static class Authenticity
    {
        // états
        public const string Dev = "dev";             // build de développement (Mono) : jamais une copie distribuée
        public const string Authentic = "authentique"; // signée, valide, empreinte = alex.b
        public const string Unsigned = "nonsignee";    // aucune signature (portable non signé, ou clone recompilé)
        public const string Tampered = "alteree";      // signée par un autre, ou modifiée après signature

        static string _state;
        public static string State { get { if (_state == null) _state = CheckSelf(); return _state; } }

        // fonctions sensibles autorisées ? (authentique ou build de dev)
        public static bool Trusted { get { return State == Authentic || State == Dev; } }

        // message de bandeau, ou null si tout va bien (authentique / dev)
        public static string Banner
        {
            get
            {
                switch (State)
                {
                    case Unsigned: return "⚠  Copie non signée — cette version n'est pas signée par alex.b. Supervision et outils sensibles désactivés. La version officielle signée débloque tout.";
                    case Tampered: return "⛔  COPIE NON AUTORISÉE — cette application a été modifiée ou re-signée par un tiers. Ce n'est pas la version officielle d'alex.b. Supervision et outils sensibles désactivés.";
                    default: return null;
                }
            }
        }

        // décision pure (testable) : présence + validité + empreinte comparées à l'empreinte épinglée
        public static string Decide(bool present, bool valid, string thumb, string pin)
        {
            if (!present) return Unsigned;
            if (valid && !string.IsNullOrEmpty(pin) && Norm(thumb) == Norm(pin)) return Authentic;
            return Tampered;   // signée mais empreinte différente, ou signature invalide (modifiée)
        }
        static string Norm(string s) { return (s ?? "").Replace(" ", "").ToUpperInvariant(); }

        static string CheckSelf()
        {
            // sous Mono : environnement de dev/test d'alex.b, jamais une copie distribuée → tout autorisé.
            // La protection réelle est le chemin Windows ci-dessous, sur les copies distribuées.
            if (Type.GetType("Mono.Runtime") != null) return Dev;
            try
            {
                var path = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(path)) return Dev;
                string thumb;
                try { using (var c = new X509Certificate2(X509Certificate.CreateFromSignedFile(path))) thumb = c.Thumbprint; }
                catch { return Unsigned; }   // CreateFromSignedFile lève si l'exe n'a aucune signature
                bool valid = WinTrust.Verify(path);   // hash du fichier vérifié contre la signature (détecte une modification)
                return Decide(true, valid, thumb, Ed.SignThumb);
            }
            catch { return Unsigned; }
        }
    }

    // vérification Authenticode via WinVerifyTrust (Windows uniquement ; non exécuté sous Mono)
    static class WinTrust
    {
        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1;
        const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
        const uint WTD_SAFER_FLAG = 0x100, WTD_REVOCATION_CHECK_NONE = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FILE_INFO { public uint cbStruct; public IntPtr pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        struct WTD
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData;
            public uint dwUIChoice; public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile;
            public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
            public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = false)]
        static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

        public static bool Verify(string path)
        {
            var fi = new FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(FILE_INFO)), pcwszFilePath = Marshal.StringToCoTaskMemUni(path) };
            IntPtr pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(FILE_INFO))); Marshal.StructureToPtr(fi, pFile, false);
            var wtd = new WTD
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WTD)), dwUIChoice = WTD_UI_NONE, fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE, pFile = pFile, dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_SAFER_FLAG | WTD_REVOCATION_CHECK_NONE
            };
            IntPtr pWtd = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WTD))); Marshal.StructureToPtr(wtd, pWtd, false);
            try
            {
                uint r = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, pWtd);
                wtd.dwStateAction = WTD_STATEACTION_CLOSE; Marshal.StructureToPtr(wtd, pWtd, true);
                WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, pWtd);
                return r == 0;   // 0 = signature présente, chaîne de confiance et hash du fichier valides
            }
            finally { Marshal.FreeCoTaskMem(fi.pcwszFilePath); Marshal.FreeCoTaskMem(pFile); Marshal.FreeCoTaskMem(pWtd); }
        }
    }
}
