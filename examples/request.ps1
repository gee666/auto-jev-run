param([string]$BaseUrl = 'http://localhost:8765')
$body = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'request.json')
Invoke-RestMethod -Method Post -Uri ($BaseUrl.TrimEnd('/') + '/v1/systemone') -ContentType 'application/json' -Body $body -TimeoutSec 1200 | ConvertTo-Json -Depth 20
