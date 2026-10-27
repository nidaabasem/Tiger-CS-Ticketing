<#
.SYNOPSIS  Restores one TigerCS site from a backup made by Deploy-TigerCS.ps1. Touches only that site; never restarts IIS.
.EXAMPLE   .\Rollback-TigerCS.ps1 -App Api -Backup C:\inetpub\TigerCS\_backup\Api-20261101-0900 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param([Parameter(Mandatory)][ValidateSet('Api', 'Web')][string]$App, [Parameter(Mandatory)][string]$Backup)
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration
$map = @{ Api = @{ Site = 'TigerCS-API'; Dir = 'C:\inetpub\TigerCS\Api' }; Web = @{ Site = 'TigerCS-Web'; Dir = 'C:\inetpub\TigerCS\Web' } }[$App]
if (-not (Test-Path (Join-Path $Backup 'web.config'))) { throw "$Backup is not a TigerCS backup." }
if ($PSCmdlet.ShouldProcess($map.Site, "restore $($map.Dir) from $Backup")) {
  Stop-Website -Name $map.Site
  robocopy $Backup $map.Dir /MIR /XD logs /NFL /NDL /NJH /NJS | Out-Null
  if ($LASTEXITCODE -ge 8) { throw 'Restore failed.' }
  Start-Website -Name $map.Site
  Write-Host "$($map.Site) restored from $Backup. Re-run the health checks."
}
