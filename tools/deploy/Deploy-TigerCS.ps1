<#
.SYNOPSIS  Deploys a published build to the existing TigerCS IIS sites on TGAESHJAPP05. Only TigerCS-API / TigerCS-Web are touched;
           the script refuses any other site and never restarts IIS. Use -WhatIf first.
.EXAMPLE   .\Deploy-TigerCS.ps1 -Source C:\build\TigerCS\20261101-0900 -WhatIf
.EXAMPLE   .\Deploy-TigerCS.ps1 -Source C:\build\TigerCS\20261101-0900 -Confirm:$false
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param([Parameter(Mandatory)][string]$Source, [string]$BackupRoot = 'C:\inetpub\TigerCS\_backup')
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration
if (-not (Get-WebGlobalModule | Where-Object Name -eq 'AspNetCoreModuleV2')) { throw 'AspNetCoreModuleV2 is not installed. Install the Hosting Bundle in the maintenance window first.' }
$stamp = Get-Date -Format yyyyMMdd-HHmm
$targets = [ordered]@{ Api = @{ Site = 'TigerCS-API'; Dir = 'C:\inetpub\TigerCS\Api'; Url = 'http://localhost:8091/health' }
                       Web = @{ Site = 'TigerCS-Web'; Dir = 'C:\inetpub\TigerCS\Web'; Url = 'http://localhost:8092/health' } }
foreach ($k in $targets.Keys) { if (-not (Test-Path (Join-Path $Source "$k\web.config"))) { throw "Source $Source\$k is not a published build." } }

foreach ($k in $targets.Keys) {                                   # API first, then Web
  $t = $targets[$k]
  if ($t.Site -notin 'TigerCS-API', 'TigerCS-Web') { throw 'Refusing to touch any other site.' }
  if (-not $PSCmdlet.ShouldProcess($t.Site, "stop, back up, copy $Source\$k to $($t.Dir), start")) { continue }
  Stop-Website -Name $t.Site
  if (Get-ChildItem $t.Dir -Force -ErrorAction SilentlyContinue) {
    $backup = Join-Path $BackupRoot "$k-$stamp"
    robocopy $t.Dir $backup /MIR /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Backup of $($t.Dir) failed." }
    Write-Host "Backed up to $backup"
  }
  robocopy (Join-Path $Source $k) $t.Dir /MIR /XD logs /NFL /NDL /NJH /NJS | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "Copy to $($t.Dir) failed." }
  Start-Website -Name $t.Site
  $ok = $false
  foreach ($i in 1..15) { try { if ((Invoke-WebRequest $t.Url -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { $ok = $true; break } } catch { Start-Sleep 2 } }
  if (-not $ok) { Write-Warning "$($t.Site) did not become healthy. Run Rollback-TigerCS.ps1 -Backup $backup, or read the Application event log."; exit 1 }
  Write-Host "$($t.Site) healthy."
}
