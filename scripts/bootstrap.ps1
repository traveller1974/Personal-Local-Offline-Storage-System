param([switch]$SkipDotnet,[string]$DownloadProxy='')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot 'tools/nuget-packages'
$env:DOTNET_NOLOGO = '1'
$toolsRoot = Join-Path $projectRoot 'tools'
New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
function Fetch([string]$Url,[string]$Path,[string]$ExpectedHash) {
    if(-not(Test-Path -LiteralPath $Path)){
        $fetchArgs=@('-fL','--silent','--show-error','--connect-timeout','25','--max-time','120','-o',($Path+'.part'))
        if($DownloadProxy){$fetchArgs=@('--proxy',$DownloadProxy)+$fetchArgs}
        & curl.exe @fetchArgs $Url
        if($LASTEXITCODE -ne 0){throw "Download failed: $Url"}
        Move-Item -LiteralPath ($Path+'.part') -Destination $Path
    }
    if((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ExpectedHash){throw "Download hash mismatch: $Path"}
}
$toolLock=Get-Content -Raw (Join-Path $PSScriptRoot 'tools.lock.json') | ConvertFrom-Json
if (-not $SkipDotnet) {
    & dotnet restore (Join-Path $projectRoot 'LocalStockManager.slnx')
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed' }
}
$pythonRoot = Join-Path $toolsRoot 'python312'
if (-not (Test-Path (Join-Path $pythonRoot 'tools/python.exe'))) {
    $pythonZip = Join-Path $toolsRoot 'python-3.12.10.zip'
    Fetch $toolLock.python.url $pythonZip $toolLock.python.sha256
    Expand-Archive -LiteralPath $pythonZip -DestinationPath $pythonRoot -Force
}
$pythonExe = Join-Path $pythonRoot 'tools/python.exe'
& $pythonExe -m ensurepip
if ($LASTEXITCODE -ne 0) { throw 'Python pip bootstrap failed' }
if (-not (Test-Path (Join-Path $projectRoot 'ocr/.venv/Scripts/python.exe'))) {
    & $pythonExe -m venv (Join-Path $projectRoot 'ocr/.venv')
    if ($LASTEXITCODE -ne 0) { throw 'Python venv failed' }
}
$venvPython = Join-Path $projectRoot 'ocr/.venv/Scripts/python.exe'
& $venvPython -m pip install -r (Join-Path $projectRoot 'ocr/requirements.lock.txt')
if ($LASTEXITCODE -ne 0) { throw 'OCR dependency install failed' }
$nsisRoot = Join-Path $toolsRoot 'nsis'
if (-not (Test-Path (Join-Path $nsisRoot 'nsis-3.11/makensis.exe'))) {
    $nsisZip = Join-Path $toolsRoot 'nsis-3.11.zip'
    Fetch $toolLock.nsis.url $nsisZip $toolLock.nsis.sha256
    Expand-Archive -LiteralPath $nsisZip -DestinationPath $nsisRoot -Force
}
New-Item -ItemType Directory -Force (Join-Path $projectRoot 'ocr/models') | Out-Null
Copy-Item -Path (Join-Path $projectRoot 'ocr/.venv/Lib/site-packages/rapidocr_onnxruntime/models/*.onnx') -Destination (Join-Path $projectRoot 'ocr/models') -Force
$modelLock=Get-Content -Raw (Join-Path $projectRoot 'ocr/models/manifest.json') | ConvertFrom-Json
foreach($entry in $modelLock.sha256.PSObject.Properties){if((Get-FileHash -LiteralPath (Join-Path $projectRoot "ocr/models/$($entry.Name)") -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value){throw "Model hash mismatch: $($entry.Name)"}}
& (Join-Path $PSScriptRoot 'fetch_licenses.ps1') -DownloadProxy $DownloadProxy
Write-Output 'Bootstrap complete'
