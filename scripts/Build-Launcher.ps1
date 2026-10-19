param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build\dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.build\nuget'
$env:TEMP = Join-Path $projectRoot '.build\temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
dotnet publish (Join-Path $projectRoot 'src\AutoJev.Local\AutoJev.Local.csproj') -c $Configuration -r win-x64 --self-contained true -o (Join-Path $projectRoot 'app')
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\AutoJev.Local\index.html') -Destination (Join-Path $projectRoot 'app\index.html')

Copy-Item -LiteralPath (Join-Path $projectRoot 'src\AutoJev.Local\usage.html') -Destination (Join-Path $projectRoot 'app\usage.html')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\api-agent.md') -Destination (Join-Path $projectRoot 'app\api.md')
