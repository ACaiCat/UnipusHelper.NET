param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'UnipusHelper/UnipusHelper.csproj'
$destination = Join-Path $projectRoot "artifacts/$Runtime"
dotnet publish $project -c Release -r $Runtime -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Native AOT 编译失败。' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'LICENSE') -Destination $destination
& (Join-Path $PSScriptRoot 'bundle-tts.ps1') -PublishDirectory $destination
Write-Output "输出目录：$destination"
