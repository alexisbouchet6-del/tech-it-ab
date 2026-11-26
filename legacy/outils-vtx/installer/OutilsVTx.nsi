; Outils VTx - installateur
; Développé by alex.b
Unicode true
!include "MUI2.nsh"

!define APPNAME   "Outils VTx"
!define EXENAME   "Outils-VTx.exe"
!define VERSION   "1.1.2"
!define PUBLISHER "alex.b"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\OutilsVTx"

Name "${APPNAME} ${VERSION}"
OutFile "..\dist\Outils-VTx-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\Outils VTx"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION} - by ${PUBLISHER}"

VIProductVersion "1.1.2.0"
VIAddVersionKey /LANG=1036 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=1036 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=1036 "FileDescription" "Installateur Outils VTx"
VIAddVersionKey /LANG=1036 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "LegalCopyright" "by ${PUBLISHER} - 2026"

!define MUI_ICON   "..\build\outils-vtx.ico"
!define MUI_UNICON "..\build\outils-vtx.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "Outils VTx bascule le PC entre le mode VMware (VT-x libre) et le mode Windows (Hyper-V, VBS, Intégrité de la mémoire), avec diagnostic complet, accès direct au BIOS, thème cyber sombre / clair et icône permanente près de l'horloge (démarrage automatique).$\r$\n$\r$\nDéveloppé by alex.b.$\r$\n$\r$\nPrérequis : Windows 10 / 11 (.NET Framework 4.8 inclus)."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXENAME}"
!define MUI_FINISHPAGE_RUN_TEXT "Lancer Outils VTx maintenant"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "French"

Section "Outils VTx" SecMain
  SectionIn RO
  SetShellVarContext all
  SetRegView 64

  nsExec::Exec 'taskkill /f /im ${EXENAME}'
  Sleep 500

  SetOutPath "$INSTDIR"
  File "..\build\${EXENAME}"
  File "..\build\outils-vtx.ico"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\Outils VTx"
  CreateShortcut "$SMPROGRAMS\Outils VTx\Outils VTx.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\outils-vtx.ico"
  CreateShortcut "$SMPROGRAMS\Outils VTx\Désinstaller Outils VTx.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\Outils VTx.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\outils-vtx.ico"

  ; icône permanente dans la zone de notification : démarrage auto sans UAC (tâche planifiée)
  DetailPrint "Création de la tâche de démarrage..."
  nsExec::ExecToLog '"$INSTDIR\${EXENAME}" --autostart-on'
  Pop $0
  StrCmp $0 "0" +2
    DetailPrint "Attention : tâche de démarrage non créée (code $0). Activable depuis le menu de l'icône."

  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayName"          "${APPNAME}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayVersion"       "${VERSION}"
  WriteRegStr   HKLM "${UNINSTKEY}" "Publisher"            "${PUBLISHER}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayIcon"          "$INSTDIR\outils-vtx.ico"
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

  nsExec::Exec 'taskkill /f /im ${EXENAME}'
  Sleep 500
  nsExec::Exec '"$INSTDIR\${EXENAME}" --autostart-off'

  Delete "$INSTDIR\${EXENAME}"
  Delete "$INSTDIR\outils-vtx.ico"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir  "$INSTDIR"

  Delete "$SMPROGRAMS\Outils VTx\Outils VTx.lnk"
  Delete "$SMPROGRAMS\Outils VTx\Désinstaller Outils VTx.lnk"
  RMDir  "$SMPROGRAMS\Outils VTx"
  Delete "$DESKTOP\Outils VTx.lnk"

  DeleteRegKey HKLM "${UNINSTKEY}"
  ; la sauvegarde d'état et le journal restent dans C:\ProgramData\OutilsVTx
SectionEnd
