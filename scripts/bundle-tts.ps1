param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'

if ($env:GITHUB_ACTIONS -ne 'true') {
    Write-Output '本地构建：不打包语音模型。'
    return
}

$modelName = 'vits-piper-en_US-amy-low'
$modelUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/$modelName.tar.bz2"
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$stagingDirectory = Join-Path $temporaryRoot "unipus-tts-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
$archive = Join-Path $stagingDirectory 'model.tar.bz2'
Write-Output "下载随包语音模型：$modelName"
Invoke-WebRequest -Uri $modelUrl -OutFile $archive
& tar -xf $archive -C $stagingDirectory
if ($LASTEXITCODE -ne 0) { throw '语音模型解压失败。' }

$modelDirectory = Join-Path $stagingDirectory $modelName
foreach ($name in @('en_US-amy-low.onnx', 'tokens.txt', 'espeak-ng-data')) {
    if (!(Test-Path -LiteralPath (Join-Path $modelDirectory $name))) {
        throw "语音模型缺少文件：$name"
    }
}
$ttsDirectory = Join-Path $PublishDirectory 'tts'
$destination = Join-Path $ttsDirectory $modelName
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $modelDirectory -Force |
    Copy-Item -Destination $destination -Recurse -Force
Write-Output "已打包语音模型：$destination"
