; MaintenancePC - installateur
; Développé by alex.b
Unicode true
!include "MUI2.nsh"

!define APPNAME   "MaintenancePC"
!define EXENAME   "MaintenancePC.exe"
!define VERSION   "1.1.1"
!define PUBLISHER "alex.b"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\MaintenancePC"

Name "${APPNAME} ${VERSION}"
OutFile "..\dist\MaintenancePC-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\MaintenancePC"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION} - by ${PUBLISHER}"

VIProductVersion "1.1.1.0"
VIAddVersionKey /LANG=1036 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=1036 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=1036 "FileDescription" "Installateur ${APPNAME}"
VIAddVersionKey /LANG=1036 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "LegalCopyright" "by ${PUBLISHER} - 2026"

!define MUI_ICON   "..\build\app.ico"
!define MUI_UNICON "..\build\app.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "Maintenance Windows en un clic : fichiers temporaires, cache Windows Update, corbeille, DNS, pilotes Windows Update, outil du fabricant (Dell / Lenovo / HP), DISM, SFC, optimisation du disque, avec point de restauration et rapport.$\r$\n$\r$\nDéveloppé by alex.b.$\r$\n$\r$\nAdministrateur requis au lancement.$\r$\n$\r$\nPrérequis : Windows 10 / 11 (.NET Framework 4.8 inclus)."
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
  SetOutPath "$INSTDIR"
  File "..\build\${EXENAME}"
  File "..\build\app.ico"
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
  Delete "$INSTDIR\${EXENAME}"
  Delete "$INSTDIR\app.ico"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir  "$INSTDIR"
  Delete "$SMPROGRAMS\Outils alex.b\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\Outils alex.b\Désinstaller ${APPNAME}.lnk"
  RMDir  "$SMPROGRAMS\Outils alex.b"
  Delete "$DESKTOP\${APPNAME}.lnk"
  DeleteRegKey HKLM "${UNINSTKEY}"
SectionEnd
