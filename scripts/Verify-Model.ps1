$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$modelDir = Join-Path $projectRoot 'models\autojev-27b'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $modelDir 'provenance.json') | ConvertFrom-Json
$entry = $manifest.files | Where-Object rfilename -eq 'autojev-Q4_K_M.gguf'
$modelPath = Join-Path $modelDir $entry.rfilename
if ((Get-Item -LiteralPath $modelPath).Length -ne $entry.size) { throw 'Model size mismatch.' }
$actual = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash
if ($actual -ne $entry.lfs.sha256) { throw 'Model SHA-256 mismatch.' }
Write-Output ('Verified model: ' + $actual)

$visionManifest = Get-Content -Raw -LiteralPath (Join-Path $modelDir 'vision-provenance.json') | ConvertFrom-Json
$visionPath = Join-Path $modelDir $visionManifest.file
if (Test-Path -LiteralPath $visionPath) {
    if ((Get-Item -LiteralPath $visionPath).Length -ne $visionManifest.size) { throw 'Vision projector size mismatch.' }
    $visionHash = (Get-FileHash -LiteralPath $visionPath -Algorithm SHA256).Hash
    if ($visionHash -ne $visionManifest.sha256) { throw 'Vision projector SHA-256 mismatch.' }
    Write-Output ('Verified vision projector: ' + $visionHash)
}
