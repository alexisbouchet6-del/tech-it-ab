<#
  Autotest d'AMI·OPS sans fenêtre (lecture seule). Code de sortie = nombre de tests en échec.
  Développé by alex.b
  Usage : pwsh -File build/selftest.ps1 [-Full]
#>
[CmdletBinding()]
param([string]$Exe = (Join-Path (Split-Path $PSScriptRoot -Parent) 'out\AmiOps.exe'), [switch]$Full)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "AmiOps.exe introuvable : $Exe (lancer build/build.ps1)" }
$report = Join-Path (Split-Path $Exe -Parent) 'autotest.txt'
$mode = if ($Full) { '--selftest' } else { '--selftest-ci' }
$p = Start-Process -FilePath $Exe -ArgumentList $mode, "`"$report`"" -Wait -PassThru -WindowStyle Hidden
if (Test-Path $report) { Get-Content $report -Encoding UTF8 | Write-Host } else { Write-Host 'Aucun rapport produit.' }
Write-Host "Tests en échec : $($p.ExitCode)"
exit $p.ExitCode
