param([switch]$Bootstrap,[string]$DownloadProxy='',[switch]$SkipOcrBuild)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME=Join-Path $projectRoot 'tools/dotnet-home'
$env:NUGET_PACKAGES=Join-Path $projectRoot 'tools/nuget-packages'
$env:DOTNET_NOLOGO='1'
$env:PYINSTALLER_CONFIG_DIR=Join-Path $projectRoot 'tools/pyinstaller-cache'
Set-Location -LiteralPath $projectRoot
if($Bootstrap){ & (Join-Path $PSScriptRoot 'bootstrap.ps1') -DownloadProxy $DownloadProxy }
$python=Join-Path $projectRoot 'ocr/.venv/Scripts/python.exe'
$nsis=Join-Path $projectRoot 'tools/nsis/nsis-3.11/makensis.exe'
foreach($tool in @($python,$nsis)){if(-not(Test-Path -LiteralPath $tool)){throw "Missing build tool: $tool. Run build.ps1 -Bootstrap first."}}
& dotnet restore LocalStockManager.slnx --locked-mode -p:NuGetAudit=false --disable-parallel -m:1 -nodeReuse:false
if($LASTEXITCODE -ne 0){throw 'Locked NuGet restore failed'}
& dotnet test tests/Stock.Tests/Stock.Tests.csproj --no-restore -c Release -m:1 -nodeReuse:false --logger 'trx;LogFileName=core.trx' --results-directory artifacts/tests
if($LASTEXITCODE -ne 0){throw 'Business tests failed'}
& $python ocr/smoke_test.py
if($LASTEXITCODE -ne 0){throw 'Source OCR tests failed'}
& $python ocr/protocol_tests.py
if($LASTEXITCODE -ne 0){throw 'OCR error protocol tests failed'}
if(-not $SkipOcrBuild){ & $python -m PyInstaller --noconfirm ocr/worker.spec --distpath artifacts/ocr-worker --workpath artifacts/pyinstaller }
if($LASTEXITCODE -ne 0){throw 'OCR worker packaging failed'}
& $python ocr/smoke_test.py --worker artifacts/ocr-worker/StockOcr/StockOcr.exe
if($LASTEXITCODE -ne 0){throw 'Bundled OCR tests failed'}
$publish=Join-Path $projectRoot 'artifacts/publish-v1.1.1'
& dotnet publish src/Stock.Desktop/Stock.Desktop.csproj -c Release --no-restore -m:1 -nodeReuse:false -r win-x64 --self-contained true -o $publish -p:PublishReadyToRun=false
if($LASTEXITCODE -ne 0){throw 'Desktop publish failed'}
New-Item -ItemType Directory -Force -Path (Join-Path $publish 'Ocr'),(Join-Path $publish 'Samples') | Out-Null
Copy-Item -Path artifacts/ocr-worker/StockOcr/* -Destination (Join-Path $publish 'Ocr') -Recurse -Force
Copy-Item -Path artifacts/ocr-worker/StockOcr/_internal/*140*.dll -Destination $publish -Force
Copy-Item -LiteralPath 'artifacts/ocr/中文 含空格样本/打印货单.png' -Destination (Join-Path $publish 'Samples/示例货单.png') -Force
# FFmpeg is unnecessary for still images and the explicitly selected DirectShow camera.
# Delete only known optional files under the verified project publish folder.
Get-ChildItem -LiteralPath $publish -Filter 'opencv_videoio_ffmpeg*.dll' -File -Recurse | ForEach-Object {
 $candidate=[IO.Path]::GetFullPath($_.FullName)
 if(-not $candidate.StartsWith($publish+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe publish path'}
 Remove-Item -LiteralPath $candidate
}
& $python scripts/collect_licenses.py
if($LASTEXITCODE -ne 0){throw 'License collection failed'}
Copy-Item -LiteralPath docs/使用说明.md,docs/已知限制.md,THIRD-PARTY-NOTICES.md -Destination $publish -Force
Copy-Item -LiteralPath licenses -Destination $publish -Recurse -Force
New-Item -ItemType Directory -Force dist | Out-Null
$installer=Join-Path $projectRoot 'dist/LocalStockManager-1.1.1-win-x64-Setup.exe'
& $nsis '/INPUTCHARSET' 'UTF8' "/DPUBLISH_DIR=$publish" "/DOUTPUT_FILE=$installer" '/DAPP_VERSION=1.1.1' installer/installer.nsi
if($LASTEXITCODE -ne 0){throw 'NSIS compilation failed'}
$hash=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($installer+'.sha256') -Encoding ascii -Value "$hash  $([IO.Path]::GetFileName($installer))"
Copy-Item -LiteralPath docs/使用说明.md,docs/已知限制.md,THIRD-PARTY-NOTICES.md -Destination dist -Force
Write-Output "Installer: $installer"
Write-Output "SHA-256: $hash"
