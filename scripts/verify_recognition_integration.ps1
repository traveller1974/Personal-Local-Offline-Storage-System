param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot 'tools/nuget-packages'
$env:DOTNET_NOLOGO = '1'
Set-Location -LiteralPath $projectRoot
$payload = Join-Path $projectRoot 'artifacts/recognition-lab/single-file/Stock.RecognitionLab.exe'
if (-not (Test-Path -LiteralPath $payload)) { throw 'Run build_recognition_lab.ps1 -SmokeTest first.' }
if (-not $SkipPublish) {
    & dotnet restore src/Stock.Desktop/Stock.Desktop.csproj --locked-mode -p:NuGetAudit=false --disable-parallel -m:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Desktop dependency restore failed' }
}
foreach ($mode in @('legacy', 'embedded')) {
    $enabled = if ($mode -eq 'embedded') { 'true' } else { 'false' }
    $publish = Join-Path $projectRoot "artifacts/recognition-integration/publish-$mode"
    $output = Join-Path $projectRoot "artifacts/recognition-integration/$mode"
    if (-not $SkipPublish) {
        & dotnet publish src/Stock.Desktop/Stock.Desktop.csproj -c Release --no-restore -m:1 -nodeReuse:false -r win-x64 --self-contained true -o $publish -p:PublishReadyToRun=false -p:Version=1.1.3 "-p:UseEmbeddedRecognition=$enabled"
        if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed: $mode" }
    }
    $application = Join-Path $publish 'LocalStockManager.exe'
    $process = Start-Process -FilePath $application -ArgumentList '--recognition-integration-test', ('"' + $output + '"'), $mode -WorkingDirectory $publish -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw "Recognition integration timed out: $mode" }
        $resultPath = Join-Path $output 'integration-results.json'
        if (-not (Test-Path -LiteralPath $resultPath)) { throw "No integration result: $mode, exit code $($process.ExitCode)" }
        $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
        if ($process.ExitCode -ne 0 -or -not $result.success) { throw ($result | ConvertTo-Json -Depth 5) }
        if ($result.payloadSha256 -ne (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Embedded EXE differs from the tested standalone payload' }
        Write-Output "Recognition integration checks passed ($mode): $($result.checks.Count). Evidence: $resultPath"
    } finally { $process.Dispose() }
}
