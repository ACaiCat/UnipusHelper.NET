$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $projectRoot 'UnipusHelper.Tests/UnipusHelper.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
