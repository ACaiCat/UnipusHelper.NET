param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][ValidatePattern('^v[A-Za-z0-9._+-]+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $projectRoot "artifacts/$Runtime"
$distDirectory = Join-Path $projectRoot 'dist'
$filename = if ($Runtime -eq 'win-x64') { 'UnipusHelp.exe' } else { 'UnipusHelp' }
if (!(Test-Path -LiteralPath (Join-Path $publishDirectory $filename))) {
    throw "Publish output missing: $publishDirectory/$filename"
}

$bundleName = "UnipusHelp-$Version-$Runtime"
$stagingRoot = Join-Path $projectRoot "artifacts/package-$([Guid]::NewGuid().ToString('N'))"
$bundleDirectory = Join-Path $stagingRoot $bundleName
New-Item -ItemType Directory -Path $bundleDirectory, $distDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $publishDirectory -Force |
    Where-Object { $_.Name -notmatch '\.(pdb|dbg|dSYM)$' } |
    Copy-Item -Destination $bundleDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'LICENSE') -Destination $bundleDirectory -Force

if ($Runtime -eq 'win-x64') {
    $archive = Join-Path $distDirectory "$bundleName.zip"
    Compress-Archive -LiteralPath $bundleDirectory -DestinationPath $archive -Force
} else {
    & chmod +x (Join-Path $bundleDirectory $filename)
    if ($LASTEXITCODE -ne 0) { throw 'Could not set executable permission' }
    $archive = Join-Path $distDirectory "$bundleName.tar.gz"
    & tar -czf $archive -C $stagingRoot $bundleName
    if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed' }
}
Write-Output "Created $archive"
