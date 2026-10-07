param([string]$BaselinePublish='', [ValidatePattern('^\d+\.\d+\.\d+$')][string]$AppVersion='1.3.0')
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME=Join-Path $projectRoot 'tools/dotnet-home'
$env:NUGET_PACKAGES=Join-Path $projectRoot 'tools/nuget-packages'
$env:DOTNET_NOLOGO='1'
if(-not $BaselinePublish){$BaselinePublish=Join-Path $projectRoot 'artifacts/publish-1.2.1-true'}
$baseline=[IO.Path]::GetFullPath($BaselinePublish)
foreach($relative in @('LocalStockManager.dll','Ocr/StockOcr.exe','Samples/示例货单.png')){
 if(-not(Test-Path -LiteralPath (Join-Path $baseline $relative))){throw "Verified 1.2.1 publish directory required: $relative"}
}
if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $baseline 'LocalStockManager.dll')).ProductVersion.Split('+')[0] -ne '1.2.1'){throw 'Offline components must come from the accepted 1.2.1 application'}
$nsis=Join-Path $projectRoot 'tools/nsis/nsis-3.11/makensis.exe'
if(-not(Test-Path -LiteralPath $nsis)){throw 'Download the locked NSIS tool before packaging'}
& dotnet restore LocalStockManager.slnx --locked-mode -p:NuGetAudit=false --disable-parallel -m:1 -nodeReuse:false
if($LASTEXITCODE -ne 0){throw 'Locked restore failed'}
$evidence=Join-Path $projectRoot "artifacts/v$AppVersion"
& dotnet test tests/Stock.Tests/Stock.Tests.csproj --no-restore -c Release -m:1 -nodeReuse:false --logger 'trx;LogFileName=core.trx' --results-directory (Join-Path $evidence 'tests')
if($LASTEXITCODE -ne 0){throw 'Business tests failed'}
& (Join-Path $PSScriptRoot 'build_recognition_lab.ps1') -SmokeTest
$worker=Join-Path $projectRoot 'dist/Stock.RecognitionLab.exe'
$workerHash=(Get-FileHash -LiteralPath $worker -Algorithm SHA256).Hash.ToLowerInvariant()
if((Get-Content -Raw -LiteralPath ($worker+'.sha256')).Trim().Split(' ')[0] -ne $workerHash){throw 'Recognition worker checksum mismatch'}
$publish=Join-Path $projectRoot "artifacts/publish-$AppVersion-true"
& dotnet publish src/Stock.Desktop/Stock.Desktop.csproj -c Release --no-restore -m:1 -nodeReuse:false -r win-x64 --self-contained true -o $publish -p:PublishReadyToRun=false "-p:Version=$AppVersion" -p:UseEmbeddedRecognition=true "-p:RecognitionWorkerExe=$worker"
if($LASTEXITCODE -ne 0){throw 'Desktop publish failed'}
foreach($directory in @('Ocr','Samples')){Copy-Item -LiteralPath (Join-Path $baseline $directory) -Destination $publish -Recurse -Force}
Get-ChildItem -LiteralPath $baseline -Filter '*140*.dll' -File | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $publish -Force}
# Match the normal release build: still images and DirectShow do not need FFmpeg.
Get-ChildItem -LiteralPath $publish -Filter 'opencv_videoio_ffmpeg*.dll' -File -Recurse | ForEach-Object {
 $candidate=[IO.Path]::GetFullPath($_.FullName)
 if(-not $candidate.StartsWith($publish+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe optional component path'}
 Remove-Item -LiteralPath $candidate
}
$hashes=@()
foreach($directory in @('Ocr','Samples')){
 foreach($file in Get-ChildItem -LiteralPath (Join-Path $baseline $directory) -File -Recurse){
  $relative=[IO.Path]::GetRelativePath($baseline,$file.FullName)
  $sourceHash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  $targetHash=(Get-FileHash -LiteralPath (Join-Path $publish $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
  if($sourceHash -ne $targetHash){throw "Copied component differs from accepted 1.2.1: $relative"}
  $hashes+=[ordered]@{path=$relative;sha256=$sourceHash}
 }
}
[ordered]@{sourceVersion='1.2.1';targetVersion=$AppVersion;verifiedOcrFiles=@($hashes|Where-Object {$_.path.StartsWith('Ocr')}).Count;allHashesMatch=$true;newDependenciesIntroduced=$false;files=$hashes} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'offline-component-reuse-private.json') -Encoding UTF8
$notes=Join-Path $projectRoot "docs/发布说明-v$AppVersion.md"
Copy-Item -LiteralPath docs/使用说明.md,docs/已知限制.md,docs/千问识别独立测试.md,$notes,docs/识图自动填单架构-v1.2.md,THIRD-PARTY-NOTICES.md -Destination $publish -Force
Copy-Item -LiteralPath licenses -Destination $publish -Recurse -Force
& (Join-Path $PSScriptRoot 'verify.ps1') -ApplicationPath (Join-Path $publish 'LocalStockManager.exe') -OutputDirectory (Join-Path $evidence 'desktop') -RecognitionIntegration
$integration=Get-Content -Raw -LiteralPath (Join-Path $evidence 'desktop/recognition-integration/integration-results.json') | ConvertFrom-Json
if($integration.payloadSha256 -ne $workerHash){throw 'Embedded recognition differs from the standalone executable'}
$installer=Join-Path $projectRoot "dist/LocalStockManager-$AppVersion-win-x64-Setup.exe"
& $nsis '/INPUTCHARSET' 'UTF8' "/DPUBLISH_DIR=$publish" "/DOUTPUT_FILE=$installer" "/DAPP_VERSION=$AppVersion" installer/installer.nsi
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
$installerHash=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($installer+'.sha256') -Encoding ascii -Value "$installerHash  $([IO.Path]::GetFileName($installer))"
[ordered]@{version=$AppVersion;embeddedRecognitionEnabled=$true;recognitionPayloadSha256=$workerHash;installerSha256=$installerHash;installerBytes=(Get-Item -LiteralPath $installer).Length;installer=$installer;publish=$publish;integrationChecks=$integration.checks.Count;databaseSchema=2;offlineOcrReusedFrom='1.2.1';realModelRequests=0;firstUseDefaultProvider='Qwen';existingProviderSelectionPreserved=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'release-manifest.json') -Encoding UTF8
Copy-Item -LiteralPath docs/使用说明.md,docs/已知限制.md,THIRD-PARTY-NOTICES.md,$notes -Destination dist -Force
Write-Output "Installer: $installer"
Write-Output "SHA-256: $installerHash"
