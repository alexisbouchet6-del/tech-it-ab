<#
  Compilation Windows de la suite alex.b, de Tech IT AB et de PC Santé AB avec le compilateur Microsoft (Roslyn, Visual Studio Build Tools).
  Développé by alex.b
  Usage : pwsh -File build/build.ps1            -> exe dans out/
          pwsh -File build/build.ps1 -Tests     -> en plus, bancs de tests console (T15 : page Jeu) pour les deux éditions
#>
[CmdletBinding()]
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'out'), [switch]$Tests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe introuvable : installer Visual Studio Build Tools (charge de travail .NET desktop).' }
$csc = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
if (-not $csc) { throw 'csc.exe (Roslyn) introuvable.' }
Write-Host "Compilateur : $csc"

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.Management', 'System.Web.Extensions', 'System.Security', 'System.Xml' |
    ForEach-Object { "/r:$(Join-Path $fw "$_.dll")" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$obj = Join-Path $Out 'obj'; if (Test-Path $obj) { Remove-Item $obj -Recurse -Force }; New-Item -ItemType Directory -Path $obj | Out-Null

function Invoke-Csc([string]$Name, [string]$Icon, [string[]]$Files, [string]$Main, [string]$Define, [string]$Target = 'winexe') {
    $a = @('/nologo', '/noconfig', '/nostdlib+', "/target:$Target", '/platform:anycpu', '/optimize+', '/langversion:7.3',
           '/codepage:65001', '/nowarn:649,414,169,219', "/win32icon:$Icon", "/out:$(Join-Path $Out "$Name.exe")") + $refs + $Files
    if ($Main) { $a += "/main:$Main" }
    if ($Define) { $a += "/define:$Define" }
    & $csc @a
    if ($LASTEXITCODE -ne 0) { throw "Échec de compilation : $Name" }
    Write-Host "OK  $Name.exe"
}

# copie d'un fichier source sans ses attributs d'assembly (sources embarquées dans Tech IT AB et PC Santé AB)
function Copy-Stripped([string]$File, [string]$Prefix) {
    $dst = Join-Path $obj ("$Prefix" + '_' + (Split-Path $File -Leaf))
    $lines = Get-Content -LiteralPath $File -Encoding UTF8 | Where-Object { $_ -notmatch '^\[assembly:' }
    [IO.File]::WriteAllLines($dst, [string[]]$lines, (New-Object Text.UTF8Encoding $false))
    $dst
}

$suite = Join-Path $root 'suite'; $legacy = Join-Path $root 'legacy'
$common = Join-Path $suite 'common\Cyber.cs'
$apps = 'DiagPoste', 'MaintenancePC', 'PrepaPoste', 'ProfilSave', 'TestReseau', 'ScanParc', 'MailCheck', 'TriFichiers', 'RapportInter', 'NasConnect'

# 1. applis autonomes de la suite
foreach ($a in $apps) {
    Invoke-Csc $a (Join-Path $suite "$a\build\app.ico") (@($common) + (Get-ChildItem (Join-Path $suite "$a\src") -Filter *.cs).FullName)
}
$harden = Join-Path $legacy 'common-legacy\Harden.cs'
Invoke-Csc 'NetSwitch' (Join-Path $legacy 'netswitch\build\netswitch.ico') (@($harden) + (Get-ChildItem (Join-Path $legacy 'netswitch\src') -Filter *.cs).FullName)
Invoke-Csc 'Outils-VTx' (Join-Path $legacy 'outils-vtx\build\outils-vtx.ico') (@($harden) + (Get-ChildItem (Join-Path $legacy 'outils-vtx\src') -Filter *.cs).FullName)
Invoke-Csc 'PowerMenu' (Join-Path $legacy 'powermenu\build\powermenu.ico') (@($harden) + (Get-ChildItem (Join-Path $legacy 'powermenu\src') -Filter *.cs).FullName)

# 2. Tech IT AB (tout) et PC Santé AB (symbole POSTE, liste blanche : aucun code sensible compilé)
$emb = @()
foreach ($a in $apps) { foreach ($f in (Get-ChildItem (Join-Path $suite "$a\src") -Filter *.cs)) { $emb += Copy-Stripped $f.FullName $a } }
foreach ($d in 'netswitch', 'outils-vtx', 'powermenu') { foreach ($f in (Get-ChildItem (Join-Path $legacy "$d\src") -Filter *.cs)) { $emb += Copy-Stripped $f.FullName $d } }
$emb += Copy-Stripped $harden 'legacy'
$src = (Get-ChildItem (Join-Path $suite 'AmiOps\src') -Filter *.cs).FullName
Invoke-Csc 'TechITAB' (Join-Path $suite 'AmiOps\build\techitab.ico') (@($common) + $src + $emb) 'AmiOps.Program'
$excl = 'Pages.cs', 'Parc.cs', 'CyberPage.cs', 'Labo.cs', 'Remote.cs', 'Secours.cs', 'Defense.cs'
$posteSrc = $src | Where-Object { $excl -notcontains (Split-Path $_ -Leaf) }
$posteEmb = $emb | Where-Object { (Split-Path $_ -Leaf) -match '^(DiagPoste|MaintenancePC|TestReseau|ScanParc|TriFichiers|MailCheck|netswitch|outils-vtx|powermenu|legacy)_' }
Invoke-Csc 'PCSanteAB' (Join-Path $suite 'AmiOps\build\pcsante.ico') (@($common) + $posteSrc + $posteEmb) 'AmiOps.Program' 'POSTE'

# 3. bancs de tests (option -Tests) : mêmes sources que chaque édition + le banc, en application console
if ($Tests) {
    $t15 = Join-Path $root 'tests\GameTests.cs'
    Invoke-Csc 'T15-TechITAB' (Join-Path $suite 'AmiOps\build\techitab.ico') (@($common) + $src + $emb + $t15) 'AmiOps.T15' '' 'exe'
    Invoke-Csc 'T15-PCSanteAB' (Join-Path $suite 'AmiOps\build\pcsante.ico') (@($common) + $posteSrc + $posteEmb + $t15) 'AmiOps.T15' 'POSTE' 'exe'
    $t16 = Join-Path $root 'tests\SupervTests.cs'
    Invoke-Csc 'T16-TechITAB' (Join-Path $suite 'AmiOps\build\techitab.ico') (@($common) + $src + $emb + $t16) 'AmiOps.T16' '' 'exe'
    Invoke-Csc 'T16-PCSanteAB' (Join-Path $suite 'AmiOps\build\pcsante.ico') (@($common) + $posteSrc + $posteEmb + $t16) 'AmiOps.T16' 'POSTE' 'exe'
    $t17 = Join-Path $root 'tests\DefenseTests.cs'
    Invoke-Csc 'T17-TechITAB' (Join-Path $suite 'AmiOps\build\techitab.ico') (@($common) + $src + $emb + $t17) 'AmiOps.T17' '' 'exe'
}

Copy-Item (Join-Path $Out 'TechITAB.exe') (Join-Path $Out 'TechITAB-portable.exe') -Force
Copy-Item (Join-Path $Out 'PCSanteAB.exe') (Join-Path $Out 'PCSanteAB-portable.exe') -Force
Remove-Item $obj -Recurse -Force
Get-ChildItem $Out -Filter *.exe | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content (Join-Path $Out 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Terminé : $Out"
