#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Live smoke test against the production Auricrux API (AUX-020 / AUX-029 / AUX-030).

.DESCRIPTION
  Hits the real production host (default: https://auricrux.futurecontractorsofamerica.com)
  to prove the deployed backend is the actual AI stack (health, chat, thinking, search),
  not a mock/minimal API shell. Intended to be run manually or wired into a scheduled
  GitHub Actions workflow for ongoing production verification.

.PARAMETER BaseUrl
  Production base URL to test. Defaults to the app's documented production endpoint.

.EXAMPLE
  ./scripts/smoke_prod.ps1
  ./scripts/smoke_prod.ps1 -BaseUrl "https://auricrux.futurecontractorsofamerica.com"
#>
param(
    # Azure is permanently retired; live product is HTTPS on the custom domain.
    [string]$BaseUrl = "https://auricrux.futurecontractorsofamerica.com"
)

$ErrorActionPreference = "Stop"
$baseUrl = $BaseUrl.TrimEnd('/')
$results = New-Object System.Collections.Generic.List[object]
$failures = 0
$warnings = 0

function Invoke-SmokeCheck {
    param(
        [string]$Name,
        [scriptblock]$Action,
        [switch]$Optional
    )
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $result = & $Action
        $sw.Stop()
        Write-Host "[PASS] $Name ($($sw.ElapsedMilliseconds)ms)" -ForegroundColor Green
        $script:results.Add([ordered]@{ name = $Name; status = "PASS"; optional = [bool]$Optional; elapsedMs = $sw.ElapsedMilliseconds; detail = $result })
    }
    catch {
        if ($Optional) {
            Write-Host "[WARN] $Name (LLM path, non-gating) - $($_.Exception.Message)" -ForegroundColor Yellow
            $script:results.Add([ordered]@{ name = $Name; status = "WARN"; optional = $true; error = $_.Exception.Message })
            $script:warnings++
        }
        else {
            Write-Host "[FAIL] $Name - $($_.Exception.Message)" -ForegroundColor Red
            $script:results.Add([ordered]@{ name = $Name; status = "FAIL"; optional = $false; error = $_.Exception.Message })
            $script:failures++
        }
    }
}

Write-Host "Auricrux production smoke test against $baseUrl" -ForegroundColor Cyan
Write-Host "Run at (UTC): $((Get-Date).ToUniversalTime().ToString('o'))"
Write-Host ""

Invoke-SmokeCheck "GET /api/health (runtime identity)" {
    $r = Invoke-RestMethod -Uri "$baseUrl/api/health" -Method Get -TimeoutSec 30
    if (-not $r.status) { throw "No status in /api/health" }
    if (-not $r.runtimeMode) { throw "No runtimeMode in /api/health" }
    $ver = $r.version
    $sha = $r.gitSha
    "status=$($r.status) runtimeMode=$($r.runtimeMode) version=$ver gitSha=$sha primary=$($r.primaryModel)"
}

Invoke-SmokeCheck "GET /health" {
    $r = Invoke-RestMethod -Uri "$baseUrl/health" -Method Get -TimeoutSec 30
    $status = if ($r -is [string]) { $r } elseif ($r.status) { $r.status } else { $null }
    if (-not $status) { throw "No recognizable status in health response: $($r | ConvertTo-Json -Compress)" }
    "status=$status"
}

Invoke-SmokeCheck "GET /api/models" {
    $r = Invoke-RestMethod -Uri "$baseUrl/api/models" -Method Get -TimeoutSec 30
    if (-not $r.models -or $r.models.Count -lt 1) { throw "No models returned" }
    "models=$($r.models -join ',')"
}

Invoke-SmokeCheck "POST /api/chat (real construction query)" -Optional {
    $body = '{"query":"What is a sill plate?","thinkingMode":0,"searchScope":0}'
    $r = Invoke-RestMethod -Uri "$baseUrl/api/chat" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 120
    if (-not $r.content -or $r.content.Length -lt 10) { throw "Chat content missing or too short" }
    "contentLength=$($r.content.Length)"
}

