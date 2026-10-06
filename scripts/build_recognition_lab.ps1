param([switch]$SmokeTest, [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot 'tools/nuget-packages'
$env:DOTNET_NOLOGO = '1'
Set-Location -LiteralPath $projectRoot
$testProject = 'tests/Stock.Recognition.Tests/Stock.Recognition.Tests.csproj'
& dotnet restore $testProject --locked-mode -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Recognition test dependency restore failed' }
& dotnet test $testProject --no-restore -c Release -m:1 -nodeReuse:false --logger 'trx;LogFileName=recognition.trx' --results-directory artifacts/recognition-lab/tests
if ($LASTEXITCODE -ne 0) { throw 'Recognition tests failed' }
$publish = Join-Path $projectRoot 'artifacts/recognition-lab/single-file'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$appProject = 'src/Stock.RecognitionLab/Stock.RecognitionLab.csproj'
& dotnet restore $appProject -r win-x64 -p:SelfContained=$selfContained --locked-mode -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Recognition lab dependency restore failed' }
& dotnet publish $appProject -c Release --no-restore -r win-x64 --self-contained $selfContained -m:1 -nodeReuse:false -o $publish -p:PublishReadyToRun=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE -ne 0) { throw 'Recognition lab publish failed' }
$application = Join-Path $publish 'Stock.RecognitionLab.exe'
if (@(Get-ChildItem -LiteralPath $publish -Force).Count -ne 1 -or -not (Test-Path -LiteralPath $application)) {
    throw "Single-file output must contain only Stock.RecognitionLab.exe: $publish"
}
New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'dist') | Out-Null
$delivery = Join-Path $projectRoot 'dist/Stock.RecognitionLab.exe'
Copy-Item -LiteralPath $application -Destination $delivery -Force
$sha256 = (Get-FileHash -LiteralPath $delivery -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($delivery + '.sha256') -Encoding ascii -Value "$sha256  Stock.RecognitionLab.exe"
if ($SmokeTest) {
    $isolated = Join-Path $projectRoot 'artifacts/recognition-lab/isolated-single-file'
    New-Item -ItemType Directory -Force -Path $isolated | Out-Null
    Copy-Item -LiteralPath $delivery -Destination (Join-Path $isolated 'Stock.RecognitionLab.exe') -Force
    if (@(Get-ChildItem -LiteralPath $isolated -Force).Count -ne 1) { throw 'Isolated smoke directory must contain only the executable' }
    $output = Join-Path $projectRoot 'artifacts/recognition-lab/desktop-smoke'
    $process = Start-Process -FilePath (Join-Path $isolated 'Stock.RecognitionLab.exe') -ArgumentList '--smoke-test', ('"' + $output + '"') -WorkingDirectory $isolated -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Recognition lab desktop checks failed: $output/failure.txt" }
    Write-Output "Offline desktop checks: $output/desktop-results.json"
}
Write-Output "Independent single-file application: $delivery"
Write-Output "SHA-256: $sha256"
