<#
  AMI·OPS - création de la clé de secours (WinPE + AMI·OPS en mode secours)
  Développé by alex.b

  PRÉREQUIS (sur ton PC, une seule fois) :
    1. Windows ADK  : https://learn.microsoft.com/windows-hardware/get-started/adk-install
       (cocher au minimum « Deployment Tools »)
    2. Module complémentaire WinPE pour l'ADK (même page, même version que l'ADK)
  UTILISATION (PowerShell en administrateur) :
    .\Creer-cle-secours.ps1                      -> crée l'ISO  (C:\AmiOps-Secours\AmiOps-Secours.iso, à mettre sur Ventoy)
    .\Creer-cle-secours.ps1 -Usb E               -> écrit directement sur la clé E:  (LA CLÉ EST EFFACÉE)
    .\Creer-cle-secours.ps1 -Exe D:\AmiOps.exe   -> chemin de AmiOps.exe si ce n'est pas celui installé
#>
[CmdletBinding()]
param(
    [string]$Exe = "",
    [string]$Usb = "",
    [string]$Work = "C:\AmiOps-Secours"
)
$ErrorActionPreference = 'Stop'

function Step($t) { Write-Host "`n==> $t" -ForegroundColor Cyan }

# --- droits administrateur
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Lance ce script dans un PowerShell ouvert en administrateur."
}

# --- AmiOps.exe
if ($Exe -eq "") {
    foreach ($p in @("$env:ProgramFiles\AmiOps\AmiOps.exe", "$PSScriptRoot\..\AmiOps-portable.exe", "$PSScriptRoot\AmiOps.exe")) { if (Test-Path $p) { $Exe = (Resolve-Path $p).Path; break } }
}
if (-not (Test-Path $Exe)) { throw "AmiOps.exe introuvable : précise -Exe <chemin>" }

# --- ADK
$adk = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Assessment and Deployment Kit"
$pe  = Join-Path $adk "Windows Preinstallation Environment"
$ocs = Join-Path $pe "amd64\WinPE_OCs"
if (-not (Test-Path (Join-Path $pe "amd64\en-us\winpe.wim"))) { throw "Module WinPE de l'ADK absent : installe « Windows PE add-on for the ADK »." }