Invoke-SmokeCheck "POST /api/breakthrough/demo/foundation-pour (ACI 305R)" {
    $r = Invoke-RestMethod -Uri "$baseUrl/api/breakthrough/demo/foundation-pour" -Method Post -Body '{}' -ContentType "application/json" -TimeoutSec 60
    $hyps = @($r.hypotheses)
    if ($hyps.Count -ne 4) { throw "Expected 4 pour hypotheses, got $($hyps.Count)" }
    $hot = $hyps | Where-Object { $_.approach -match 'Hot-Weather' }
    if (-not $hot) { throw "Missing ACI 305R Hot-Weather strategy" }
    if ($r.pedagogySilence -eq $true) { throw "closed pour loop must not silence pedagogy" }
    if ($r.pedagogyProposedAction -ne "hold-strip") { throw "expected hold-strip, got $($r.pedagogyProposedAction)" }
    if ($r.pedagogyLesson -notmatch 'not catalog matching') { throw "expected job-derived lesson" }
    if ($r.pedagogyRecorded -ne $true) { throw "closed loop must record a field lesson" }
    "hypotheses=$($hyps.Count) recommended=$($r.recommendedApproach) action=$($r.pedagogyProposedAction) lessonId=$($r.pedagogyLessonId)"
}

Invoke-SmokeCheck "GET /api/breakthrough/field-lessons (job-derived process memory)" {
    $r = Invoke-RestMethod -Uri "$baseUrl/api/breakthrough/field-lessons?projectId=demo-foundation-pour" -Method Get -TimeoutSec 30
    if ($r.count -lt 1) { throw "expected at least one field lesson" }
    if ($r.durableStore -ne "process-memory") { throw "expected process-memory store" }
    $hold = @($r.lessons) | Where-Object { $_.proposedAction -eq "hold-strip" }
    if (-not $hold) { throw "expected hold-strip lesson in process memory" }
    "count=$($r.count) store=$($r.durableStore)"
}

Invoke-SmokeCheck "POST /api/breakthrough/act (proof-gated, audit only)" {
    $pour = Invoke-RestMethod -Uri "$baseUrl/api/breakthrough/demo/foundation-pour" -Method Post -Body '{}' -ContentType "application/json" -TimeoutSec 60
    $decisionId = $pour.decisionId
    $verificationId = $pour.verification.verificationId
    if (-not $decisionId -or -not $verificationId) { throw "pour demo missing decisionId/verificationId for act" }

    $denyBody = @{
        action = "hold-strip"
        slice = "foundation-pour"
        projectId = "demo-foundation-pour"
        humanAccepted = $false
        decisionId = $decisionId
        verificationId = $verificationId
    } | ConvertTo-Json
    $denied = Invoke-RestMethod -Uri "$baseUrl/api/breakthrough/act" -Method Post -Body $denyBody -ContentType "application/json" -TimeoutSec 30
    if ($denied.accepted -eq $true) { throw "act without humanAccepted must be refused" }
    if ($denied.mutationApplied -eq $true) { throw "refused act must not mutate" }

    $okBody = @{
        action = "hold-strip"
        slice = "foundation-pour"
        projectId = "demo-foundation-pour"
        humanAccepted = $true
        decisionId = $decisionId
        verificationId = $verificationId
    } | ConvertTo-Json
    $ok = Invoke-RestMethod -Uri "$baseUrl/api/breakthrough/act" -Method Post -Body $okBody -ContentType "application/json" -TimeoutSec 30
    if ($ok.accepted -ne $true) { throw "proof-gated act with humanAccepted should be accepted: $($ok.reason)" }
    if ($ok.mutationApplied -ne $false) { throw "pedagogy act must remain audit-only (mutationApplied=false)" }
    "accepted=$($ok.accepted) mutationApplied=$($ok.mutationApplied) actId=$($ok.actId)"
}

Invoke-SmokeCheck "POST /api/breakthrough/demo/foundation-pour (incomplete prior silences)" {
    $body = '{"includeRequiredPhysics":false,"seedAdditionalVerifications":0}'
    $r = Invoke-RestMethod -Uri "$baseUrl/api/breakthrough/demo/foundation-pour" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 60
    if ($r.incomplete -ne $true) { throw "expected incomplete=true" }
    $hyps = @($r.hypotheses)
    if ($hyps.Count -ne 0) { throw "expected 0 hypotheses, got $($hyps.Count)" }
    if ($r.pedagogySilence -ne $true) { throw "incomplete prior must silence pedagogy" }
    if ($r.pedagogyProposedAction) { throw "incomplete prior must not propose an act" }
    "silenced=$($r.incompleteReason)"
}

