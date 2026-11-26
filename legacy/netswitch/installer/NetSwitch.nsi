; NetSwitch - installateur
Unicode true
!include "MUI2.nsh"

!define APPNAME   "NetSwitch"
!define VERSION   "1.1.2"
!define PUBLISHER "alex.b"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\NetSwitch"

Name "${APPNAME} ${VERSION}"
OutFile "..\dist\NetSwitch-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\NetSwitch"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION} - by ${PUBLISHER}"

VIProductVersion "1.1.2.0"
VIAddVersionKey /LANG=1036 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=1036 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=1036 "FileDescription" "Installateur NetSwitch"
VIAddVersionKey /LANG=1036 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "LegalCopyright" "by ${PUBLISHER} - 2026"

!define MUI_ICON   "..\build\netswitch.ico"
!define MUI_UNICON "..\build\netswitch.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "NetSwitch ajoute une icône près de l'horloge pour basculer en un clic une carte Wi-Fi ou Ethernet entre DHCP et IP statique (192.168.X.100).$\r$\n$\r$\nDéveloppé by alex.b.$\r$\n$\r$\nNouveau : panneau moderne au clic sur l'icône, thème cyber sombre / clair.$\r$\n$\r$\nIl démarrera automatiquement à l'ouverture de session.$\r$\n$\r$\nPrérequis : Windows 10 / 11 (.NET Framework 4.8 inclus)."
!define MUI_FINISHPAGE_RUN "$INSTDIR\NetSwitch.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Lancer NetSwitch maintenant"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "French"

Section "NetSwitch" SecMain
  SectionIn RO
  SetShellVarContext all
  SetRegView 64

  ; ferme une instance en cours (mise à jour)
  nsExec::Exec 'taskkill /f /im NetSwitch.exe'
  Sleep 800

  SetOutPath "$INSTDIR"
  File "..\build\NetSwitch.exe"
  File "..\build\netswitch.ico"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\NetSwitch"
  CreateShortcut "$SMPROGRAMS\NetSwitch\NetSwitch.lnk" "$INSTDIR\NetSwitch.exe" "" "$INSTDIR\netswitch.ico"
  CreateShortcut "$SMPROGRAMS\NetSwitch\Désinstaller NetSwitch.lnk" "$INSTDIR\Uninstall.exe"

  ; démarrage automatique élevé sans invite UAC (tâche planifiée)
  DetailPrint "Création de la tâche de démarrage..."
  nsExec::ExecToLog '"$INSTDIR\NetSwitch.exe" --autostart-on'
  Pop $0
  StrCmp $0 "0" +2
    DetailPrint "Attention : tâche de démarrage non créée (code $0). Activable depuis le menu de l'icône."

  ; Programmes et fonctionnalités
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayName"          "${APPNAME}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayVersion"       "${VERSION}"
  WriteRegStr   HKLM "${UNINSTKEY}" "Publisher"            "${PUBLISHER}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayIcon"          "$INSTDIR\netswitch.ico"
  WriteRegStr   HKLM "${UNINSTKEY}" "InstallLocation"      "$INSTDIR"
  WriteRegStr   HKLM "${UNINSTKEY}" "UninstallString"      '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "EstimatedSize" 120
SectionEnd

Section "Uninstall"
  SetShellVarContext all
  SetRegView 64

  nsExec::Exec 'taskkill /f /im NetSwitch.exe'
  Sleep 800
  nsExec::Exec '"$INSTDIR\NetSwitch.exe" --autostart-off'

  Delete "$INSTDIR\NetSwitch.exe"
  Delete "$INSTDIR\netswitch.ico"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir  "$INSTDIR"

  Delete "$SMPROGRAMS\NetSwitch\NetSwitch.lnk"
  Delete "$SMPROGRAMS\NetSwitch\Désinstaller NetSwitch.lnk"
  RMDir  "$SMPROGRAMS\NetSwitch"

  DeleteRegKey HKLM "${UNINSTKEY}"
  ; la config et le journal restent dans C:\ProgramData\NetSwitch (à supprimer à la main si besoin)
SectionEnd
