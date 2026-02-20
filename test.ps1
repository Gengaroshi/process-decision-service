param(
  [string]$BaseUrl = "http://10.10.15.15:8888",
  [int]$FailSearchMax = 25
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# =====================================================
# Counters
# =====================================================

$global:PassCount = 0
$global:FailCount = 0
$global:SkipCount = 0

function Write-Section([string]$Title) {
  Write-Host ""
  Write-Host "==== $Title ===="
}

function Pass([string]$Msg) {
  $global:PassCount++
  Write-Host "[PASS] $Msg"
}

function Fail([string]$Msg) {
  $global:FailCount++
  Write-Host "[FAIL] $Msg"
}

function Skip([string]$Msg) {
  $global:SkipCount++
  Write-Host "[SKIP] $Msg"
}

function Assert-True([bool]$Condition, [string]$Msg) {
  if ($Condition) { Pass $Msg }
  else { Fail $Msg; throw $Msg }
}

function Assert-Equals($Expected, $Actual, [string]$Msg) {
  if ($Expected -eq $Actual) {
    Pass "$Msg (expected=$Expected actual=$Actual)"
  } else {
    Fail "$Msg (expected=$Expected actual=$Actual)"
    throw "$Msg (expected=$Expected actual=$Actual)"
  }
}

function Count-Of($x) {
  return @($x).Count
}

# =====================================================
# HTTP helpers
# =====================================================

function Api-Get([string]$Path) {
  return Invoke-RestMethod -Method Get -Uri "$BaseUrl$Path"
}

function Api-PostJson([string]$Path, [hashtable]$Body) {
  return Invoke-RestMethod `
    -Method Post `
    -Uri "$BaseUrl$Path" `
    -ContentType "application/json" `
    -Body ($Body | ConvertTo-Json)
}

function Api-PostNoBody([string]$Path) {
  return Invoke-RestMethod -Method Post -Uri "$BaseUrl$Path"
}

# =====================================================
# Domain helpers
# =====================================================

function New-JobPayload(
  [string]$OrderNo,
  [string]$BatchNo,
  [string]$AttrCode,
  [string]$Filename,
  [int]$PiecesTotal,
  [string]$Priority
) {
  return @{
    orderNo     = $OrderNo
    batchNo     = $BatchNo
    attrCode    = $AttrCode
    filename    = $Filename
    piecesTotal = $PiecesTotal
    priority    = $Priority
  }
}

function Get-PlannedActionsCount($job) {
  if ([string]::IsNullOrWhiteSpace($job.plannedActionsJson)) { return 0 }
  try {
    return Count-Of ($job.plannedActionsJson | ConvertFrom-Json)
  } catch {
    return 0
  }
}

function Has-AuditEvent($audit, [string]$Type) {
  return (Count-Of ($audit | Where-Object { $_.eventType -eq $Type })) -gt 0
}

# =====================================================
# Tests
# =====================================================

function Test-Smoke {
  Write-Section "Smoke test"
  $m = Api-Get "/api/metrics"
  Assert-True ($null -ne $m.total) "metrics.total exists"
  Assert-True ($null -ne $m.byStatus) "metrics.byStatus exists"
}

function Test-Flow([string]$AttrCode, [int]$ExpectedCalls, [string]$Suffix) {
  Write-Section "Flow test attrCode=$AttrCode"

  $orderNo = "ORD-AUDIT-$AttrCode-$Suffix"
  $batchNo = "B-AUDIT-$AttrCode-$Suffix"

  if ($AttrCode -eq "Y") {
    $filename = "12_lamination_2026-02-05"
    $pieces   = 120
    $prio     = "HIGH"
  } else {
    $filename = "5_mounting_2026-02-06"
    $pieces   = 50
    $prio     = "LOW"
  }

  $payload = New-JobPayload $orderNo $batchNo $AttrCode $filename $pieces $prio

  $eval = Api-PostJson "/api/jobs/evaluate" $payload
  Assert-True ($null -ne $eval.jobId) "evaluate returned jobId"
  $jobId = $eval.jobId

  $exec = Api-PostNoBody "/api/jobs/$jobId/execute"
  Assert-True ($exec.status -in @(2,3)) "job finished (Completed or Failed)"

  $full = Api-Get "/api/jobs/$jobId"

  Assert-True ($null -ne $full.job) "job exists"
  Assert-True ($null -ne $full.audit) "audit exists"
  Assert-True ($null -ne $full.calls) "calls exists"

  $planned = Get-PlannedActionsCount $full.job
  $calls   = Count-Of $full.calls

  Assert-Equals $ExpectedCalls $planned "planned actions count"

  Assert-True (Has-AuditEvent $full.audit "EVALUATED") "audit has EVALUATED"
  Assert-True (Has-AuditEvent $full.audit "EXECUTING") "audit has EXECUTING"

  if ($full.job.status -eq 2) {
    Assert-Equals $ExpectedCalls $calls "completed job has expected calls"
    Assert-True (Has-AuditEvent $full.audit "COMPLETED") "audit has COMPLETED"
  } else {
    Assert-True ($calls -ge 1) "failed job has at least one call"
    Assert-True (Has-AuditEvent $full.audit "INTEGRATION_ERROR") "audit has INTEGRATION_ERROR"
  }

  Write-Host "JobId: $jobId"
  Write-Host "Decision: $($full.job.decision)"
  Write-Host "Status: $($full.job.status)"
  Write-Host "Planned actions: $planned"
  Write-Host "Calls recorded: $calls"

  return $jobId
}

function Test-ExecuteIdempotency([string]$JobId) {
  Write-Section "Execute idempotency"

  try {
    Api-PostNoBody "/api/jobs/$JobId/execute" | Out-Null
    Fail "second execute was allowed"
    throw "idempotency failed"
  } catch {
    Pass "second execute blocked"
  }
}

function Test-FailureDiscovery {
  Write-Section "Failure discovery (random mock fail)"

  for ($i = 1; $i -le $FailSearchMax; $i++) {
    $p = New-JobPayload "ORD-AUDIT-FAIL-$i" "B-FAIL" "Y" "" 1 "HIGH"
    $eval = Api-PostJson "/api/jobs/evaluate" $p
    $jobId = $eval.jobId
    $exec = Api-PostNoBody "/api/jobs/$jobId/execute"

    if ($exec.status -eq 3) {
      Pass "found FAILED job after $i attempts ($jobId)"
      $full = Api-Get "/api/jobs/$jobId"
      Assert-True (Has-AuditEvent $full.audit "INTEGRATION_ERROR") "failed job has INTEGRATION_ERROR"
      Assert-True ((Count-Of $full.calls) -ge 1) "failed job has calls"
      return
    }
  }

  Skip "no failure hit in $FailSearchMax attempts (random)"
}

function Test-Metrics {
  Write-Section "Metrics sanity"
  $m = Api-Get "/api/metrics"
  Assert-True ($m.total -ge 1) "metrics.total >= 1"

  $sum = 0
  foreach ($row in @($m.byStatus)) { $sum += [int]$row.count }
  Assert-Equals $m.total $sum "sum(byStatus.count) == total"
}

# =====================================================
# RUN
# =====================================================

$start = Get-Date
Write-Host "BaseUrl: $BaseUrl"
Write-Host "Started: $start"

try {
  Test-Smoke

  $jobY = Test-Flow -AttrCode "Y" -ExpectedCalls 2 -Suffix "01"
  Test-ExecuteIdempotency -JobId $jobY

  $jobZ = Test-Flow -AttrCode "Z" -ExpectedCalls 2 -Suffix "01"
  Test-ExecuteIdempotency -JobId $jobZ

  Test-FailureDiscovery
  Test-Metrics

} catch {
  Write-Host ""
  Write-Host "ERROR: $($_.Exception.Message)"
} finally {
  $end = Get-Date
  $dur = New-TimeSpan -Start $start -End $end

  Write-Host ""
  Write-Host "==== SUMMARY ===="
  Write-Host "PASS: $global:PassCount"
  Write-Host "FAIL: $global:FailCount"
  Write-Host "SKIP: $global:SkipCount"
  Write-Host "Duration: $dur"
  Write-Host "Finished: $end"

  if ($global:FailCount -gt 0) { exit 1 } else { exit 0 }
}