Invoke-SmokeCheck "POST /api/thinking (non-mock reasoning)" -Optional {
    # ThinkingRequest binds `Mode` (enum), not chat's thinkingMode — Deep can overload Ollama/App Service (503).
    $body = '{"query":"How do I sequence a concrete pour after formwork?","mode":0}'
    $r = Invoke-RestMethod -Uri "$baseUrl/api/thinking" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 180
    if (-not $r.result) { throw "No result field in thinking response" }
    if ($r.result -match '(?i)mock|placeholder|lorem ipsum') { throw "Thinking result looks mocked" }
    "resultLength=$($r.result.Length)"
}

Invoke-SmokeCheck "POST /api/search (retrieved corpus hits)" {
    $body = @{ query = "OSHA fall protection height"; searchScope = "Internal" } | ConvertTo-Json
    $r = Invoke-RestMethod -Uri "$baseUrl/api/search" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 60
    if (-not $r.results -or $r.results.Count -lt 1) { throw "No search results returned" }
    "resultCount=$($r.results.Count)"
}

Invoke-SmokeCheck "GET /api/capabilities (feature parity matrix)" {
    $r = Invoke-RestMethod -Uri "$baseUrl/api/capabilities" -Method Get -TimeoutSec 30
    if (-not $r.features -or $r.features.Count -lt 10) { throw "Capabilities matrix missing or too shallow" }
    if (-not $r.constructionMoat) { throw "Construction moat summary missing" }
    "shippedCore=$($r.parityScore.shippedCore) corpus=$($r.corpusEntries)"
}

Invoke-SmokeCheck "POST /api/browse (live URL fetch + summarize)" -Optional {
    $body = '{"url":"https://example.com/","question":"Summarize for a contractor in one sentence."}'
    $r = Invoke-RestMethod -Uri "$baseUrl/api/browse" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 180
    if (-not $r.success) { throw "Browse failed: $($r.error)" }
    if (-not $r.summary -or $r.summary.Length -lt 10) { throw "Browse summary missing" }
    "extracted=$($r.extractedChars) summaryLength=$($r.summary.Length)"
}

Invoke-SmokeCheck "POST /api/calc (construction calculator)" {
    $body = '{"operation":"concrete_volume_cy","args":{"lengthFt":20,"widthFt":10,"depthIn":6}}'
    $r = Invoke-RestMethod -Uri "$baseUrl/api/calc" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 30
    if (-not $r.success) { throw "Calc failed: $($r.error)" }
    if ($r.value -lt 3.5 -or $r.value -gt 3.9) { throw "Unexpected CY value $($r.value)" }
    "value=$($r.value) $($r.unit)"
}

Invoke-SmokeCheck "POST /api/agent (tool loop)" -Optional {
    $body = '{"query":"How many cubic yards for a 20x10 ft slab 6 inches thick?"}'
    $r = Invoke-RestMethod -Uri "$baseUrl/api/agent" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 240
    if (-not $r.success) { throw "Agent failed: $($r.error)" }
    if (-not $r.finalAnswer) { throw "Agent finalAnswer missing" }
    if (-not $r.steps -or $r.steps.Count -lt 1) { throw "Agent steps missing" }
    "steps=$($r.steps.Count) answerLength=$($r.finalAnswer.Length)"
}

Write-Host ""
$report = [ordered]@{
    baseUrl = $baseUrl
    runAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    totalChecks = $results.Count
    failures = $failures
    warnings = $warnings
    checks = $results
}
$reportDir = Join-Path (Join-Path (Join-Path $PSScriptRoot "..") "eval") "reports"
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$reportPath = Join-Path $reportDir "prod_smoke_last_run.json"
$report | ConvertTo-Json -Depth 6 | Set-Content -Path $reportPath -Encoding utf8
Write-Host "Report written to $reportPath"

if ($failures -gt 0) {
    Write-Host "$failures critical check(s) FAILED ($warnings LLM warning(s))" -ForegroundColor Red
    exit 1
}
if ($warnings -gt 0) {
    Write-Host "Critical checks PASSED. $warnings LLM path warning(s) — identity/pour still green." -ForegroundColor Yellow
    exit 0
}
Write-Host "All checks PASSED against live production backend." -ForegroundColor Green
exit 0
