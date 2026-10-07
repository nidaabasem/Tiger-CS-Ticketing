<#
.SYNOPSIS
  Moves REVIEWED misrouted Genesys tickets to their intended department
  through the supported transfer workflow (POST /api/tickets/{id}/transfer).

.DESCRIPTION
  This is NOT a bulk Finance transfer. It reads a CSV you prepared after
  running 01_Investigate_Finance_Routing_UAT.sql, one row per ticket:

      TicketId,TargetDepartmentId,Reason
      1234,7,Genesys web chat for Leasing filed under Finance (queue mapping)
      1235,5,Genesys call for Customer Service filed under Finance

  Each row goes through the same TransferAsync a CS Manager uses in the UI:
  - the ticket's RowVersion is read first (optimistic concurrency),
  - the transfer is audited ("Transfer" entry, before/after department),
  - OriginatingDepartmentId is never changed (write-once),
  - the ticket number keeps its TG-FIN prefix (numbers are fixed at creation),
  - the receiving department's auto-assignment rules run, or the ticket lands
    in that department's queue.
  Closed tickets are refused by the API (422) and reported, not forced.

.PARAMETER ApiBaseUrl   e.g. https://uat-tigercs-api.tigergroup.ae/
.PARAMETER Username     a CS Manager (or System Administrator) account
.PARAMETER CsvPath      the reviewed list
.PARAMETER WhatIf       print what would be transferred, call nothing
#>
param(
  [Parameter(Mandatory)] [string] $ApiBaseUrl,
  [Parameter(Mandatory)] [string] $Username,
  [Parameter(Mandatory)] [string] $CsvPath,
  [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'
$base = $ApiBaseUrl.TrimEnd('/')
$password = Read-Host -AsSecureString "Password for $Username"
$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($password))

$login = Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' `
  -Body (@{ username = $Username; password = $plain } | ConvertTo-Json)
$headers = @{ Authorization = "Bearer $($login.accessToken)" }
$plain = $null

$rows = Import-Csv -Path $CsvPath
if (-not $rows) { throw "CSV is empty: $CsvPath" }

$results = foreach ($row in $rows) {
  $id = [long]$row.TicketId
  $target = [int]$row.TargetDepartmentId
  $reason = $row.Reason
  if ([string]::IsNullOrWhiteSpace($reason)) { throw "Ticket $id has no Reason — every transfer must say why." }

  $ticket = Invoke-RestMethod -Method Get -Uri "$base/api/tickets/$id" -Headers $headers
  if ($ticket.currentDepartmentId -eq $target) {
    [pscustomobject]@{ TicketId = $id; TicketNumber = $ticket.ticketNumber; Outcome = 'Skipped: already in target department' }
    continue
  }

  if ($WhatIf) {
    [pscustomobject]@{ TicketId = $id; TicketNumber = $ticket.ticketNumber; Outcome = "WhatIf: would transfer $($ticket.currentDepartmentId) -> $target" }
    continue
  }

  try {
    $body = @{ targetDepartmentId = $target; reason = $reason; rowVersion = $ticket.rowVersion } | ConvertTo-Json
    $moved = Invoke-RestMethod -Method Post -Uri "$base/api/tickets/$id/transfer" -Headers $headers -ContentType 'application/json' -Body $body
    [pscustomobject]@{ TicketId = $id; TicketNumber = $moved.ticketNumber; Outcome = "Transferred to department $($moved.currentDepartmentId) (originating stays $($moved.originatingDepartmentId))" }
  }
  catch {
    $detail = $_.ErrorDetails.Message
    [pscustomobject]@{ TicketId = $id; TicketNumber = $ticket.ticketNumber; Outcome = "FAILED: $($_.Exception.Message) $detail" }
  }
}

$results | Format-Table -AutoSize
$results | Export-Csv -NoTypeInformation -Path ([IO.Path]::ChangeExtension($CsvPath, 'result.csv'))
