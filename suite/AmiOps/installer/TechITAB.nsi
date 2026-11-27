; Tech IT AB - installateur
; Développé by alex.b
Unicode true
!include "MUI2.nsh"

!define APPNAME   "Tech IT AB"
!define EXENAME   "TechITAB.exe"
!define VERSION   "1.8.2"
!define PUBLISHER "alex.b"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\TechITAB"

Name "${APPNAME} ${VERSION}"
OutFile "..\dist\TechITAB-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\TechITAB"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION} - by ${PUBLISHER}"

VIProductVersion "1.8.2.0"
VIAddVersionKey /LANG=1036 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=1036 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=1036 "FileDescription" "Installateur ${APPNAME}"
VIAddVersionKey /LANG=1036 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "LegalCopyright" "by ${PUBLISHER} - 2026"

!define MUI_ICON   "..\build\techitab.ico"
!define MUI_UNICON "..\build\techitab.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "Tech IT AB : la console complète du technicien et le labo cyber. Clients, parc et GLPI, interventions, PanneScan, optimisation, dépannage, réseau, scans Nmap encadrés, chasse aux menaces, analyse d'e-mails et de liens, détection des scans, coffre AES-256.$\r$\n$\r$\nRemplace AMI·OPS : vos données sont reprises automatiquement.$\r$\n$\r$\nUsage réservé au technicien. Développé by alex.b.$\r$\n$\r$\nPrérequis : Windows 10 / 11, Nmap (nmap.org) pour les scans."
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Lancer ${APPNAME} maintenant"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchAsUser

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "French"

; lancement via l'Explorateur = avec les droits de l'utilisateur (l'appli demande l'admin elle-même si besoin)
Function LaunchAsUser
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${EXENAME}"'
FunctionEnd

Section "${APPNAME}" SecMain
  SectionIn RO
  SetShellVarContext all
  SetRegView 64
  nsExec::Exec 'taskkill /f /im ${EXENAME}'
  Sleep 300
  ; remplace AMI·OPS : tâche planifiée et désinstallation de l'ancienne version (les données restent dans le profil)
  nsExec::Exec 'schtasks /Delete /TN AMI-OPS /F'
  nsExec::Exec 'taskkill /f /im AmiOps.exe'
  ReadRegStr $0 HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\AmiOps" "QuietUninstallString"
  StrCmp $0 "" +2
  ExecWait $0
  SetOutPath "$INSTDIR"
  File "..\build\${EXENAME}"
  File "/oname=app.ico" "..\build\techitab.ico"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Outils alex.b"
  CreateShortcut "$SMPROGRAMS\Outils alex.b\${APPNAME}.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\app.ico" 0 SW_SHOWNORMAL "" "${APPNAME} - by alex.b"
  CreateShortcut "$SMPROGRAMS\Outils alex.b\Désinstaller ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\app.ico" 0 SW_SHOWNORMAL "" "${APPNAME} - by alex.b"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayName"          "${APPNAME}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayVersion"       "${VERSION}"
  WriteRegStr   HKLM "${UNINSTKEY}" "Publisher"            "${PUBLISHER}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayIcon"          "$INSTDIR\app.ico"
  WriteRegStr   HKLM "${UNINSTKEY}" "InstallLocation"      "$INSTDIR"
  WriteRegStr   HKLM "${UNINSTKEY}" "UninstallString"      '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "EstimatedSize" 150
SectionEnd

Section "Uninstall"
  SetShellVarContext all
  SetRegView 64
  nsExec::Exec 'taskkill /f /im ${EXENAME}'
  Sleep 300
  nsExec::Exec 'schtasks /Delete /TN TechITAB /F'
  Delete "$INSTDIR\${EXENAME}"
  Delete "$INSTDIR\${EXENAME}.old"
  Delete "$INSTDIR\app.ico"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir  "$INSTDIR"
  Delete "$SMPROGRAMS\Outils alex.b\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\Outils alex.b\Désinstaller ${APPNAME}.lnk"
  RMDir  "$SMPROGRAMS\Outils alex.b"
  Delete "$DESKTOP\${APPNAME}.lnk"
  DeleteRegKey HKLM "${UNINSTKEY}"
SectionEnd
