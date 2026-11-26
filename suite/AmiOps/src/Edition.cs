// Tech IT AB / PC Santé AB - choix de l'édition à la compilation (symbole POSTE).
// PC Santé AB est compilé SANS le code sensible (Nmap, clients, parc, GLPI, coffre, Multi-PC, AD, secours) :
// il n'est pas masqué, il est absent de l'exécutable.
// Développé by alex.b
namespace AmiOps
{
    static class Ed
    {
#if POSTE
        public const string Name = "PC Santé AB", Id = "PCSanteAB", Asset = "PCSanteAB-portable.exe", Tagline = "Santé et dépannage du PC";
        public const bool Full = false;
#else
        public const string Name = "Tech IT AB", Id = "TechITAB", Asset = "TechITAB-portable.exe", Tagline = "Console du technicien et labo cyber";
        public const bool Full = true;
#endif
        public const string Version = "1.6.0";
        // empreinte SHA-1 du certificat de signature de code d'alex.b (Amiprotek). Une copie signée par ce certificat
        // est « authentique » ; signée par un autre, ou modifiée, elle passe en « copie non autorisée » (fonctions bridées).
        public const string SignThumb = "7C02F586B652E6C722E3523BD86170E5564B9D0A";
    }
}
