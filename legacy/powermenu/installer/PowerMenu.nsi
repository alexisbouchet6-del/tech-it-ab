; PowerMenu - installateur
; Développé by alex.b
Unicode true
!include "MUI2.nsh"

!define APPNAME   "PowerMenu"
!define EXENAME   "PowerMenu.exe"
!define VERSION   "1.0.1"
!define PUBLISHER "alex.b"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\PowerMenu"

Name "${APPNAME} ${VERSION}"
OutFile "..\dist\PowerMenu-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\PowerMenu"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION} - by ${PUBLISHER}"

VIProductVersion "1.0.1.0"
VIAddVersionKey /LANG=1036 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=1036 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=1036 "FileDescription" "Installateur PowerMenu"
VIAddVersionKey /LANG=1036 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "LegalCopyright" "by ${PUBLISHER} - 2026"

!define MUI_ICON   "..\build\powermenu.ico"
!define MUI_UNICON "..\build\powermenu.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "PowerMenu : éteindre, redémarrer, mettre à jour, se déconnecter, verrouiller, mettre en veille ou programmer l'arrêt, proprement et en un clic.$\r$\n$\r$\nDéveloppé by alex.b.$\r$\n$\r$\nRaccourci clavier : Ctrl + Alt + P.$\r$\n$\r$\nPrérequis : Windows 10 / 11 (.NET Framework 4.8 inclus)."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXENAME}"
!define MUI_FINISHPAGE_RUN_TEXT "Ouvrir PowerMenu maintenant"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "French"

Section "PowerMenu" SecMain
  SectionIn RO
  SetShellVarContext all
  SetRegView 64

  nsExec::Exec 'taskkill /f /im ${EXENAME}'
  Sleep 300

  SetOutPath "$INSTDIR"
  File "..\build\${EXENAME}"
  File "..\build\powermenu.ico"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\PowerMenu"
  CreateShortcut "$SMPROGRAMS\PowerMenu\PowerMenu.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\powermenu.ico"
  CreateShortcut "$SMPROGRAMS\PowerMenu\Désinstaller PowerMenu.lnk" "$INSTDIR\Uninstall.exe"
  ; raccourci bureau avec touche de raccourci globale Ctrl+Alt+P
  CreateShortcut "$DESKTOP\PowerMenu.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\powermenu.ico" 0 SW_SHOWNORMAL "CONTROL|ALT|P" "PowerMenu - by alex.b"

  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayName"          "${APPNAME}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayVersion"       "${VERSION}"
  WriteRegStr   HKLM "${UNINSTKEY}" "Publisher"            "${PUBLISHER}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayIcon"          "$INSTDIR\powermenu.ico"
  WriteRegStr   HKLM "${UNINSTKEY}" "InstallLocation"      "$INSTDIR"
  WriteRegStr   HKLM "${UNINSTKEY}" "UninstallString"      '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "EstimatedSize" 110
SectionEnd

Section "Uninstall"
  SetShellVarContext all
  SetRegView 64
  nsExec::Exec 'taskkill /f /im ${EXENAME}'
  Sleep 300
  Delete "$INSTDIR\${EXENAME}"
  Delete "$INSTDIR\powermenu.ico"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir  "$INSTDIR"
  Delete "$SMPROGRAMS\PowerMenu\PowerMenu.lnk"
  Delete "$SMPROGRAMS\PowerMenu\Désinstaller PowerMenu.lnk"
  RMDir  "$SMPROGRAMS\PowerMenu"
  Delete "$DESKTOP\PowerMenu.lnk"
  DeleteRegKey HKLM "${UNINSTKEY}"
SectionEnd
