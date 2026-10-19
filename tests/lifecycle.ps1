param([string]$BaseUrl = 'http://localhost:8775')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$projectRoot = Split-Path -Parent $PSScriptRoot
$checks = [Collections.Generic.List[object]]::new()
function Check($name, $ok, $detail) {
    if (-not $ok) { throw "FAIL: $name ($detail)" }
    $checks.Add([pscustomobject]@{check=$name;passed=$true;detail=$detail})
    Write-Output "PASS $name"
}
function Status { Invoke-RestMethod -Uri "$BaseUrl/local/status" }
function Wait-State($wanted, $seconds) {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do { $s=Status; if($s.status -eq $wanted){return $s}; Start-Sleep -Milliseconds 300 } while($watch.Elapsed.TotalSeconds -lt $seconds)
    throw "Expected $wanted, got $($s.status)"
}
$client=[Net.Http.HttpClient]::new()
$client.Timeout=[TimeSpan]::FromSeconds(180)
$body='{"model":"jev-latest","state":"Python: a = [1, 2]; b = a; b.append(3)","questions":{"shared":{"type":"noul","instructions":"Does a now contain three elements?"}}}'
function Begin-Inference {
    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,"$BaseUrl/v1/systemone")
    $request.Content=[Net.Http.StringContent]::new($body,[Text.Encoding]::UTF8,'application/json')
    return $client.SendAsync($request)
}
function Finish-Inference($task) {
    $response=$task.GetAwaiter().GetResult()
    try { $response.EnsureSuccessStatusCode() | Out-Null; return ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json) } finally { $response.Dispose() }
}
try {
    $s=Status
    Check 'starts unloaded with no inference process' ($s.status -eq 'unloaded' -and $null -eq $s.backendPid) $s.status
    Check 'short test timeout configured' ($s.idleUnloadSeconds -eq 15) $s.idleUnloadSeconds
    $s=Invoke-RestMethod -Uri "$BaseUrl/health"
    Check 'health does not wake model' ($s.status -eq 'unloaded') $s.status
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $first=Begin-Inference
    $second=Begin-Inference
    $s=Wait-State 'loading' 15
    $loadingPid=$s.backendPid
    $r1=Finish-Inference $first
    $r2=Finish-Inference $second
    $coldSeconds=$watch.Elapsed.TotalSeconds
    $s=Status
    $warmPid=$s.backendPid
    Check 'concurrent cold requests share one model load' ($s.status -eq 'ready' -and $warmPid -eq $loadingPid -and $r1.answers.shared.noul -gt .9 -and $r2.answers.shared.noul -gt .9) ("{0:N2}s; PID {1}" -f $coldSeconds,$warmPid)
    Start-Sleep -Seconds 10
    $watch.Restart()
    $r3=Finish-Inference (Begin-Inference)
    Check 'batch reuses loaded process' ((Status).backendPid -eq $warmPid) ("{0:N2}s" -f $watch.Elapsed.TotalSeconds)
    Start-Sleep -Seconds 8
    Check 'recent request resets idle deadline' ((Status).status -eq 'ready') 'Ready beyond the original idle deadline'
    $s=Wait-State 'unloaded' 15
    Check 'idle unload releases inference process' ($null -eq $s.backendPid -and $null -eq (Get-Process -Id $warmPid -ErrorAction SilentlyContinue)) $s.status
    1..3 | ForEach-Object { Invoke-RestMethod -Uri "$BaseUrl/health" | Out-Null; Invoke-RestMethod -Uri "$BaseUrl/" | Out-Null }
    Check 'health and web page do not reload model' ((Status).status -eq 'unloaded') 'No backend process'
    $watch.Restart()
    $r4=Finish-Inference (Begin-Inference)
    $s=Status
    Check 'request reloads after idle unload' ($s.status -eq 'ready' -and $s.backendPid -ne $warmPid -and $r4.answers.shared.noul -gt .9) ("{0:N2}s; PID {1}" -f $watch.Elapsed.TotalSeconds,$s.backendPid)
    $token=[IO.File]::ReadAllText((Join-Path $projectRoot 'state/control.token'))
    Invoke-RestMethod -Uri "$BaseUrl/local/unload" -Method Post -Headers @{'X-AutoJev-Control'=$token} | Out-Null
    Check 'manual unload leaves API available' ((Status).status -eq 'unloaded') 'API available'
    $summary=[pscustomobject]@{date=(Get-Date -Format o);baseUrl=$BaseUrl;checks=$checks}
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $projectRoot 'reports/lifecycle-validation.json') -Encoding UTF8
} finally { $client.Dispose() }

