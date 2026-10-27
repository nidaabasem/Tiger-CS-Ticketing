<#
.SYNOPSIS  Read-only readiness check for TigerCS on TGAESHJAPP05. Changes nothing and never touches the tigercrm site.
.EXAMPLE   .\Test-IisReadiness.ps1
#>
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration
$fail = 0
function Check($ok, $msg, $hint = '') { if ($ok) { Write-Host "[ OK ] $msg" } else { Write-Host "[FAIL] $msg  $hint" -ForegroundColor Red; $script:fail++ } }

$module = Get-WebGlobalModule | Where-Object Name -eq 'AspNetCoreModuleV2'
Check ($null -ne $module) 'AspNetCoreModuleV2 registered in IIS' '-> install the .NET 10 Hosting Bundle in the maintenance window (docs/Production-Deployment.md section 2)'
Check (Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll") 'aspnetcorev2.dll present'
$runtimes = & dotnet --list-runtimes 2>$null
Check ($runtimes -match 'Microsoft\.AspNetCore\.App 10\.') 'Microsoft.AspNetCore.App 10.x runtime installed'

$apps = @(
  @{ Site = 'TigerCS-API'; Pool = 'TigerCS-API'; Path = 'C:\inetpub\TigerCS\Api'; Port = 8091; Env = @('Jwt__SigningKey', 'ConnectionStrings__TigerCsDatabase') },
  @{ Site = 'TigerCS-Web'; Pool = 'TigerCS-Web'; Path = 'C:\inetpub\TigerCS\Web'; Port = 8092; Env = @() }
)
foreach ($a in $apps) {
  $pool = Get-Item "IIS:\AppPools\$($a.Pool)" -ErrorAction SilentlyContinue
  Check ($null -ne $pool) "Pool $($a.Pool) exists"
  if ($pool) {
    Check ($pool.managedRuntimeVersion -eq '') "$($a.Pool): No Managed Code"
    Check ($pool.processModel.identityType -eq 'ApplicationPoolIdentity') "$($a.Pool): ApplicationPoolIdentity"
    $names = @(Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "system.applicationHost/applicationPools/add[@name='$($a.Pool)']/environmentVariables/add" -Name name | ForEach-Object { $_.Value })
    foreach ($n in $a.Env) { Check ($names -contains $n) "$($a.Pool): environment variable $n is set (value not shown)" }
  }
  $site = Get-Website -Name $a.Site -ErrorAction SilentlyContinue
  Check ($null -ne $site) "Site $($a.Site) exists"
  if ($site) {
    Check ($site.physicalPath -eq $a.Path) "$($a.Site): physical path $($a.Path)"
    Check (($site.bindings.Collection | Where-Object { $_.bindingInformation -like "*:$($a.Port):*" }).Count -gt 0) "$($a.Site): binding on $($a.Port)"
  }
  Check (Test-Path (Join-Path $a.Path 'web.config')) "$($a.Site): web.config deployed" '(expected before first start)'
}
$crm = Get-Website | Where-Object Name -like 'tigercrm*'
Write-Host "[INFO] tigercrm site state (read only, not modified): $($crm.State)"
if ($fail) { Write-Host "$fail check(s) failed." -ForegroundColor Red; exit 1 } else { Write-Host 'All checks passed.' }
