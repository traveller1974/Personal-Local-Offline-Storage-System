param([switch]$Bootstrap,[string]$DownloadProxy='',[switch]$SkipOcrBuild,[switch]$UseEmbeddedRecognition,[ValidatePattern('^\d+\.\d+\.\d+$')][string]$AppVersion='1.2.0')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME=Join-Path $projectRoot 'tools/dotnet-home'
$env:NUGET_PACKAGES=Join-Path $projectRoot 'tools/nuget-packages'
$env:DOTNET_NOLOGO='1'
$env:PYINSTALLER_CONFIG_DIR=Join-Path $projectRoot 'tools/pyinstaller-cache'
Set-Location -LiteralPath $projectRoot
if($Bootstrap){ & (Join-Path $PSScriptRoot 'bootstrap.ps1') -DownloadProxy $DownloadProxy }
# An enabled upgrade embeds the exact verified standalone EXE without rebuilding it.
if($UseEmbeddedRecognition){
 $workerPayload=Join-Path $projectRoot 'dist/Stock.RecognitionLab.exe'
 if(-not(Test-Path -LiteralPath $workerPayload)){throw 'Please build and verify the standalone recognition EXE before generating an enabled upgrade.'}
 $payloadChecksum=$workerPayload+'.sha256'
 if(-not(Test-Path -LiteralPath $payloadChecksum)){throw 'The reviewed recognition EXE checksum is missing.'}
 $reviewedPayloadHash=(Get-Content -Raw -LiteralPath $payloadChecksum).Trim().Split(' ',[StringSplitOptions]::RemoveEmptyEntries)[0].ToLowerInvariant()
 if($reviewedPayloadHash -notmatch '^[a-f0-9]{64}$' -or (Get-FileHash -LiteralPath $workerPayload -Algorithm SHA256).Hash.ToLowerInvariant() -ne $reviewedPayloadHash){throw 'The recognition EXE differs from its reviewed checksum.'}
}else{
 & (Join-Path $PSScriptRoot 'build_recognition_lab.ps1') -SmokeTest
 $workerPayload=Join-Path $projectRoot 'artifacts/recognition-lab/single-file/Stock.RecognitionLab.exe'
}
$recognitionEnabled=if($UseEmbeddedRecognition){'true'}else{'false'}
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
$publish=Join-Path $projectRoot "artifacts/publish-$AppVersion-$recognitionEnabled"
& dotnet publish src/Stock.Desktop/Stock.Desktop.csproj -c Release --no-restore -m:1 -nodeReuse:false -r win-x64 --self-contained true -o $publish -p:PublishReadyToRun=false "-p:Version=$AppVersion" "-p:UseEmbeddedRecognition=$recognitionEnabled" "-p:RecognitionWorkerExe=$workerPayload"
if($LASTEXITCODE -ne 0){throw 'Desktop publish failed'}
$integrationOutput=Join-Path $projectRoot "artifacts/recognition-integration/build-$AppVersion-$recognitionEnabled"
$backend=if($UseEmbeddedRecognition){'embedded'}else{'legacy'}
$integrationProcess=Start-Process -FilePath (Join-Path $publish 'LocalStockManager.exe') -ArgumentList '--recognition-integration-test',('"'+$integrationOutput+'"'),$backend -WorkingDirectory $publish -WindowStyle Hidden -PassThru
try{
 if(-not $integrationProcess.WaitForExit(60000)){$integrationProcess.Kill($true);throw 'Recognition integration timed out'}
 $integrationResult=Get-Content -Raw -LiteralPath (Join-Path $integrationOutput 'integration-results.json') | ConvertFrom-Json
 if($integrationProcess.ExitCode -ne 0 -or -not $integrationResult.success){throw 'Recognition integration failed'}
 if($integrationResult.payloadSha256 -ne (Get-FileHash -LiteralPath $workerPayload -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'Embedded worker does not match the reviewed EXE'}
}finally{$integrationProcess.Dispose()}
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
Copy-Item -LiteralPath docs/使用说明.md,docs/已知限制.md,docs/千问识别独立测试.md,docs/发布说明-v1.2.0.md,docs/识图自动填单架构-v1.2.md,THIRD-PARTY-NOTICES.md -Destination $publish -Force
Copy-Item -LiteralPath licenses -Destination $publish -Recurse -Force
New-Item -ItemType Directory -Force dist | Out-Null
$installer=Join-Path $projectRoot "dist/LocalStockManager-$AppVersion-win-x64-Setup.exe"
& $nsis '/INPUTCHARSET' 'UTF8' "/DPUBLISH_DIR=$publish" "/DOUTPUT_FILE=$installer" "/DAPP_VERSION=$AppVersion" installer/installer.nsi
if($LASTEXITCODE -ne 0){throw 'NSIS compilation failed'}
$hash=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($installer+'.sha256') -Encoding ascii -Value "$hash  $([IO.Path]::GetFileName($installer))"
 $manifestDirectory=Join-Path $projectRoot "artifacts/v$AppVersion"
 New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null
 [ordered]@{version=$AppVersion;embeddedRecognitionEnabled=[bool]$UseEmbeddedRecognition;recognitionPayloadSha256=(Get-FileHash -LiteralPath $workerPayload -Algorithm SHA256).Hash.ToLowerInvariant();installerSha256=$hash;installer=$installer;publish=$publish;integrationChecks=$integrationResult.checks.Count;databaseSchema=2} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $manifestDirectory 'release-manifest.json') -Encoding UTF8
Copy-Item -LiteralPath docs/使用说明.md,docs/已知限制.md,THIRD-PARTY-NOTICES.md -Destination dist -Force
Write-Output "Installer: $installer"
Write-Output "SHA-256: $hash"
Write-Output "Embedded recognition enabled: $recognitionEnabled"
Write-Output "Embedded recognition payload SHA-256: $((Get-FileHash -LiteralPath $workerPayload -Algorithm SHA256).Hash.ToLowerInvariant())"
