param([string]$ApplicationPath='',[string]$OutputDirectory='',[switch]$RecognitionIntegration)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
if(-not $ApplicationPath){$ApplicationPath=Join-Path $projectRoot 'artifacts/publish-1.2.0-true/LocalStockManager.exe'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $projectRoot 'artifacts/desktop-smoke'}
if(-not(Test-Path -LiteralPath $ApplicationPath)){throw 'Build the self-contained app first.'}
$process=Start-Process -FilePath $ApplicationPath -ArgumentList '--smoke-test',('"'+[IO.Path]::GetFullPath($OutputDirectory)+'"') -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru -Wait
$resultPath=Join-Path $OutputDirectory 'desktop-results.json'
if(-not(Test-Path -LiteralPath $resultPath)){throw "No desktop test result. Exit code $($process.ExitCode)"}
$result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if($process.ExitCode -ne 0 -or -not $result.success){throw ($result | ConvertTo-Json -Depth 4)}
Write-Output "Desktop integration checks passed: $($result.checks.Count)"
Write-Output "Evidence: $resultPath"
if($RecognitionIntegration){
 $configurationPath=Join-Path (Split-Path $ApplicationPath -Parent) 'LocalStockManager.runtimeconfig.json'
 $configuration=Get-Content -Raw -LiteralPath $configurationPath | ConvertFrom-Json
 $embedded=$configuration.runtimeOptions.configProperties.'LocalStockManager.UseEmbeddedRecognition'
 if($null -eq $embedded){throw 'The selected app does not declare a recognition backend; only run this option on the upgraded app.'}
 $backend=if($embedded){'embedded'}else{'legacy'}
 $integrationOutput=Join-Path $OutputDirectory 'recognition-integration'
 $integration=Start-Process -FilePath $ApplicationPath -ArgumentList '--recognition-integration-test',('"'+[IO.Path]::GetFullPath($integrationOutput)+'"'),$backend -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
 try{
  if(-not $integration.WaitForExit(60000)){$integration.Kill($true);throw 'Installed recognition integration timed out'}
  $integrationResult=Get-Content -Raw -LiteralPath (Join-Path $integrationOutput 'integration-results.json') | ConvertFrom-Json
  if($integration.ExitCode -ne 0 -or -not $integrationResult.success){throw ($integrationResult | ConvertTo-Json -Depth 4)}
  Write-Output "Recognition integration checks passed ($backend): $($integrationResult.checks.Count)"
 }finally{$integration.Dispose()}
}