# --- validation de la clé AVANT tout travail
if ($Usb -ne "") {
    $Usb = $Usb.TrimEnd(':','\').ToUpper()
    if ($Usb -eq $env:SystemDrive.TrimEnd(':')) { throw "Refusé : $Usb est le disque système." }
    $vol = Get-Volume -DriveLetter $Usb -ErrorAction SilentlyContinue
    if (-not $vol) { throw "Lecteur $Usb introuvable." }
    $disk = Get-Partition -DriveLetter $Usb | Get-Disk
    if ($disk.BusType -ne 'USB') { throw "Refusé : $Usb n'est pas un périphérique USB (bus $($disk.BusType))." }
    Write-Host "`nATTENTION : la clé $Usb ($($disk.FriendlyName), $([math]::Round($disk.Size/1GB)) Go) va être ENTIÈREMENT EFFACÉE." -ForegroundColor Yellow
    if ((Read-Host "Tape OUI pour continuer") -ne 'OUI') { Write-Host "Annulé."; return }
}

# --- copie de travail WinPE (équivalent de copype)
Step "Préparation de $Work"
if (Test-Path "$Work\mount\Windows") { Dism /Unmount-Image /MountDir:"$Work\mount" /Discard | Out-Null }
if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
New-Item -ItemType Directory -Path "$Work\media\sources", "$Work\mount", "$Work\fwfiles" -Force | Out-Null
Copy-Item "$pe\amd64\Media\*" "$Work\media" -Recurse -Force
Copy-Item "$pe\amd64\en-us\winpe.wim" "$Work\media\sources\boot.wim" -Force
Copy-Item "$adk\Deployment Tools\amd64\Oscdimg\efisys.bin", "$adk\Deployment Tools\amd64\Oscdimg\etfsboot.com" "$Work\fwfiles" -Force

Step "Montage de l'image"
Dism /Mount-Image /ImageFile:"$Work\media\sources\boot.wim" /Index:1 /MountDir:"$Work\mount" | Out-Null

# --- composants : WMI -> NetFx -> Scripting -> PowerShell -> StorageWMI -> DismCmdlets (ordre des dépendances Microsoft)
$lang = if (Test-Path "$ocs\fr-fr") { "fr-fr" } else { "en-us" }
Step "Ajout des composants (.NET, PowerShell, stockage) - langue $lang"
foreach ($oc in "WinPE-WMI", "WinPE-NetFx", "WinPE-Scripting", "WinPE-PowerShell", "WinPE-StorageWMI", "WinPE-DismCmdlets", "WinPE-EnhancedStorage") {
    Write-Host "   $oc"
    Dism /Image:"$Work\mount" /Add-Package /PackagePath:"$ocs\$oc.cab" | Out-Null
    $lp = "$ocs\$lang\$($oc)_$lang.cab"
    if (Test-Path $lp) { Dism /Image:"$Work\mount" /Add-Package /PackagePath:"$lp" | Out-Null }
}
if ($lang -eq "fr-fr") {
    Dism /Image:"$Work\mount" /Add-Package /PackagePath:"$ocs\fr-fr\lp.cab" | Out-Null
    Dism /Image:"$Work\mount" /Set-AllIntl:fr-FR | Out-Null
}
Dism /Image:"$Work\mount" /Set-InputLocale:040c:0000040c | Out-Null   # clavier AZERTY

# --- AMI·OPS + démarrage automatique
Step "Copie de AMI·OPS"
New-Item -ItemType Directory -Path "$Work\mount\AmiOps" -Force | Out-Null
Copy-Item $Exe "$Work\mount\AmiOps\AmiOps.exe" -Force
@"
@echo off
wpeinit
wpeutil SetKeyboardLayout 040c:0000040c >nul 2>&1
title AMI·OPS - mode secours - by alex.b
echo.
echo   AMI·OPS - mode secours (by alex.b)
echo   Fermer AMI·OPS = retour a cette invite. Taper  X:\AmiOps\AmiOps.exe  pour le relancer.
echo.
start "" X:\AmiOps\AmiOps.exe --secours
"@ | Set-Content -Path "$Work\mount\Windows\System32\startnet.cmd" -Encoding ASCII

# pilotes de stockage supplémentaires (Intel RST/VMD, etc.) : déposer les .inf dans .\pilotes avant de lancer le script
if (Test-Path "$PSScriptRoot\pilotes") {
    Step "Ajout des pilotes de $PSScriptRoot\pilotes"
    Dism /Image:"$Work\mount" /Add-Driver /Driver:"$PSScriptRoot\pilotes" /Recurse | Out-Null
}

Step "Démontage (enregistrement)"
Dism /Unmount-Image /MountDir:"$Work\mount" /Commit | Out-Null

# --- sortie
# MakeWinPEMedia a besoin de l'environnement de l'ADK (DandISetEnv.bat)
$env_ = "`"$adk\Deployment Tools\DandISetEnv.bat`""
if ($Usb -ne "") {
    Step "Écriture sur la clé $($Usb):"
    & cmd /c "call $env_ >nul && MakeWinPEMedia /UFD /F `"$Work`" $($Usb):"
} else {
    Step "Création de l'ISO"
    & cmd /c "call $env_ >nul && MakeWinPEMedia /ISO /F `"$Work`" `"$Work\AmiOps-Secours.iso`""
    Write-Host "`nISO prête : $Work\AmiOps-Secours.iso  (copie-la sur ta clé Ventoy)" -ForegroundColor Green
}
Write-Host "`nTerminé - by alex.b" -ForegroundColor Green
