param([string]$ApplicationPath='',[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
if(-not $ApplicationPath){$ApplicationPath=Join-Path $projectRoot 'artifacts/publish-v1.1/LocalStockManager.exe'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $projectRoot 'artifacts/desktop-smoke'}
if(-not(Test-Path -LiteralPath $ApplicationPath)){throw 'Build the self-contained app first.'}
$process=Start-Process -FilePath $ApplicationPath -ArgumentList '--smoke-test',('"'+[IO.Path]::GetFullPath($OutputDirectory)+'"') -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru -Wait
$resultPath=Join-Path $OutputDirectory 'desktop-results.json'
if(-not(Test-Path -LiteralPath $resultPath)){throw "No desktop test result. Exit code $($process.ExitCode)"}
$result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if($process.ExitCode -ne 0 -or -not $result.success){throw ($result | ConvertTo-Json -Depth 4)}
Write-Output "Desktop integration checks passed: $($result.checks.Count)"
Write-Output "Evidence: $resultPath"
